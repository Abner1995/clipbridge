# runtime/ffmpeg — FFmpeg (LGPL) 二进制

CreatFlow 桌面端视频合成（图片轮播 + 配音 + 字幕 → mp4）使用的 FFmpeg。

> ⚠️ 本目录**不提交 git**（ffmpeg.exe 115MB 超 gitee 单文件 100MB 限制）。
> 构建/开发前用 `scripts/download_ffmpeg.ps1` 获取，或手动按下方说明放置。

## 版本与来源

| 项 | 值 |
|---|---|
| 构建 | BtbN/FFmpeg-Builds `ffmpeg-master-latest-win64-lgpl`（2026-08-27，N-126303-g9f35e220ff） |
| 来源 | https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip |
| 许可证 | **LGPL**（静态链接，不含 libx264/libx265 等 GPL 组件） |
| 编码器 | `h264_mf`（MediaFoundation 硬件 H.264，主）+ `mpeg4`（软件兜底）；音频 `aac` |

## 文件

- `ffmpeg.exe` — 合成/抽帧/时长探测（`ffmpeg -i` 的 stderr 可解析 duration，不依赖 ffprobe）
- `ffprobe.exe` — 可选，JSON 时长探测更稳
- SHA256:
  - ffmpeg.exe `562567ff70e21af24943788ec378a08d623a0782984401aeef923a643fd0118b`
  - ffprobe.exe `c4c27863b3b9bda0fa0a7c1dcbaa259ef381b84f3a2de5c1e1800c5dc93a59e5`

## LGPL 合规说明

BtbN 静态 LGPL 构建满足 LGPL 再分发义务（提供对应源码链接即可）：
https://github.com/BtbN/FFmpeg-Builds（源码指向 ffmpeg 官方 + 各 LGPL 库源码）。

## 获取方式

```powershell
# 方式一：脚本（推荐，含 SHA256 校验）
scripts/download_ffmpeg.ps1

# 方式二：手动
# 1. 下载 https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip
#    （GitHub 不可达时用 gh-proxy.com 前缀：https://gh-proxy.com/https://github.com/...）
# 2. 解压，从 bin/ 拷贝 ffmpeg.exe 与 ffprobe.exe 到本目录
# 3. 校验 SHA256（见上表）
```

安装器（setup.iss）随 AppPublishObfuscated 整体分发时，本目录二进制自动进入安装包。
