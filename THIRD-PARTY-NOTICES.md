# 第三方组件与许可证

本项目（CreatFlow 单机版）基于 .NET 10 构建，包含或依赖以下第三方组件。
各组件的完整许可证文本随对应 NuGet 包或官方发行包提供。

## 直接依赖（NuGet）

| 组件 | 许可证 |
|---|---|
| Avalonia / Avalonia.Desktop / Avalonia.Fonts.Inter / Avalonia.Diagnostics | MIT |
| Avalonia.HtmlRenderer | MIT |
| Semi.Avalonia | MIT |
| CommunityToolkit.Mvvm | MIT |
| Material.Icons.Avalonia | MIT |
| Avalonia.Controls.WebView / webview.avalonia.windows | MIT |
| NAudio | MIT |
| Microsoft.Extensions.* | MIT |
| Microsoft.EntityFrameworkCore.Sqlite / Microsoft.EntityFrameworkCore.Design | MIT |
| MailKit / MimeKit | MIT |
| Serilog / Serilog.Sinks.Console / Serilog.Sinks.File | Apache-2.0 |
| org.k2fsa.sherpa.onnx / org.k2fsa.sherpa.onnx.runtime.win-x64 | Apache-2.0 |
| SQLite（Microsoft.Data.Sqlite 运行时） | Public Domain |

各 NuGet 包的传递依赖（如 SkiaSharp、HarfBuzzSharp、Tmds.DBus 等）适用其各自的许可证，
完整清单可在构建目录执行 `dotnet list package --include-transitive` 查看。

## 运行时外部组件（不随本仓库分发）

| 组件 | 许可证 | 说明 |
|---|---|---|
| ffmpeg / ffprobe | LGPL-2.1-or-later | 由 `CreatFlow/scripts/download_ffmpeg.ps1` 下载；使用 BtbN/FFmpeg-Builds 的 LGPL 构建，不静态链接 GPL 组件 |
| Kokoro 中文 TTS 模型 | Apache-2.0 | 由 `CreatFlow/scripts/download_kokoro.ps1` 下载（k2-fsa/sherpa-onnx 打包分发） |
| llama.cpp | MIT | 由 `CreatFlow/scripts/download_llama.ps1` 下载；GGUF 模型适用其各自许可证 |
| Microsoft Edge WebView2 Runtime | Microsoft 许可 | Windows 系统组件，不随本仓库分发 |
| Node.js（node.exe） | MIT | 开发/可选签名回退使用；不随公开仓库分发 |

## 字体与图标

- Inter 字体：SIL Open Font License 1.1（随 Avalonia.Fonts.Inter 分发）
- Material Design Icons：Apache-2.0（随 Material.Icons.Avalonia 分发）

## 平台声明

本项目不包含任何第三方平台（抖音、快手、小红书、微信视频号、哔哩哔哩、今日头条等）
的源代码。平台接口、页面、商标及相关数据的权利归各平台方所有。

如发现遗漏或错误的许可证信息，请提交 Issue 更正。
