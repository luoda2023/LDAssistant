; 工程助手 LDAssistant v10 — Inno Setup 安装脚本
; 将 dist/工程助手_v10/ 打包为安装程序
; 使用 Inno Setup 6.x 编译

#define MyAppName "工程助手"
#define MyAppVersion "10.0"
#define MyAppPublisher "LDAssistant Team"
#define MyAppURL "https://example.com"
#define MyAppExeName "工程助手.exe"

[Setup]
AppId={{B8F4A7D3-2E5C-4A9B-8F1D-3C6E2A9B5F7E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=工程助手_v10_安装包
SetupIconFile=app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
; 优化压缩：跳过已压缩的文件
AllowNoFiles=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"; Flags: checkedonce

[Files]
; 主程序及其依赖 (递归包含所有子目录)
Source: "dist\工程助手_v10\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsWin64
; 确保 DLL 目录被正确复制
Source: "dist\工程助手_v10\_internal\*"; DestDir: "{app}\_internal"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; 关闭正在运行的进程 (可选)
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im 工程助手.exe"; Flags: runhidden skipifdoesntexist

[Code]
// 检查是否 64 位系统
function IsWin64: Boolean;
begin
  Result := IsWin64;
end;

// 安装前检查磁盘空间（要求至少 2GB）
function InitializeSetup: Boolean;
begin
  Result := True;
  if GetSpaceOnDisk(ExpandConstant('{autopf}'), mbDiv) < 2048 then
  begin
    MsgBox('安装需要至少 2GB 可用磁盘空间。请释放空间后重试。', mbError, MB_OK);
    Result := False;
  end;
end;
