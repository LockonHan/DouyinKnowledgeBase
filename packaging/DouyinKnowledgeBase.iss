; 抖音知识库 —— Inno Setup 6 安装脚本
;
; 编译前请先运行：powershell -ExecutionPolicy Bypass -File packaging\build.ps1
; 生成 packaging\payload 后再编译本脚本（或直接用 build.ps1 -MakeInstaller）。
;
; 说明：中文语言包 ChineseSimplified.isl 已随仓库内置（packaging\ChineseSimplified.isl），
;       无需依赖 Inno Setup 安装目录中的语言文件，离线也可编译；缺失时回退英文。
;
; 安装器特性：
;   * 每用户安装（PrivilegesRequired=lowest），不需要管理员权限
;   * 安装向导中可自选安装位置（DisableDirPage=no）
;   * 安装后保留 runtime\models 与 data，卸载不删除用户数据

#define AppName "抖音知识库"
#define AppNameEn "DouyinKnowledgeBase"
#define AppVersion "1.1.0"
#define AppPublisher "LockonHan"
#define AppExeName "DouyinKnowledgeBase.exe"

[Setup]
AppId={{7C4A1E52-3B7E-4E7A-9C2B-2F5A6D8E1A34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppNameEn}
DefaultGroupName={#AppName}
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
OutputDir=out
OutputBaseFilename={#AppNameEn}-Setup-{#AppVersion}-x64
SetupIconFile=..\DouyinKnowledgeBase\Assets\AppIcon.ico
UninstallDisplayIcon={app}\app\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl,compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "launchapp"; Description: "安装完成后启动{#AppName}"; GroupDescription: "附加任务："

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\runtime\models"
Name: "{app}\runtime\browsers"
Name: "{app}\runtime\bin"
Name: "{app}\data"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"; Tasks: desktopicon

[Run]
; 应用内登录抖音需要 WebView2 运行时；仅在缺失时静默安装（引导程序已随包内置，免管理员）。
Filename: "{app}\runtime\bin\webview2\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 运行时（应用内登录抖音所需）..."; Flags: waituntilterminated skipifdoesntexist; Check: WebView2Missing
Filename: "{app}\app\{#AppExeName}"; Description: "启动{#AppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp

[UninstallDelete]
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\tools"
Type: filesandordirs; Name: "{app}\runtime\python"
Type: filesandordirs; Name: "{app}\runtime\bin"
Type: filesandordirs; Name: "{app}\runtime\browsers"

[Code]
{ 检测 WebView2 运行时是否已安装（按机器 / 按用户两种安装位置）。 }
function WebView2Missing: Boolean;
var
  Version: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') then
    Result := False
  else if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') then
    Result := False
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') then
    Result := False;
end;
