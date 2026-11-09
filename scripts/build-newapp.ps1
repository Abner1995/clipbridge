# ============================================================
# CreatFlow.NewApp local build + obfuscate + verify + package (手动分发)
# Run on Windows dev machine
#
# ⚠️ 本文件必须保存为 UTF-8 with BOM！
#    PowerShell 5.1 读无 BOM 文件会按 GBK 解码，中文注释会吞掉换行，
#    导致代码行被并进注释（老脚本 $SymbolArchive 赋值被吞 → null 报错的事故）。
#
# 产物（联网版）:
#   Output/CreatFlow-NewApp-Setup-v{Version}.exe   安装包 (推荐分发)
#   CreatFlow/CreatFlow-NewApp-v{Version}.zip      绿色版 zip
# 产物（单机版，csproj 定义 CREATFLOW_STANDALONE 时自动切换，也可显式 -Standalone）:
#   Output/CreatFlow-Standalone-Setup-v{Version}.exe   独立 AppId / 目录 / 数据说明
#   CreatFlow/CreatFlow-Standalone-v{Version}.zip
# 留档:
#   CreatFlow/ReleaseArchive/{NewApp|Standalone}-v{Version}/    符号文件 (Mapping.txt + pdb)
#     - 解析混淆后崩溃堆栈用，绝不进分发包
#
# 与老版 build-upload-app.ps1 的区别（双存期并存，老版脚本/产物完全不受影响）:
#   1) 目标工程 = CreatFlow.NewApp（终态）；Inno 身份独立（AppId/目录/快捷方式都不撞老版）
#   2) 多一道【验证门禁】：混淆产物必须跑通 --selftest（退出码=失败数）与 --smoke（读报告）
#      —— 拦住"开发版正常、打包版炸"（JSON 按名绑定/反射被混淆打断）这类只在分发包暴露的问题
#   3) 包内容裁剪：不捆 Qwen3-4B 模型（2.4GB，首次用 AI 时应用内下载）；补拷 csproj 没带的 runtime/kokoro
#   4) 不清空整个 Output 目录（里面有老版安装包），只删自己的旧 Setup
#   5) 混淆后验证失败/混淆产物缺失 → 直接中止，不回退"zip 未混淆目录"（那会出个看似正常的裸包）
# ============================================================
param(
    [switch]$SkipObfuscate,
    [switch]$SkipInstaller,
    [switch]$SkipZip,
    [switch]$SkipVerify,     # 跳过 selftest/smoke 门禁（只建议临时排障用，产物不可分发）
    [switch]$SkipKokoro,     # 不捆 Kokoro 配音模型（~300MB；用户首次配音前需应用内下载）
    [switch]$WithLlmModel,   # 把 Qwen3-4B（2.4GB）也捆进包 —— 默认关：>2GB 单文件对 zip/Inno 都是雷
    [switch]$Standalone,     # 单机版（edition/standalone）：独立目录 / 独立安装器身份 / 跳过服务端配置。
                             # 分支 csproj 定义了 CREATFLOW_STANDALONE 时自动按单机版打包，可不传
    [ValidateSet("none", "patch", "minor", "major")]
    [string]$Bump = "patch", # 版本策略：默认每次打包 patch +1（1.0.0 → 1.0.1）；-Bump none 不递增
    [string]$Version = ""    # 显式版本（优先级最高，传了就不递增）
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path "$ScriptDir\..\CreatFlow"

# ---- 版本号（2026-10-21 自动化）----
#   1) 显式 -Version 1.2.3  → 原样使用（不递增）；发大版本用这个，或 -Bump minor/major
#   2) 否则按 -Bump（默认 patch）调 bump-version.ps1 改 Directory.Build.props
#   3) 最后从 props 读回（dotnet publish 与 Inno 共用这一个字符串）
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "  Version (显式指定，不递增): $Version" -ForegroundColor DarkGray
}
else {
    if ($Bump -ne "none") {
        # 子脚本用 throw 报错，终止错误会直接冒泡；不要看 $LASTEXITCODE
        # （PowerShell 脚本调用不会更新它，可能沿用上一次原生命令的旧值）
        & "$ScriptDir\bump-version.ps1" -Part $Bump
    }

    $propsPath = "$ProjectRoot\Directory.Build.props"
    if (-not (Test-Path $propsPath)) {
        throw "找不到版本源：$propsPath（或显式传 -Version，例如 -Version 1.0.3）"
    }
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
    $Version = ($props.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) { throw "Directory.Build.props 里没有 <Version>" }
    $Version = $Version.Trim()
    Write-Host "  Version (from Directory.Build.props): $Version" -ForegroundColor DarkGray
}
$AppProject = "$ProjectRoot\src\CreatFlow.NewApp"

