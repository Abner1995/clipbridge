# download_llama.ps1 — 获取内置本地 LLM 引擎（llama.cpp llama-server）+ 默认模型（Qwen3-4B）
#
# 用法:  powershell -ExecutionPolicy Bypass -File scripts/download_llama.ps1
#        可选 -SkipModel 只取引擎（开发调试用）
# 产物:  runtime/llm/llama-server.exe + 全部 dll（server 子集，不带 cli/bench/quantize 等工具）
#        runtime/llm/models/Qwen3-4B-Q4_K_M.gguf
#
# 说明: 引擎 zip ~19MB、模型 ~2.5GB，均不提交 git；打包（网盘分发/安装器）前运行本脚本。
# 来源: ggml-org/llama.cpp 官方 release（MIT）+ Qwen/Qwen3-4B-GGUF（Apache-2.0，ModelScope 官方仓库）
# 为什么不用 Ollama: 产品决策 2026-10-10 —— 不再要求用户自己装 Ollama，本地推理随包分发；
#        llama-server 自带 OpenAI 兼容端点，现有 OpenAiCompatibleLlmClient 零改动。
param([switch]$SkipModel)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot   # scripts/ 的上一级 = CreatFlow/
$TargetDir = Join-Path $Root "runtime\llm"
$ModelDir = Join-Path $TargetDir "models"

# ─── 引擎（固定构建号 + SHA256，升级时同步改这三处） ───
$Build = "b11540"
$EngineZipName = "llama-$Build-bin-win-cpu-x64.zip"
$EngineZipHash = "85cae1b982145d1b315e56e7343524b301a11f0fdf2c7c9dd973c26daba4b3b5"
$EngineUrls = @(
    "https://github.com/ggml-org/llama.cpp/releases/download/$Build/$EngineZipName",
    "https://gh-proxy.com/https://github.com/ggml-org/llama.cpp/releases/download/$Build/$EngineZipName"
)

# ─── 模型（默认档：Qwen3-4B Q4_K_M ≈ 2.5GB；大档 8B 由应用内下载，不进本脚本） ───
$ModelName = "Qwen3-4B-Q4_K_M.gguf"
$ModelHash = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5"
$ModelUrls = @(
    "https://modelscope.cn/models/Qwen/Qwen3-4B-GGUF/resolve/master/$ModelName",
    "https://hf-mirror.com/Qwen/Qwen3-4B-GGUF/resolve/main/$ModelName"
)

# ═══ [1/3] 引擎 ═══
$ServerExe = Join-Path $TargetDir "llama-server.exe"
if (Test-Path $ServerExe) {
    Write-Host "runtime/llm/ 引擎已就绪，跳过（如需更新请先删 runtime/llm/llama-server.exe 与 dll）" -ForegroundColor DarkGray
} else {
    Write-Host "[1/3] 下载 llama-server ($EngineZipName, ~19MB)..." -ForegroundColor Yellow
    $ZipPath = Join-Path $env:TEMP $EngineZipName
    $downloaded = $false
    foreach ($url in $EngineUrls) {
        try {
            curl.exe -sL -o $ZipPath $url --connect-timeout 30
            if ((Get-Item $ZipPath).Length -gt 10MB) { $downloaded = $true; break }
        } catch { Write-Host "  源不可达: $url" -ForegroundColor DarkGray }
    }
    if (-not $downloaded) { throw "下载失败：GitHub 与其镜像均不可达" }

    $hash = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $EngineZipHash) { throw "引擎 zip SHA256 不匹配: $hash（构建号 $Build 被重传？核对后更新脚本）" }

    Write-Host "  解压（只要 server + dll，其余 cli/bench/quantize 等工具不带）..." -ForegroundColor Yellow
    $ExtractDir = Join-Path $env:TEMP "llama-extract"
    if (Test-Path $ExtractDir) { Remove-Item -Recurse -Force $ExtractDir }
    Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    Copy-Item (Join-Path $ExtractDir "llama-server.exe") $TargetDir -Force
    Copy-Item (Join-Path $ExtractDir "*.dll") $TargetDir -Force
    Copy-Item (Join-Path $ExtractDir "LICENSE-LLVM-OpenMP") $TargetDir -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $ExtractDir
    Remove-Item $ZipPath -Force
    Write-Host "  llama-server.exe OK（$hash）" -ForegroundColor Green
}

# ═══ [2/3] 模型 ═══
$ModelPath = Join-Path $ModelDir $ModelName
if ($SkipModel) {
    Write-Host "[2/3] -SkipModel：跳过模型下载" -ForegroundColor DarkGray
} elseif ((Test-Path $ModelPath) -and ((Get-Item $ModelPath).Length -gt 2.4GB)) {
    Write-Host "[2/3] 模型已就绪，跳过（如需更新请先删除 $ModelPath）" -ForegroundColor DarkGray
} else {
    Write-Host "[2/3] 下载模型 ($ModelName, ~2.5GB，断点续传)..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $ModelDir | Out-Null
    $part = "$ModelPath.part"
    $downloaded = $false
    foreach ($url in $ModelUrls) {
        try {
            # -C - 断点续传：中断后重跑本脚本从 .part 继续（ModelScope 实测 ~10MB/s，HF 直连国内不通走 hf-mirror）
            curl.exe -L -C - -o $part $url --connect-timeout 30 --retry 3
            if ((Test-Path $part) -and ((Get-Item $part).Length -gt 2.4GB)) { $downloaded = $true; break }
        } catch { Write-Host "  源不可达: $url" -ForegroundColor DarkGray }
    }
    if (-not $downloaded) { throw "模型下载未完成：重跑本脚本可续传（$part 保留）" }
    Move-Item $part $ModelPath -Force
}

# ═══ [3/3] 校验 ═══
Write-Host "[3/3] 校验..." -ForegroundColor Yellow
if (-not (Test-Path $ServerExe)) { throw "校验失败：缺少 llama-server.exe" }
Write-Host "  llama-server.exe OK" -ForegroundColor Green
if (-not $SkipModel) {
    if (-not (Test-Path $ModelPath)) { throw "校验失败：缺少 $ModelName" }
    if ($ModelHash) {
        $hash = (Get-FileHash $ModelPath -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $ModelHash) { throw "模型 SHA256 不匹配: $hash" }
    }
    Write-Host "  $ModelName OK ($([math]::Round((Get-Item $ModelPath).Length/1GB, 2)) GB)" -ForegroundColor Green
}
Write-Host "完成：$TargetDir（随打包整体分发；应用内 8B 档下载见 AI 设置页）" -ForegroundColor Green
