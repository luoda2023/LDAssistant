; 工程助手 LDAssistant v10 — Inno Setup 安装脚本
; 使用 Inno Setup 6.x 编译

#define MyAppName "工程助手 LDAssistant"
#define MyAppVersion "10.0"
#define MyAppPublisher "LDAssistant Team"
#define MyAppURL "https://example.com"
#define MyAppExeName "工程助手.exe"

[Setup]
AppId={{8E9F5B2A-0C3D-4E1A-9B7C-6D2F5E8A1B3C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=J:\WorkBuddy-work\csres-standards\dist_v10\installer
OutputBaseFilename=工程助手_v10_安装程序
SetupIconFile=J:\WorkBuddy-work\csres-standards\app_icon.ico
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce

[Files]
Source: "J:\WorkBuddy-work\csres-standards\dist_v10\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "J:\WorkBuddy-work\csres-standards\dist_v10\*"; DestDir: "{app}\data"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 工程助手"; Flags: postinstall nowait skipifsilent

[UninstallRun]
