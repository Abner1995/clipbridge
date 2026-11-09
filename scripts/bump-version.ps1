# ============================================================
# 版本号自增（唯一来源：CreatFlow\Directory.Build.props）
#
#   .\scripts\bump-version.ps1                 # patch +1（1.0.0 -> 1.0.1）
#   .\scripts\bump-version.ps1 -Part minor     # 1.0.1 -> 1.1.0
#   .\scripts\bump-version.ps1 -Part major     # 1.1.0 -> 2.0.0
#   .\scripts\bump-version.ps1 -Set 1.2.3      # 直接指定
#   .\scripts\bump-version.ps1 -DryRun         # 只打印，不写盘
#   .\scripts\bump-version.ps1 -Commit         # 递增后顺手 git commit
#
# 由 build-newapp.ps1 自动调用（-Bump，默认 patch）。
# 只替换 <Version> 标签内容，不动文件里的注释与缩进（[xml].Save 会整份重排，不能用）。
#
# ⚠️ 本文件必须保存为 UTF-8 with BOM（PowerShell 5.1 读无 BOM 文件会按 GBK 解码）。
# ============================================================
param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Part = "patch",
    [string]$Set = "",
    [switch]$DryRun,
    [switch]$Commit
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PropsPath = (Resolve-Path (Join-Path $ScriptDir "..\CreatFlow\Directory.Build.props")).Path

# 必须显式 -Encoding UTF8：props 可能没有 BOM，PS 5.1 会按 GBK 解码 → 中文注释写回变乱码
$text = Get-Content -LiteralPath $PropsPath -Raw -Encoding UTF8
$match = [regex]::Match($text, "<Version>\s*([0-9]+(?:\.[0-9]+){0,3})\s*</Version>")
if (-not $match.Success) { throw "Directory.Build.props 里找不到 <Version>" }

$old = $match.Groups[1].Value.Trim()
$pieces = $old.Split(".")
$major = [int]$pieces[0]
$minor = if ($pieces.Length -ge 2) { [int]$pieces[1] } else { 0 }
$patch = if ($pieces.Length -ge 3) { [int]$pieces[2] } else { 0 }

if (-not [string]::IsNullOrWhiteSpace($Set)) {
    if ($Set -notmatch "^\d+\.\d+\.\d+$") { throw "-Set 必须是 x.y.z（例如 1.2.3）" }
    $new = $Set
}
else {
    switch ($Part) {
        "major" { $major += 1; $minor = 0; $patch = 0 }
        "minor" { $minor += 1; $patch = 0 }
        default { $patch += 1 }
    }
    $new = "$major.$minor.$patch"
}

if ($new -eq $old) {
    Write-Host "  Version unchanged: $old" -ForegroundColor DarkGray
    return
}

if ($DryRun) {
    Write-Host "  [dry-run] Version $old -> $new" -ForegroundColor Yellow
    return
}

# 只替换标签内容：保留注释与缩进
$newText = $text.Substring(0, $match.Index) +
           "<Version>$new</Version>" +
           $text.Substring($match.Index + $match.Length)
[System.IO.File]::WriteAllText(
    $PropsPath, $newText, (New-Object System.Text.UTF8Encoding($true)))

Write-Host "  Version $old -> $new" -ForegroundColor Green

if ($Commit) {
    $repoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
    git -C $repoRoot add -- $PropsPath
    if ($LASTEXITCODE -ne 0) { throw "git add 失败" }
    git -C $repoRoot commit -m "chore(release): v$new"
    if ($LASTEXITCODE -ne 0) { throw "git commit 失败" }
    Write-Host "  committed: chore(release): v$new" -ForegroundColor DarkGray
}
