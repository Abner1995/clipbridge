# runtime/kokoro — Kokoro 本地 TTS 模型

CreatFlow 桌面端本地配音（Kokoro 引擎，sherpa-onnx 离线推理）使用的模型文件。

> ⚠️ 本目录**不提交 git**（压缩包 147MB 超 gitee 单文件 100MB 限制）。
> 打包/发版前用 `scripts/download_kokoro.ps1` 获取，或手动按下方说明放置。
> 应用运行时会**优先读取本目录**（随包分发、离线可用）；无捆绑时回退到应用内下载（`%LocalAppData%/CreatFlow/models/`）。

## 版本与来源

| 项 | 值 |
|---|---|
| 模型 | kokoro-int8-multi-lang-v1_1（int8 多语言，含中文音色 晓蓓/晓妮/晓晓/晓依/云健/云希/云夏/云扬） |
| 来源 | https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2 |
| 压缩包 | ~147MB（tar.bz2）；解压后 ~300MB |
| 许可证 | Apache-2.0（k2-fsa/sherpa-onnx 打包分发；模型本体 hexgrad/Kokoro） |
| 音色 sid | 中文女声 45-48（zf_*）、中文男声 49-52（zm_*），v1.1 固定映射 |

## 文件（解压 + 布局修复后）

- `model.int8.onnx` — 推理模型
- `voices.bin` / `tokens.txt` / `lexicon-zh.txt` — 音色表/词表/中文词典
- `phontab` / `phonindex` — 拼音数据（sherpa-onnx 要求放 **DataDir 根目录**，原打包在 `espeak-ng-data/` 子目录，脚本已复制到根）
- `espeak-ng-data/` — 原始数据目录（保留）

## 获取方式

```powershell
# 方式一：脚本（推荐，含布局修复 + 就绪校验）
scripts/download_kokoro.ps1

# 方式二：手动
# 1. 下载 https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2
#    （GitHub 不可达时用 gh-proxy.com 前缀：https://gh-proxy.com/https://github.com/...）
# 2. 解压到本目录。⚠️ 归档带顶层目录 kokoro-int8-multi-lang-v1_1/，必须用 --strip-components=1 去掉；
#    且用 System32 的 tar（git-bash 里的 GNU tar 会把 C:\ 当远程主机报 "Cannot connect to C:"）；
#    归档自带 README.md，别覆盖本目录这份仓库文档：
#    C:\Windows\System32\tar.exe -xjf <文件> -C runtime/kokoro --strip-components=1 --exclude README.md
# 3. 复制 espeak-ng-data/ 下的 phontab/phonindex 到本目录根
```

打包时：新版 `scripts/build-newapp.ps1` 会显式把本目录补拷进分发包（NewApp csproj 不带 kokoro 条目）；
老版 csproj 也从未包含本目录 —— 老安装包里的配音模型一直走应用内下载路径（%LocalAppData%）。
