; LDAssistant 安装包脚本（融合 file-viewer 预览组件）
; 由 CI 编译: iscc /DAppVersion="3.1.0" LDAssistant.iss

#define AppName "工程助手 LDAssistant"
#ifndef AppVersion
  #define AppVersion "3.1.0"
#endif
#define AppExeName "LDAssistant.exe"
#define OutputBaseFilename "LDAssistant-Setup-v{#AppVersion}"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LDAssistant
DefaultDirName={autopf}\LDAssistant
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=app_icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
; 卸载时保留用户数据和更新文件
Uninstallable=yes
CreateUninstallRegKey=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; WPF 主程序 + 所有依赖
Source: "dist\LDAssistant\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app_icon.ico"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app_icon.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// 安装前清理旧版本的 pdb 文件
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
