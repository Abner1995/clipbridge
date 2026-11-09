#!/usr/bin/env python3
r"""CreatFlow 开发启动器 —— 一条命令起对的东西（Windows / Linux / macOS）。

联网版：起服务端（http://localhost:5079）+ 客户端（NewApp），客户端连本机服务端。
单机版：只起客户端（本地 SQLite，无服务端、无登录）。

版本判定与 build-newapp.ps1 同一口径：读
  CreatFlow/src/CreatFlow.NewApp/CreatFlow.NewApp.csproj 的 <DefineConstants>
含 CREATFLOW_STANDALONE → 单机版，否则联网版。显式 --edition 与工程不符时直接报错
（避免拿单机分支的构建去配联网版的启动方式，反之亦然）。

常用：
  scripts\dev-online.cmd                    # Windows：联网版（= --edition online）
  scripts\dev-standalone.cmd                # Windows：单机版（= --edition standalone）
  python scripts/dev.py                     # 自动识别 + 构建 + 启动
  python scripts/dev.py --no-build          # 跳过构建（秒开；小心跑到旧二进制）
  python scripts/dev.py --server-only       # 只起服务端（联网版调试）
  python scripts/dev.py --client-only       # 只起客户端
  python scripts/dev.py --dry-run           # 只打印计划，不构建不启动
  python scripts/dev.py -- --selftest       # -- 之后的参数原样透传给客户端

⚠️ 版本是编译期开关（csproj 的 DefineConstants），不是运行时选项：同一棵工作树只能
   构建它所在分支的版本。两个 .cmd 各自锁死版本，在不对应的分支上运行会直接报错并
   提示该切哪个分支（不会静默起错版本）。

约定：
  · 不经过 `dotnet run`：直接跑 Debug 产物，cwd = 工程目录（服务端内容根与 VS 一致）。
  · 联网版发现 5079 已在监听 → 认为服务端已在跑：跳过服务端构建/启动，只构建并启动客户端
    （构建运行中的服务端会撞 MSB3021 锁 dll；要重启服务端就先关掉它再跑本脚本）。
  · 任一进程退出 → 停掉其余进程；Ctrl+C 一样整组收尾。
  · 客户端 GUI 只在 Windows 上完整可用（WebView2 / NAudio）；Linux/macOS 主要用于起服务端。
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CF = ROOT / "CreatFlow"
CLIENT_DIR = CF / "src" / "CreatFlow.NewApp"
SERVER_DIR = CF / "src" / "CreatFlow.Server"
NEWAPP_CSPROJ = CLIENT_DIR / "CreatFlow.NewApp.csproj"
SERVER_CSPROJ = SERVER_DIR / "CreatFlow.Server.csproj"

PORT = 5079
BASE_URL = f"http://localhost:{PORT}"
EDITION_NAMES = {"standalone": "单机版", "online": "联网版"}

# Serilog 控制台主题可能带 ANSI 颜色；管道里显示成乱码，统一剥掉
ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]")


# ---------------------------------------------------------------- 版本判定

def detect_edition() -> str | None:
    """按 NewApp.csproj 的编译期常量判定版本；找不到工程返回 None。"""
    if not NEWAPP_CSPROJ.is_file():
        print(f"找不到 {NEWAPP_CSPROJ} —— 请在仓库内的 scripts/dev.py 运行。")
        return None
    text = NEWAPP_CSPROJ.read_text(encoding="utf-8-sig")
    # 只认 <DefineConstants> 里的常量，注释里提到不算（build-newapp.ps1 同口径）
    m = re.search(r"<DefineConstants>([^<]*)</DefineConstants>", text)
    return "standalone" if "CREATFLOW_STANDALONE" in (m.group(1) if m else "") else "online"


# ---------------------------------------------------------------- 构建产物

def find_output(proj_dir: Path, cfg: str, file_name: str) -> Path | None:
    """在 bin/<cfg>/ 下找产物（含 RID 子目录），取最新的一份。"""
    base = proj_dir / "bin" / cfg
    if not base.is_dir():
        return None
    hits = [p for p in base.rglob(file_name) if p.is_file()]
    if not hits:
        return None
    return max(hits, key=lambda p: p.stat().st_mtime)


def run_builds(projects: list[Path], cfg: str) -> bool:
    for proj in projects:
        print(f"\n== 构建 {proj.relative_to(ROOT)} ({cfg}) ==")
        rc = subprocess.call(
            ["dotnet", "build", str(proj), "-c", cfg, "--nologo", "-v", "minimal"],
            cwd=str(CF),
        )
        if rc != 0:
            print(f"\n构建失败（退出码 {rc}）。")
            print("提示：服务端/客户端还在跑时 dll 会被锁住（MSB3021）——"
                  "先关掉它们再构建，或加 --no-build。")
            return False
    return True


# ---------------------------------------------------------------- 进程与端口

def port_open(host: str, port: int) -> bool:
    try:
        with socket.create_connection((host, port), timeout=0.25):
            return True
    except OSError:
        return False


def any_local_port_open(port: int) -> bool:
    return port_open("127.0.0.1", port) or port_open("::1", port)


def looks_like_creatflow(port: int) -> bool:
    """5079 上的东西是不是 CreatFlow 服务端（/api/config/public 免签名）。"""
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/config/public",
                                    timeout=2.0) as resp:
            return resp.status == 200
    except Exception:
        return False


def spawn(label: str, cmd: list[str], cwd: Path, env: dict[str, str]) -> subprocess.Popen:
    kwargs: dict = {}
    if os.name == "nt":
        # 子进程独立进程组：Ctrl+C 只到启动器，由启动器统一收尾（taskkill /T）
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    print(f"  启动 {label}: {' '.join(cmd)}")
    p = subprocess.Popen(
        cmd,
        cwd=str(cwd),
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        bufsize=0,
        **kwargs,
    )
    threading.Thread(target=_pump, args=(p, label), daemon=True).start()
    return p


def _decode(raw: bytes) -> str:
    """子进程编码不统一（.NET 控制台在中文 Windows 上走 GBK，有些日志走 UTF-8）：
    逐行先试 UTF-8，失败退回 GBK，最后 latin-1 兜底。"""
    for enc in ("utf-8", "gbk", "latin-1"):
        try:
            return raw.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", errors="replace")


def _pump(p: subprocess.Popen, label: str) -> None:
    try:
        assert p.stdout is not None
        for raw in p.stdout:
            line = ANSI_RE.sub("", _decode(raw))
            sys.stdout.write(f"[{label}]\t{line}")
            sys.stdout.flush()
    except Exception:
        pass


def stop(p: subprocess.Popen) -> None:
    """整组停掉（含子进程）：Windows 用 taskkill /T，POSIX 用进程组信号。"""
    if p.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            p.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        return
    try:
        os.killpg(os.getpgid(p.pid), signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        p.wait(timeout=5)
        return
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(os.getpgid(p.pid), signal.SIGKILL)
    except ProcessLookupError:
        pass
    try:
        p.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass


def wait_ready(server: subprocess.Popen, timeout: float) -> bool:
    """等服务端开始监听（Kestrel 在迁移/种子之后才 bind，端口打开 = 就绪）。"""
    started = time.time()
    while time.time() - started < timeout:
        rc = server.poll()
        if rc is not None:
            print(f"\n服务端在就绪前退出（退出码 {rc}）。常见原因：MySQL 没起来 / 端口被占。")
            return False
        if any_local_port_open(PORT):
            print(f"服务端就绪（{time.time() - started:.1f}s）")
            return True
        time.sleep(0.3)
    print(f"⚠ 等待服务端就绪超时（{timeout:.0f}s），继续启动客户端（看上面的 [server] 日志）")
    return True


# ---------------------------------------------------------------- 主流程

def main(argv: list[str]) -> int:
    # `--` 之后 = 客户端参数（例如 --selftest / --smoke）
    client_args: list[str] = []
    if "--" in argv:
        i = argv.index("--")
        argv, client_args = argv[:i], argv[i + 1:]

    ap = argparse.ArgumentParser(
        description="CreatFlow 开发启动器：按工程版本自动启动服务端/客户端。",
        epilog="`-- 之后的参数原样传给客户端`，例如：python scripts/dev.py -- --selftest",
    )
    ap.add_argument("--edition", choices=["auto", "standalone", "online"], default="auto",
                    help="版本（默认 auto：按 NewApp.csproj 的 CREATFLOW_STANDALONE 判定）")
    ap.add_argument("--server-only", action="store_true", help="只启动服务端（不校验客户端版本）")
    ap.add_argument("--client-only", action="store_true", help="只启动客户端（不构建/启动服务端）")
    ap.add_argument("--no-build", action="store_true", help="跳过构建")
    ap.add_argument("--no-wait", action="store_true", help="启动服务端后不等就绪，直接起客户端")
    ap.add_argument("--wait-seconds", type=float, default=90.0, help="等服务端就绪的上限（默认 90s）")
    ap.add_argument("-c", "--configuration", default="Debug", help="构建配置（默认 Debug）")
    ap.add_argument("--dry-run", action="store_true", help="只打印启动计划")
    args = ap.parse_args(argv)

    if args.server_only and args.client_only:
        ap.error("--server-only 与 --client-only 不能同时使用")

    detected = detect_edition()
    if detected is None:
        return 1
    edition = detected if args.edition == "auto" else args.edition

    start_client = not args.server_only
    start_server = args.server_only or (not args.client_only and edition == "online")

    # --server-only 与客户端版本无关（单机分支上也能单独起服务端调接口）
    if start_client and edition != detected:
        target = "refactor/semi-ui 或 master" if edition == "online" else "edition/standalone"
        print(f"版本不符：要起{EDITION_NAMES[edition]}，但当前分支的 NewApp.csproj 是"
              f"{EDITION_NAMES[detected]}（{'含' if detected == 'standalone' else '不含'} CREATFLOW_STANDALONE）。")
        print(f"版本是编译期开关，请先切到 {target} 再运行。")
        return 2

    cfg = args.configuration
    port_busy = any_local_port_open(PORT)

    reuse_server = False
    if start_server and port_busy:
        reuse_server = True
        start_server = False

    local_server = start_server or port_busy

    print("CreatFlow 开发启动器")
    print(f"  仓库    {ROOT}")
    print(f"  版本    {EDITION_NAMES[edition]}"
          f"（{'--edition 指定' if args.edition != 'auto' else '按 NewApp.csproj 自动识别'}）")
    flow = ("服务端 + 客户端" if start_server and start_client
            else "仅服务端" if start_server
            else "仅客户端" if start_client
            else "无（服务端已在运行）")
    print(f"  流程    {flow}")
    if reuse_server:
        print(f"  ⚠ {BASE_URL} 已在监听：跳过服务端构建/启动，复用现有服务端")
    elif start_client and edition == "online" and not local_server:
        print(f"  ⚠ {BASE_URL} 上没有服务端在跑，客户端可能连不上")

    # ---- 构建计划
    builds: list[Path] = []
    if not args.no_build:
        if start_server:
            builds.append(SERVER_CSPROJ)
        if start_client:
            builds.append(NEWAPP_CSPROJ)

    # ---- 启动命令（dry-run 也按真实产物路径展示）
    server_dll = find_output(SERVER_DIR, cfg, "CreatFlow.Server.dll")
    client_exe = find_output(CLIENT_DIR, cfg, "CreatFlow.NewApp.exe")
    client_dll = find_output(CLIENT_DIR, cfg, "CreatFlow.NewApp.dll")

    client_cmd: list[str] = []
    if client_exe and os.name == "nt":
        client_cmd = [str(client_exe)] + client_args
    elif client_dll:
        client_cmd = ["dotnet", str(client_dll)] + client_args

    if builds:
        print(f"  构建    {', '.join(str(p.relative_to(ROOT)) for p in builds)} ({cfg})")
    elif args.no_build:
        print("  构建    跳过（--no-build）")
    else:
        print("  构建    —")
    if start_server:
        shown = str(server_dll.relative_to(ROOT)) if server_dll else f"bin/{cfg}/*/CreatFlow.Server.dll"
        print(f"  服务端  dotnet {shown}")
    elif reuse_server:
        print(f"  服务端  复用 {BASE_URL}")
    else:
        print("  服务端  —")
    def show(arg: str) -> str:
        """展示用：绝对路径尽量缩成仓库相对路径。"""
        p = Path(arg)
        if p.is_absolute():
            try:
                return str(p.relative_to(ROOT))
            except ValueError:
                pass
        return arg

    if start_client:
        shown = (" ".join(show(c) for c in client_cmd) if client_cmd
                 else f"bin/{cfg}/*/CreatFlow.NewApp")
        print(f"  客户端  {shown}")
    else:
        print("  客户端  —")
    if client_args:
        print(f"  透传    {' '.join(client_args)}")
    if start_client and edition == "online" and local_server:
        print(f"  环境    CREATFLOW_ServerUrl={BASE_URL}（客户端连本机服务端）")

    if args.dry_run:
        print("\n（--dry-run：只打印计划，不构建、不启动）")
        return 0

    if not start_server and not start_client:
        print(f"\n服务端已在 {BASE_URL} 运行，本次没有需要启动的进程。")
        return 0

    # 端口被占但不像 CreatFlow 服务端 → 明确警告（常见：别的程序占了 5079）
    if port_busy and start_client and not looks_like_creatflow(PORT):
        print(f"  ⚠ 5079 有程序在监听，但对 /api/config/public 没有正常响应 —— "
              f"若它不是 CreatFlow 服务端，先关掉它再跑", flush=True)

    # ---- 构建
    if builds:
        if not shutil.which("dotnet"):
            print("\n找不到 dotnet 命令（需要 .NET 10 SDK）。")
            return 1
        if not run_builds(builds, cfg):
            return 1
        server_dll = find_output(SERVER_DIR, cfg, "CreatFlow.Server.dll")
        client_exe = find_output(CLIENT_DIR, cfg, "CreatFlow.NewApp.exe")
        client_dll = find_output(CLIENT_DIR, cfg, "CreatFlow.NewApp.dll")
        client_cmd = ([str(client_exe)] + client_args if client_exe and os.name == "nt"
                      else ["dotnet", str(client_dll)] + client_args if client_dll else [])

    # ---- 产物检查
    if start_server and not server_dll:
        print(f"找不到服务端产物（{SERVER_DIR / 'bin' / cfg}），先构建一次或去掉 --no-build。")
        return 1
    if start_client and not client_cmd:
        print(f"找不到客户端产物（{CLIENT_DIR / 'bin' / cfg}），先构建一次或去掉 --no-build。")
        return 1

    # ---- 环境
    def base_env() -> dict[str, str]:
        env = dict(os.environ)
        env["DOTNET_ENVIRONMENT"] = "Development"
        return env

    server_env = base_env()
    server_env["ASPNETCORE_ENVIRONMENT"] = "Development"
    server_env["ASPNETCORE_URLS"] = BASE_URL

    client_env = base_env()
    if edition == "online" and local_server:
        client_env["CREATFLOW_ServerUrl"] = BASE_URL

    procs: list[tuple[str, subprocess.Popen]] = []
    try:
        if start_server:
            print(f"\n== 启动服务端 {BASE_URL}（后台 {BASE_URL}/admin/） ==")
            assert server_dll is not None
            server = spawn("server", ["dotnet", str(server_dll)], SERVER_DIR, server_env)
            procs.append(("server", server))
            if not args.no_wait and not wait_ready(server, args.wait_seconds):
                return 1

        if start_client:
            print("\n== 启动客户端 ==")
            client = spawn("client", client_cmd, CLIENT_DIR, client_env)
            procs.append(("client", client))

        print("\n（Ctrl+C 停止；任一端退出会停掉另一端）\n")
        while True:
            for label, p in procs:
                rc = p.poll()
                if rc is not None:
                    print(f"\n[{label}] 已退出（退出码 {rc}），停止其余进程…")
                    return rc
            time.sleep(0.2)
    except KeyboardInterrupt:
        print("\n收到 Ctrl+C，正在停止…")
        return 130
    finally:
        for label, p in reversed(procs):
            stop(p)
        if procs:
            print("已全部停止。")


def _raise_interrupt(signum, frame):  # noqa: ARG001 - signal 回调签名
    raise KeyboardInterrupt


if __name__ == "__main__":
    if os.name == "nt":
        # Windows 下 Python 默认不接管 Ctrl+Break（整个进程直接被杀，子进程成孤儿）；
        # 显式装成 KeyboardInterrupt，和 Ctrl+C 走同一条收尾路径。
        signal.signal(signal.SIGBREAK, _raise_interrupt)
    try:
        # line_buffering：管道/重定向时也逐行刷出（自己的日志 + 子进程日志共用这个流）
        sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
        sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    except Exception:
        pass
    sys.exit(main(sys.argv[1:]))
