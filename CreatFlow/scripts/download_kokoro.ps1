# download_kokoro.ps1 — 获取 Kokoro 本地 TTS 模型 (sherpa-onnx tts-models 官方打包)
#
# 用法:  powershell -ExecutionPolicy Bypass -File scripts/download_kokoro.ps1
# 产物:  runtime/kokoro/{model.int8.onnx, voices.bin, tokens.txt, lexicon-zh.txt, phontab, phonindex, espeak-ng-data/}
#        布局即 sherpa-onnx OfflineTtsKokoroModelConfig 所需（phontab/phonindex 已复制到根目录）
#
# 说明: 压缩包 147MB 超 gitee 单文件限制，不提交 git；打包（网盘分发/安装器）前运行本脚本。
# 来源: k2-fsa/sherpa-onnx releases/tts-models kokoro-int8-multi-lang-v1_1（Apache-2.0）
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot   # scripts/ 的上一级 = CreatFlow/
$TargetDir = Join-Path $Root "runtime\kokoro"
$Bz2Path = Join-Path $env:TEMP "kokoro-int8-multi-lang-v1_1.tar.bz2"
$FileName = "kokoro-int8-multi-lang-v1_1.tar.bz2"

$Urls = @(
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$FileName",
    "https://gh-proxy.com/https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$FileName"
)

# 就绪校验：关键文件缺任一则重新获取（与 KokoroModels.IsReady 同一标准 + phontab）
$Need = @("model.int8.onnx", "voices.bin", "tokens.txt", "phontab")
$Complete = $true
foreach ($f in $Need) { if (-not (Test-Path (Join-Path $TargetDir $f))) { $Complete = $false } }
if ($Complete) {
    Write-Host "runtime/kokoro/ 已就绪，跳过下载（如需更新请先删除该目录）" -ForegroundColor DarkGray
    exit 0
}

Write-Host "[1/3] 下载 Kokoro 模型 ($FileName, ~147MB)..." -ForegroundColor Yellow
$downloaded = $false
foreach ($url in $Urls) {
    try {
        curl.exe -sL -o $Bz2Path $url --connect-timeout 30
        if ((Get-Item $Bz2Path).Length -gt 100MB) { $downloaded = $true; break }
    } catch { Write-Host "  源不可达: $url" -ForegroundColor DarkGray }
}
if (-not $downloaded) { throw "下载失败：GitHub 与其镜像均不可达" }

Write-Host "[2/3] 解压 (tar -xjf, Windows 10 1803+ 自带 bsdtar 支持 bz2)..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
# 三个坑（2026-10-14 实测）：
# 1) 必须显式用 System32 的 bsdtar —— 从 git-bash 等环境继承的 PATH 里 MSYS 版 GNU tar 会抢先，
#    它把 "C:\..." 当远程主机（host:path 语法）直接报 "Cannot connect to C: resolve failed"。
# 2) --strip-components=1：归档带顶层目录 kokoro-int8-multi-lang-v1_1/，去掉它文件才落在本目录根
#    （KokoroModels.BundledDir 期望的布局）——否则后面的就绪校验必然失败。
# 3) 先解压到临时目录再合并（同名已存在就跳过）：归档自带 README.md/LICENSE，直接解压到目标目录
#    会把仓库自带的 README.md（git 跟踪的文档）顶掉。
$TarExe = Join-Path $env:SystemRoot "System32\tar.exe"
if (-not (Test-Path $TarExe)) { $TarExe = "tar" }
$ExtractDir = Join-Path $env:TEMP "kokoro-extract"
if (Test-Path $ExtractDir) { Remove-Item -Recurse -Force $ExtractDir }
New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null
& $TarExe -xjf $Bz2Path -C $ExtractDir --strip-components=1
if ($LASTEXITCODE -ne 0) { throw "解压失败 (tar exit=$LASTEXITCODE)" }
Get-ChildItem $ExtractDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($ExtractDir.Length + 1)
    $dest = Join-Path $TargetDir $rel
    if (-not (Test-Path $dest)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Move-Item $_.FullName $dest
    }
}
Remove-Item -Recurse -Force $ExtractDir

Write-Host "[3/3] 布局修复 + 校验..." -ForegroundColor Yellow
# sherpa-onnx 要求 DataDir 根有 phontab/phonindex；v1.1-zh 打包在 espeak-ng-data/ 子目录（实测缺失直接报错）→ 复制到根
$EsData = Join-Path $TargetDir "espeak-ng-data"
if (Test-Path $EsData) {
    Get-ChildItem $EsData -File | ForEach-Object {
        $t = Join-Path $TargetDir $_.Name
        if (-not (Test-Path $t)) { Copy-Item $_.FullName $t }
    }
}
foreach ($f in $Need) {
    if (-not (Test-Path (Join-Path $TargetDir $f))) { throw "校验失败：缺少 $f" }
    Write-Host "  $f OK" -ForegroundColor Green
}

Remove-Item $Bz2Path -Force
Write-Host "完成：$TargetDir（新版打包脚本 scripts/build-newapp.ps1 会把它补拷进安装包）" -ForegroundColor Green