# ---- 版本分支（联网 / 单机，2026-10-28）----
# 打的是什么二进制，就必须用对应的安装器身份与产物名 —— 不能让「单机版二进制 + 联网版
# 安装器」这种混搭流出去（AppId 相同还会互相覆盖安装、互相顶掉快捷方式）。
# 自动判据 = csproj 里的编译期常量（唯一事实来源）；-Standalone 只作为显式确认。
$newAppCsproj = Get-Content -LiteralPath "$AppProject\CreatFlow.NewApp.csproj" -Raw -Encoding UTF8
# 只在 <DefineConstants> 里找，避免注释里提到这个常量就误判
$csprojStandalone = $newAppCsproj -match '<DefineConstants>[^<]*CREATFLOW_STANDALONE'
if ($csprojStandalone -and -not $Standalone) {
    $Standalone = $true
    Write-Host "  检测到 csproj 定义 CREATFLOW_STANDALONE → 按单机版身份打包" -ForegroundColor DarkGray
}
if ($Standalone -and -not $csprojStandalone) {
    throw "-Standalone 与工程不符：CreatFlow.NewApp.csproj 未定义 CREATFLOW_STANDALONE（当前分支是联网版）。"
}

# 符号文件留档目录（按版本分支定）：Mapping.txt (混淆映射) + pdb (原始调试符号)
# 两者一起进分发包 = 混淆白做，攻击者可一键还原类名
if ($Standalone) {
    $AppDisplayName = "CreatFlow 单机版"
    $PublishDir = "$ProjectRoot\StandalonePublish"
    $ObfuscatedDir = "$ProjectRoot\StandalonePublishObfuscated"
    $SymbolArchive = "$ProjectRoot\ReleaseArchive\Standalone-v$Version"
    $IssFile = "$ProjectRoot\setup-standalone.iss"
    $SetupFilter = "CreatFlow-Standalone-Setup-*.exe"
    $ZipFile = "$ProjectRoot\CreatFlow-Standalone-v$Version.zip"
}
else {
    $AppDisplayName = "CreatFlow NewApp"
    $PublishDir = "$ProjectRoot\NewAppPublish"
    $ObfuscatedDir = "$ProjectRoot\NewAppPublishObfuscated"
    $SymbolArchive = "$ProjectRoot\ReleaseArchive\NewApp-v$Version"
    $IssFile = "$ProjectRoot\setup-newapp.iss"
    $SetupFilter = "CreatFlow-NewApp-Setup-*.exe"
    $ZipFile = "$ProjectRoot\CreatFlow-NewApp-v$Version.zip"
}

