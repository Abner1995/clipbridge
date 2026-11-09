; ============================================================
; CreatFlow 单机版 安装包脚本 (Inno Setup 6)
; 用法: 由 scripts/build-newapp.ps1（单机版分支自动识别 / -Standalone）调用
;       iscc /DAppVersion=1.0.2 setup-standalone.iss
;
; 与联网版 setup-newapp.iss 的关系：
;   AppId / 安装目录 / 快捷方式 / 产物名全部独立 —— 同一台机器可以并存安装两版，
;   卸载其中一个不会影响另一个。数据目录是共享的（%LocalAppData%\CreatFlow），
;   但两版各自的账号体系不交叉：单机版只用自己的固定本机会员目录。
;
; ⚠️ 品牌项（二次开发时改这里，别散落到别处）：
;   AppName / AppPublisher / AppURL / DefaultDirName。
;   当前值沿用 CreatFlow 自有品牌；二次开发者可只改本文件顶部。
; ============================================================

; /DAppVersion 覆盖必须不报错也不被忽略：裸 #define 重复定义行为不稳 → #ifndef 守卫
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName       "CreatFlow 单机版"
#define AppPublisher  "CreatFlow"
#define AppURL        "https://creatflow.xuguotian.top"
#define AppExeName    "CreatFlow.NewApp.exe"

[Setup]
; AppId 与老版 / 联网新版都不同 —— 三版并存，控制面板三条记录
AppId={{611AF737-CBFD-4AE9-9F30-099332FEF7BF}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/download/
DefaultDirName={autopf}\CreatFlowStandalone
DefaultGroupName={#AppName}
AllowNoIcons=yes
; 程序是 win-x64 自包含：不加这两条 Inno 默认按 32 位装，{autopf} 会解析到 Program Files (x86)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 输出文件名（与联网版的 CreatFlow-NewApp-Setup-* 不撞）
OutputDir=..\Output
OutputBaseFilename=CreatFlow-Standalone-Setup-v{#AppVersion}
; 安装界面
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 需要管理员权限（写入 Program Files）
PrivilegesRequired=admin
; 签名（如果有证书）
; SignTool=MySignTool

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他快捷方式:"

[Files]
; 主程序（单机版混淆后的产物 + runtime 资产 + runtimes\llm 引擎；4B 模型不随包）
; 单机版构建目录与联网版分开（StandalonePublishObfuscated），不会打到另一版的产物
; Excludes 挡符号文件：Mapping.txt/pdb 已由构建脚本留档移出，此处再防一手
Source: "StandalonePublishObfuscated\*"; DestDir: "{app}"; Excludes: "Mapping.txt,*.pdb,appsettings.Production.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";                     Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}";                 Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";                Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

; ⚠️ 刻意没有 [UninstallDelete] 用户数据目录 —— 卸载/升级绝不能删：
;   本地库 %LocalAppData%\CreatFlow\accounts\{固定会员}\creatflow.db 是用户的全部业务数据
;   （发布记录 / 合集 / 账号会话 / 异步任务），另有每日自动备份在同目录 backups\。
;   用户要清数据请走「账号设置 → 数据与备份 → 导出」后自行删除目录。

[Code]
// 安装完成后提示：单机版没有登录与激活，装完即用；数据位置与备份方式讲清楚
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox('{#AppName} 安装完成！' + #13#10 +
           '本版本无需登录、无需激活码，双击即可使用（平台账号在软件内登录）。' + #13#10 +
           '程序依赖 WebView2 运行时（Win11 自带；Win10 缺失时请到微软官网安装 "WebView2 Runtime"）。' + #13#10 +
           '全部数据保存在本机（不会上传服务器）:' + #13#10 +
           ExpandConstant('{localappdata}\CreatFlow\accounts\') + #13#10 +
           '可在「账号设置 → 数据与备份」导出 zip 备份 / 换机恢复。' + #13#10 +
           '卸载不会删除数据目录；如需清理请自行删除上述目录。', mbInformation, MB_OK);
end;
