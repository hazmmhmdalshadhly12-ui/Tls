; HAZEM Tunnel — Inno Setup script
; Build on Windows with Inno Setup 6+:
;   ISCC.exe installer\hazem-tunnel.iss
; The EXE must exist at publish\win-x64\HAZEM.Tunnel.exe (run scripts\publish.ps1 first).

#define AppName "HAZEM Tunnel"
#define AppVersion "1.2.0"
#define AppPublisher "HAZEM"
#define AppExeName "HAZEM.Tunnel.exe"

[Setup]
; AppId must be a VALID GUID — it identifies the app for upgrades/uninstall forever.
AppId={{EC754B65-815A-43B2-9F98-D4528927DAF0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\HAZEM Tunnel
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\dist
OutputBaseFilename=HAZEM-Tunnel-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\win-x64\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Messages]
; Honest SmartScreen note for unsigned builds
FinishedLabelNoIcons=Setup has finished installing [name].%n%nNote: this build is not code-signed, so Windows SmartScreen may verify it on first launch (choose "More info" then "Run anyway").
FinishedLabel=Setup has finished installing [name].%n%nNote: this build is not code-signed, so Windows SmartScreen may verify it on first launch (choose "More info" then "Run anyway").