$ObfuscarXml = "$AppProject\obfuscar.xml"
$OutputDir = "$ProjectRoot\..\Output"
$ExeName = "CreatFlow.NewApp.exe"
$RuntimeDir = "$ProjectRoot\runtime"
$DownloadDir = "$ProjectRoot\scripts"
# 多路径查找 Inno Setup（用户可能安装在非默认盘）
$InnoSetup = $null
$pfx86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
$candidates = @(
    "E:\programfiles\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
if ($pfx86) { $candidates += "$pfx86\Inno Setup 6\ISCC.exe" }
foreach ($candidate in $candidates) {
    if (Test-Path $candidate) { $InnoSetup = $candidate; break }
}
if ($InnoSetup) {
    Write-Host "  Inno Setup found: $InnoSetup" -ForegroundColor DarkGray
}

Write-Host "========== CreatFlow.NewApp v${Version} build ==========" -ForegroundColor Cyan

# setup-newapp.iss 固定打包 NewAppPublishObfuscated 目录 —— 跳过混淆时打安装包会拿到旧产物或是空的
if ($SkipObfuscate -and -not $SkipInstaller) {
    throw "-SkipObfuscate 与打安装包冲突（安装包只从 NewAppPublishObfuscated 取料）。要么去掉 -SkipObfuscate，要么加 -SkipInstaller 只出 zip。"
}

# ---- Step 0: 运行时资产预检（缺则自动补下载；下载脚本幂等 + SHA256 校验） ----
Write-Host "[0/6] Runtime assets preflight..." -ForegroundColor Yellow

# 下载脚本用裸命令名调用系统工具（tar 等）——从 git-bash 等环境继承的 PATH 里 MSYS 工具会抢先，
# 行为与「用户自己开 PowerShell 跑」不一致（GNU tar 会把 C:\ 路径当远程主机报错）→
# 把 System32 顶到最前，让子进程拿到系统版工具（download_kokoro.ps1 里也有同款防御）。
$env:PATH = "$env:SystemRoot\System32;$env:SystemRoot;$env:PATH"

# 用独立 PowerShell 子进程跑下载脚本：脚本里有 `exit 0`（已就绪早退），
# 直接 & 调用可能把本脚本一起 exit 掉；子进程 + 退出码是最稳的。
function Invoke-DownloadScript([string]$ScriptPath, [string[]]$ScriptArgs, [string]$What) {
    Write-Host "  下载缺失资产: $What ..." -ForegroundColor DarkYellow
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @ScriptArgs
    if ($LASTEXITCODE -ne 0) { throw "$What 下载失败（脚本退出码 $LASTEXITCODE）：$ScriptPath" }
}

if (-not (Test-Path "$RuntimeDir\ffmpeg\ffmpeg.exe") -or -not (Test-Path "$RuntimeDir\ffmpeg\ffprobe.exe")) {
    Invoke-DownloadScript "$DownloadDir\download_ffmpeg.ps1" @() "FFmpeg"
}
if (-not (Test-Path "$RuntimeDir\llm\llama-server.exe")) {
    # -SkipModel：包不捆 4B 模型，只取引擎（~19MB）
    Invoke-DownloadScript "$DownloadDir\download_llama.ps1" @("-SkipModel") "llama-server 引擎"
}
if (-not $SkipKokoro -and -not (Test-Path "$RuntimeDir\kokoro\model.int8.onnx")) {
    Invoke-DownloadScript "$DownloadDir\download_kokoro.ps1" @() "Kokoro 配音模型"
}
# 快手签名已改为 WebView2 页面 webpack 模块（不再依赖 node.exe / chunk.js）
# appsettings.Production.json 被 gitignore（含 AppSecret）：新克隆的机器没有，漏了会打出指向 localhost 的包。
# 单机版没有服务端、不打这个包 —— 源码分发时，用户机器上也不会有这份私有文件。
if (-not $Standalone -and -not (Test-Path "$AppProject\..\CreatFlow.App\appsettings.Production.json")) {
    throw "缺少 src\CreatFlow.App\appsettings.Production.json（Release 发布用它，含 ServerUrl 与 AppSecret）。该文件不进 git，需从私有备份取回。"
}
Write-Host "  Runtime assets OK" -ForegroundColor Green

# ---- Step 1: dotnet publish ----
Write-Host "[1/6] dotnet publish (win-x64, self-contained)..." -ForegroundColor Yellow

# 残留进程会锁住 publish 目录（冒烟若因网络挂住会留下进程）→ 先清
Get-Process "CreatFlow.NewApp" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  发现残留 CreatFlow.NewApp 进程 (PID $($_.Id))，先结束" -ForegroundColor DarkYellow
    Stop-Process -Id $_.Id -Force
}
if (Test-Path $PublishDir)    { Remove-Item -Recurse -Force $PublishDir }
if (Test-Path $ObfuscatedDir) { Remove-Item -Recurse -Force $ObfuscatedDir }
# 只删自己的旧安装包（整个 Output 目录里有老版/另一版本的 Setup，不能动）
Remove-Item "$OutputDir\$SetupFilter" -Force -ErrorAction SilentlyContinue

# -p:Version 让 AssemblyVersion/FileVersion 跟包版本一致（UI 与登录上报都读它）
dotnet publish $AppProject -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
Write-Host "  Build done: $PublishDir" -ForegroundColor Green

