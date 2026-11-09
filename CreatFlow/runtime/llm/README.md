# runtime/llm — 内置本地 LLM 引擎（llama.cpp）+ 默认模型（Qwen3-4B）

CreatFlow 桌面端**零安装**本地 AI 文案引擎：llama.cpp 的 `llama-server.exe` 作为子进程运行，
自带 OpenAI 兼容端点（`127.0.0.1:<随机端口>/v1`）—— 应用经现有 `OpenAiCompatibleLlmClient`
调用，与云端 DeepSeek/Qwen、用户自装 Ollama 走同一条协议路径。

> ⚠️ 引擎与模型**不提交 git**（zip 19MB + 模型 2.5GB）。
> 打包/发版前用 `scripts/download_llama.ps1` 获取，或手动按下方说明放置。
> 模型解析顺序：**用户选定档位（`%LocalAppData%/CreatFlow/models/llm/`）→ 本目录捆绑默认**
> ——与 Kokoro 相反（Kokoro 单一固定模型、捆绑优先；LLM 多档位、用户下载的 8B 必须盖过捆绑的 4B）。

## 版本与来源

| 项 | 值 |
|---|---|
| 引擎 | llama.cpp 官方 release `b11540`（win-cpu-x64，AVX2 起步；运行时按 CPU 自动选 `ggml-cpu-*.dll`，老 CPU 落到 sse42/x64 变体） |
| 引擎来源 | https://github.com/ggml-org/llama.cpp/releases/download/b11540/llama-b11540-bin-win-cpu-x64.zip |
| 引擎许可 | MIT（`libomp.dll` 为 LLVM OpenMP，许可见 `LICENSE-LLVM-OpenMP`） |
| 模型 | Qwen3-4B-Q4_K_M（~2.5GB，Apache-2.0） |
| 模型来源 | https://modelscope.cn/models/Qwen/Qwen3-4B-GGUF/resolve/master/Qwen3-4B-Q4_K_M.gguf （兜底 hf-mirror.com；HF 直连国内不通） |

## 文件

- `llama-server.exe` + `llama-server-impl.dll` / `llama-common.dll` / `llama.dll` / `mtmd.dll`
- `ggml*.dll`（含每代 CPU 变体 `ggml-cpu-*.dll`，全部保留——运行时按微架构自动选）
- `libomp.dll` / `LICENSE-LLVM-OpenMP`
- `models/Qwen3-4B-Q4_K_M.gguf` — 默认档捆绑模型

不带 `llama-cli.exe` / `llama-bench.exe` / `llama-quantize.exe` 等开发工具（脚本解压时只取 server + dll）。

## 获取方式

```powershell
# 方式一：脚本（推荐，含 SHA256 校验 + 断点续传；-SkipModel 只取引擎）
powershell -ExecutionPolicy Bypass -File scripts/download_llama.ps1

# 方式二：手动
# 1. 下载引擎 zip（GitHub 不可达时用 gh-proxy.com 前缀）并解压，把 llama-server.exe + 全部 dll 放到本目录
# 2. 下载 Qwen3-4B-Q4_K_M.gguf 放到 models/ 子目录
```

## 运行形态（LlamaServerHost）

- **懒启动**：首次用到 AI 功能才拉起（模型加载数秒~十几秒）；**热态保持 ~10 分钟**无请求后自动卸载
- 参数：`--host 127.0.0.1 --port <自选空闲端口> -c 4096 --reasoning off --no-webui`
  （`--reasoning off`：Qwen3 必须关思考，否则慢数倍且内容跑进 reasoning 字段——同 DeepSeek `thinking:disabled` 一课；
  `--jinja` 此版默认开启，模板取 GGUF 内嵌的 Qwen3 chat template）
- 退出应用杀进程；启动时回收上次残留的孤儿进程（按镜像路径核对，只杀本目录的）

打包（setup.iss / 网盘 zip）随 publish 输出整体分发；应用内 8B 档下载落 `%LocalAppData%/CreatFlow/models/llm/`。
