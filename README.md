# CreatFlow 单机版

多平台自媒体内容发布桌面工具（Windows），基于 .NET 10 + Avalonia + WebView2。

> 本仓库为源码公开版。功能截图和使用文档将在后续版本补齐。

## 功能

- **六平台发布**：抖音 / 快手 / 小红书 / 微信视频号 / B站 / 今日头条，多账号勾选、一次提交。
- **发布队列**：批量导入、失败重试、定时发布（多平台取可用窗口交集）、账号分组一键勾选。
- **内容创作**：本地中文配音（Kokoro，可选音色 / 语速，字幕按句滚动）、图片 / 视频合成（转场、动效、背景音乐、字幕、水印、封面）。
- **AI 辅助**：AI 文案优化 + 角色库（台词模板 / 人设 / 知识库），可选本地 llama.cpp 引擎。
- **视频下载**：抖音 / 快手 / 小红书链接解析与下载（更多平台持续补充）。
- **作品管理**：跨平台作品列表、批量删除（异步任务，失败可重试 / 兜底清理）。
- **合集管理**：抖音 / 快手 / 小红书 / 视频号 / 头条（视频 + 文章）合集创建、同步、更新、删除。
- **账号管理**：WebView2 登录、多账号独立档案、会话探活、账号分组。
- **本地优先**：SQLite 本地库、数据备份 / 导出、日志自动清理；单机版不依赖服务端。
- **通知与设置**：钉钉机器人通知、上传提示音音量、视频水印、联系我们页面。

> 平台能力有差异：B站不支持程序内删除（仅清本地记录）；头条竖版发布即公开；各平台定时窗口不同，多平台定时取交集。使用前请阅读 [DISCLAIMER.md](DISCLAIMER.md)。

## 构建

环境：Windows 10/11、.NET 10 SDK。

```powershell
# 1) 运行时资产（ffmpeg 为合成/发布/自检所需；llama/kokoro 可选）
powershell -ExecutionPolicy Bypass -File CreatFlow/scripts/download_ffmpeg.ps1

# 可选：本地 LLM 引擎（不捆模型）
powershell -ExecutionPolicy Bypass -File CreatFlow/scripts/download_llama.ps1 -SkipModel

# 可选：Kokoro 中文配音模型（约 147MB）
powershell -ExecutionPolicy Bypass -File CreatFlow/scripts/download_kokoro.ps1

# 2) 构建
dotnet build CreatFlow/CreatFlow.Standalone.slnx -c Debug

# 3) 自检 / 冒烟
CreatFlow/src/CreatFlow.NewApp/bin/Debug/net10.0/CreatFlow.NewApp.exe --selftest
CreatFlow/src/CreatFlow.NewApp/bin/Debug/net10.0/CreatFlow.NewApp.exe --smoke
```

开发启动：`scripts\dev-standalone.cmd`（只启动客户端，不启动服务端）。

> 平台账号登录、发布等操作需要各平台账号；本项目与各平台无隶属、合作或授权关系。

## 支持本项目

如果这个项目对你有帮助，欢迎请作者喝杯咖啡：

<p align="center">
  <img src="assets/donation.jpg" width="280" alt="赞赏码">
</p>

## 许可证

本项目使用 **PolyForm Shield License 1.0.0**：

- 允许学习、修改和用于非竞品用途；
- 要求保留版权声明（见 [NOTICE](NOTICE)）；
- **禁止将本项目或其修改版用于售卖软件、转售、出租或提供同类竞品服务**。

完整条款见 [LICENSE](LICENSE)。

## 免责声明

⚠️ 请务必先阅读 [DISCLAIMER.md](DISCLAIMER.md)。

本项目仅供学习和技术研究使用，禁止用于任何违法用途或侵犯他人合法权益的行为。
使用本项目即表示您同意免责声明的所有条款和条件。

## 第三方组件

见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

本项目不包含任何第三方平台的源代码，与各平台无隶属或授权关系。
