#ifndef MyAppVersion
#define MyAppVersion "1.0.1"
#endif

[Setup]
AppId={{A7C3E5B1-9D2F-4E8A-B6C1-RemoteDesktopLAN}
AppName=Remote Desktop LAN
AppVersion={#MyAppVersion}
AppPublisher=Remote Desktop LAN
DefaultDirName={autopf}\Remote Desktop LAN
DefaultGroupName=Remote Desktop LAN
OutputDir=..\dist
OutputBaseFilename=RemoteDesktopLAN-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\RemoteDesktop.App.exe

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成"; GroupDescription: "追加タスク:"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Remote Desktop LAN"; Filename: "{app}\RemoteDesktop.App.exe"
Name: "{autodesktop}\Remote Desktop LAN"; Filename: "{app}\RemoteDesktop.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RemoteDesktop.App.exe"; Description: "Remote Desktop LAN を起動"; Flags: nowait postinstall skipifsilent
