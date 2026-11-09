# download_ffmpeg.ps1 — 获取 CreatFlow 视频合成用的 FFmpeg (BtbN win64-lgpl)
#
# 用法:  powershell -ExecutionPolicy Bypass -File scripts/download_ffmpeg.ps1
# 产物:  runtime/ffmpeg/{ffmpeg.exe, ffprobe.exe}（含 SHA256 校验）
#
# 说明: 二进制 115MB 超 gitee 单文件限制，不提交 git；构建/开发前运行本脚本。
# 来源: BtbN/FFmpeg-Builds win64-lgpl（LGPL，无 libx264 等 GPL 组件）
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot   # scripts/ 的上一级 = CreatFlow/
$TargetDir = Join-Path $Root "runtime\ffmpeg"
$ZipPath = Join-Path $env:TEMP "ffmpeg-master-latest-win64-lgpl.zip"

$ExpectedHash = @{
    "ffmpeg.exe"  = "562567ff70e21af24943788ec378a08d623a0782984401aeef923a643fd0118b"
    "ffprobe.exe" = "c4c27863b3b9bda0fa0a7c1dcbaa259ef381b84f3a2de5c1e1800c5dc93a59e5"
}

$Urls = @(
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip",
    "https://gh-proxy.com/https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip"
)

if (Test-Path (Join-Path $TargetDir "ffmpeg.exe")) {
    Write-Host "ffmpeg.exe 已存在，跳过下载（如需更新请先删除 runtime/ffmpeg/）" -ForegroundColor DarkGray
    exit 0
}

Write-Host "[1/3] 下载 FFmpeg (BtbN win64-lgpl, ~148MB)..." -ForegroundColor Yellow
$downloaded = $false
foreach ($url in $Urls) {
    try {
        curl.exe -sL -o $ZipPath $url --connect-timeout 30
        if ((Get-Item $ZipPath).Length -gt 100MB) { $downloaded = $true; break }
    } catch { Write-Host "  源不可达: $url" -ForegroundColor DarkGray }
}
if (-not $downloaded) { throw "下载失败：GitHub 与其镜像均不可达" }

Write-Host "[2/3] 解压..." -ForegroundColor Yellow
$ExtractDir = Join-Path $env:TEMP "ffmpeg-extract"
if (Test-Path $ExtractDir) { Remove-Item -Recurse -Force $ExtractDir }
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir
$Bin = Get-ChildItem $ExtractDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
$BinDir = Split-Path $Bin.FullName

Write-Host "[3/3] 拷贝 + SHA256 校验..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
foreach ($exe in @("ffmpeg.exe", "ffprobe.exe")) {
    Copy-Item (Join-Path $BinDir $exe) $TargetDir -Force
    $hash = (Get-FileHash (Join-Path $TargetDir $exe) -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $ExpectedHash[$exe]) { throw "$exe SHA256 不匹配: $hash" }
    Write-Host "  $exe OK ($hash)" -ForegroundColor Green
}

Remove-Item -Recurse -Force $ExtractDir
Remove-Item $ZipPath -Force
Write-Host "完成：$TargetDir" -ForegroundColor Green