# 单机版不带服务端生产配置：本机若有这份私有文件会被 Release 的 Content 拷进 publish，
# 剔除掉，避免分发包里出现指向我们服务器的地址与 AppSecret（单机版全程不用它）。
if ($Standalone) {
    Remove-Item "$PublishDir\appsettings.Production.json" -Force -ErrorAction SilentlyContinue
}

# ---- 版本门禁（发布产物）：UI 读 AssemblyInformationalVersion，安装包读 -Version；
#      两者必须是同一个字符串。不一致直接中止，别让用户装上一个「显示 1.0.0.0、包名 1.0.0」的版本。
$publishedExe = Join-Path $PublishDir $ExeName
if (-not (Test-Path $publishedExe)) { throw "发布产物缺失：$publishedExe" }
$publishedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe).ProductVersion
if (-not $publishedVersion.StartsWith($Version, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "版本不一致：打包参数=$Version，产物 ProductVersion=$publishedVersion —— 检查 Directory.Build.props / csproj 是否覆盖了 <Version>"
}
Write-Host "  版本门禁 OK：产物 ProductVersion=$publishedVersion" -ForegroundColor DarkGray

# ---- Step 1b: 包内容裁剪/补拷 ----
if ($WithLlmModel) {
    Write-Host "  ⚠️ -WithLlmModel：Qwen3-4B（2.4GB）将进入分发包 —— Inno 压缩会非常慢，" -ForegroundColor DarkYellow
    Write-Host "     且 zip 阶段单文件超 2GB 会失败（Compress-Archive 无 Zip64）。自担风险。" -ForegroundColor DarkYellow
}
else {
    $ggufs = Get-ChildItem "$PublishDir\runtimes\llm\models" -Filter "*.gguf" -ErrorAction SilentlyContinue
    if ($ggufs) {
        $ggufs | Remove-Item -Force
        Write-Host "  已剔除 LLM 模型（$($ggufs.Count) 个，首次用 AI 时应用内下载）" -ForegroundColor DarkGray
    }
}
if (-not $SkipKokoro) {
    # csproj 没带 runtime\kokoro（KokoroModels.BundledDir 期望 publish\runtime\kokoro\）→ 打包时补拷
    New-Item -ItemType Directory -Force -Path "$PublishDir\runtime\kokoro" | Out-Null
    Copy-Item "$RuntimeDir\kokoro\*" "$PublishDir\runtime\kokoro\" -Recurse -Force
    Write-Host "  已补拷 runtime\kokoro（离线配音模型）" -ForegroundColor DarkGray
}
else {
    Write-Host "  -SkipKokoro：不捆 Kokoro 模型（用户首次配音前需在 AI 设置页下载）" -ForegroundColor DarkYellow
}

# ---- Step 1c: 分发包资产断言（缺任一即中止 —— 这些缺失都是"装完才发现"的坑） ----
$mustHave = @(
    $ExeName,
    "runtime\ffmpeg\ffmpeg.exe",
    "runtime\ffmpeg\ffprobe.exe",
    "runtimes\llm\llama-server.exe",
    "appsettings.json",
    "Assets\app-icon.ico"
)
if (-not $Standalone) { $mustHave += "appsettings.Production.json" }
if (-not $SkipKokoro) { $mustHave += "runtime\kokoro\model.int8.onnx" }
foreach ($rel in $mustHave) {
    if (-not (Test-Path "$PublishDir\$rel")) { throw "分发包缺资产: $rel（publish 输出不完整，检查 csproj 与 runtime 目录）" }
}
if ($Standalone -and (Test-Path "$PublishDir\appsettings.Production.json")) {
    throw "单机版分发包里混入 appsettings.Production.json（含服务端地址 / AppSecret，单机版不用）"
}
if (-not $WithLlmModel -and (Test-Path "$PublishDir\runtimes\llm\models\*.gguf")) {
    throw "gguf 模型仍在 publish 目录 —— 剔除失败，zip/Inno 会因为 2.4GB 文件出问题"
}
Write-Host "  Package assets OK" -ForegroundColor Green

# ---- Step 2: Obfuscar ----
$DistDir = $PublishDir
if (-not $SkipObfuscate) {
    Write-Host "[2/6] Obfuscar..." -ForegroundColor Yellow

    $obfuscar = Get-Command obfuscar.console -ErrorAction SilentlyContinue
    if (-not $obfuscar) {
        Write-Host "  Installing Obfuscar..." -ForegroundColor DarkGray
        dotnet tool install --global Obfuscar.GlobalTool 2>&1 | Out-Null
        $obfuscar = Get-Command obfuscar.console -ErrorAction SilentlyContinue
    }
    if (-not $obfuscar) {
        throw "Obfuscar 不可用（自动安装失败）。装好它或显式 -SkipObfuscate（后者产物不可分发）。"
    }

    # 先复制 publish 全部文件到混淆目录（含 .NET 运行时 DLL 与 pdb）
    # 机制：OutPath 不会被打扫，obfuscar 只把【模块 dll】覆盖过去 + 落 Mapping.txt；
    #       没这份预拷贝，OutPath 里就只剩被混淆的几个 dll，运行时会缺依赖。
    Copy-Item -Recurse -Path $PublishDir -Destination $ObfuscatedDir

    # OutPath 在 xml 里写死为联网版目录（NewAppPublishObfuscated）。单机版必须改写到
    # StandalonePublishObfuscated —— 否则会把单机版二进制混进联网版产物目录，
    # 而脚本又去单机版目录找 Mapping.txt，必然误报「产物不完整」。
    # 用临时副本改写（绝对路径），源 xml 继续作为唯一配置源；临时文件不进分发包。
    $ObfuscarConfig = $ObfuscarXml
    if ($Standalone) {
        $ObOutLiteral = $ObfuscatedDir.Replace('\', '/')
        $xmlText = Get-Content -LiteralPath $ObfuscarXml -Raw -Encoding UTF8
        $xmlText = $xmlText -replace '<Var name="OutPath" value="[^"]*"',
            ('<Var name="OutPath" value="' + $ObOutLiteral.Replace('$', '$$') + '"')
        $ObfuscarConfig = Join-Path $env:TEMP "creatflow-obfuscar-standalone.xml"
        Set-Content -LiteralPath $ObfuscarConfig -Value $xmlText -Encoding UTF8
        Write-Host "  Obfuscar OutPath -> $ObfuscatedDir（临时配置）" -ForegroundColor DarkGray
    }

    # 在 publish 目录里跑：obfuscar 的 InPath/OutPath/file= 全部相对【进程 CWD】解析（不是 xml 所在目录）。
    # ⚠️ 不把 obfuscar.xml 拷进 publish 目录（那会混进分发包）—— 直接传路径参数，相对解析照样成立。
    Push-Location $PublishDir
    try {
        obfuscar.console $ObfuscarConfig
        if ($LASTEXITCODE -ne 0) { throw "Obfuscar 失败（退出码 $LASTEXITCODE）" }
    }
    finally { Pop-Location }

    if (-not (Test-Path "$ObfuscatedDir\Mapping.txt") -or -not (Test-Path "$ObfuscatedDir\$ExeName")) {
        throw "Obfuscar 产物不完整（缺 Mapping.txt 或 $ExeName）"
    }
    $DistDir = $ObfuscatedDir
    Write-Host "  Obfuscated: $ObfuscatedDir" -ForegroundColor Green
}
else {
    Write-Host "[2/6] Skipping obfuscation (-SkipObfuscate)" -ForegroundColor DarkGray
}

# ---- Step 3: 验证门禁（selftest 退出码 + smoke 报告；全部在 TEMP 落盘，零用户文件） ----
if (-not $SkipVerify) {
    Write-Host "[3/6] Verify gate (selftest + smoke) on the shipping binaries..." -ForegroundColor Yellow
    $verifyExe = "$DistDir\$ExeName"

    # 清旧报告（留着的旧文件会把"没跑"误读成"跑过了"）
    Remove-Item "$env:TEMP\newapp_smoke.txt", "$env:TEMP\newapp_crash.txt" -Force -ErrorAction SilentlyContinue
    Get-ChildItem $env:TEMP -Filter "newapp_*_selftest.txt" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # 与分发包同配置（Release 只发布 Production 配置；本机若残留 Development 环境变量会跑错配置）
    $env:DOTNET_ENVIRONMENT = "Production"

    # --selftest：退出码 = 失败项数（0 = 全过）；7 组报告写 %TEMP%\newapp_*_selftest.txt
    # 用 Start-Process -Wait 而不是 & exe：Release 是 WinExe，PS 5.1 对 GUI 子系统不等待
    $st = Start-Process -FilePath $verifyExe -ArgumentList "--selftest" -Wait -PassThru -NoNewWindow
    if ($st.ExitCode -ne 0) {
        throw "自检失败 $($st.ExitCode) 项！报告在 %TEMP%\newapp_*_selftest.txt（先看第一份报错的；" +
              "混淆打断 JSON 名绑定/反射时，按报告里的类型名往 obfuscar.xml 补 SkipType 再跑）。"
    }
    Write-Host "  --selftest 全过（7 组）：" -ForegroundColor Green
    Get-ChildItem $env:TEMP -Filter "newapp_*_selftest.txt" | ForEach-Object {
        $tail = Get-Content $_.FullName -Tail 1
        Write-Host "    $($_.Name): $tail" -ForegroundColor DarkGray
    }

    # --smoke：报告 %TEMP%\newapp_smoke.txt；成功/失败两个分支退出码都是 0 → 必须读报告。
    # 需要交互式桌面会话（会真开窗口渲染）；会发真 HTTP 到 Production ServerUrl（空态可容忍，设计如此）。
    $sm = Start-Process -FilePath $verifyExe -ArgumentList "--smoke" -PassThru -NoNewWindow
    if (-not $sm.WaitForExit(10 * 60 * 1000)) {
        try { $sm.Kill() } catch { }
        throw "--smoke 超时（10 分钟）已强杀 —— 通常是某个页面卡网络；查 %LocalAppData%\CreatFlow\logs 最新日志"
    }
    $smokeFile = "$env:TEMP\newapp_smoke.txt"
    $smokeLine = if (Test-Path $smokeFile) { Get-Content $smokeFile -TotalCount 1 } else { "" }
    if ($smokeLine -notmatch '^SMOKE OK') {
        if (Test-Path "$env:TEMP\newapp_crash.txt") {
            throw "--smoke 前发生硬崩溃：见 %TEMP%\newapp_crash.txt（不是断言失败，是进程级异常）"
        }
        throw "--smoke 未通过。%TEMP%\newapp_smoke.txt: $smokeLine"
    }
    Write-Host "  --smoke OK: $smokeLine" -ForegroundColor Green
    Write-Host "  验证门禁通过（验证的就是即将分发的二进制）" -ForegroundColor Green
}
else {
    Write-Host "[3/6] Skipping verify gate (-SkipVerify) —— 此产物未经验证，不可分发" -ForegroundColor DarkYellow
}

# ---- Step 4: 符号文件留档（移出分发包） ----
# Mapping.txt = Obfuscar 改名映射表（混淆的"答案表"）
# *.pdb       = 混淆前构建的调试符号（含全部原始类名）
# 留档用于解析混淆后的崩溃堆栈；zip/Setup 里绝不允许出现
Write-Host "[4/6] Archiving symbols (Mapping.txt + pdb)..." -ForegroundColor Yellow

if (Test-Path "$DistDir\Mapping.txt") {
    New-Item -ItemType Directory -Force -Path $SymbolArchive | Out-Null
    # -Force: 同版本号重复打包时覆盖旧 Mapping/pdb（旧符号已无意义）
    Move-Item -Force "$DistDir\Mapping.txt" $SymbolArchive
    $pdbs = Get-ChildItem $DistDir -Filter "*.pdb" -ErrorAction SilentlyContinue
    if ($pdbs) {
        $pdbs | Move-Item -Force -Destination $SymbolArchive
        Write-Host "  Archived Mapping.txt + $($pdbs.Count) pdb: $SymbolArchive" -ForegroundColor DarkGray
    }
}
else {
    # -SkipObfuscate：没有 Mapping 可留档；但仍把 pdb 移出分发目录（符号绝不进包）
    $pdbs = Get-ChildItem $DistDir -Filter "*.pdb" -ErrorAction SilentlyContinue
    if ($pdbs) { $pdbs | Move-Item -Force -Destination $env:TEMP }
    Write-Host "  无 Mapping（未混淆），跳过留档" -ForegroundColor DarkGray
}

# ---- Step 4.5: 最终分发产物版本门禁（混淆后仍须一致；Obfuscar 若丢了版本资源这里会拦下） ----
$finalExe = Join-Path $DistDir $ExeName
if (-not (Test-Path $finalExe)) { throw "分发产物缺失：$finalExe" }
$finalVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($finalExe).ProductVersion
if (-not $finalVersion.StartsWith($Version, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "版本不一致：打包参数=$Version，分发产物 ProductVersion=$finalVersion"
}
Write-Host "  版本门禁 OK：分发产物 ProductVersion=$finalVersion" -ForegroundColor DarkGray

# ---- Step 5: Inno Setup ----
if (-not $SkipInstaller -and (Test-Path $InnoSetup)) {
    Write-Host "[5/6] Inno Setup packaging..." -ForegroundColor Yellow
    & $InnoSetup /DAppVersion=$Version $IssFile
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed（退出码 $LASTEXITCODE）" }
    $SetupFile = Get-ChildItem "$OutputDir\$SetupFilter" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host "  Setup: $($SetupFile.FullName)" -ForegroundColor Green
}
else {
    Write-Host "[5/6] Skipping installer" -ForegroundColor DarkGray
}

# ---- Step 6: zip ----
if (-not $SkipZip) {
    Write-Host "[6/6] Creating zip..." -ForegroundColor Yellow
    # 混淆产物缺失时直接失败 —— 不回退 zip 未混淆目录（那会出个看似正常、实则裸奔的包）
    if (-not (Test-Path "$DistDir\$ExeName")) { throw "分发目录不完整: $DistDir\$ExeName 不存在" }
    # 出包前最后一道栅栏：符号/模型不该在包里
    $leaks = @()
    if (Test-Path "$DistDir\Mapping.txt") { $leaks += "Mapping.txt" }
    if (Test-Path "$DistDir\obfuscar.xml") { $leaks += "obfuscar.xml" }
    $pdbLeak = Get-ChildItem $DistDir -Filter "*.pdb" -ErrorAction SilentlyContinue
    if ($pdbLeak) { $leaks += "$($pdbLeak.Count) 个 pdb" }
    if (-not $WithLlmModel -and (Test-Path "$DistDir\runtimes\llm\models\*.gguf")) { $leaks += "gguf 模型" }
    if ($leaks) { throw "分发包里混入不该有的文件：$($leaks -join '、')" }

    if (Test-Path $ZipFile) { Remove-Item -Force $ZipFile }
    Compress-Archive -Path "$DistDir\*" -DestinationPath $ZipFile
    Write-Host "  Zip: $ZipFile" -ForegroundColor Green
}
else {
    Write-Host "[6/6] Skipping zip" -ForegroundColor DarkGray
}

Write-Host "========== Done ==========" -ForegroundColor Cyan
Write-Host ""
Write-Host "Outputs (手动分发 · $AppDisplayName):" -ForegroundColor Yellow
Write-Host "  Setup: $OutputDir\$($SetupFilter -replace '\*', "v$Version")" -ForegroundColor White
Write-Host "  Zip:   $ZipFile" -ForegroundColor White
Write-Host ""
Write-Host "符号留档 (内部使用, 绝不进分发包):" -ForegroundColor Yellow
Write-Host "  $SymbolArchive" -ForegroundColor White
Write-Host ""
Write-Host "备注:" -ForegroundColor DarkGray
Write-Host "  - 与老版并存安装（AppId/目录独立），老版产物不受影响" -ForegroundColor DarkGray
Write-Host "  - 不捆 Qwen3-4B：用户首次用 AI 时在「AI 设置」页下载（ModelScope 直连）" -ForegroundColor DarkGray
Write-Host "  - 目标机需 WebView2 运行时（Win11 自带；Win10 装 'WebView2 Runtime'）" -ForegroundColor DarkGray
Write-Host "  - 混淆后崩溃堆栈用 ReleaseArchive 里的 Mapping.txt 解析" -ForegroundColor DarkGray
