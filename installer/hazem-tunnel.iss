; HAZEM Tunnel — Inno Setup script
; Build on Windows with Inno Setup 6+:
;   ISCC.exe installer\hazem-tunnel.iss
; The EXE must exist at publish\win-x64\HAZEM.Tunnel.exe (run scripts\publish.ps1 first).

#define AppName "HAZEM Tunnel"
#define AppVersion "1.1.0"
#define AppPublisher "HAZEM"
#define AppExeName "HAZEM.Tunnel.exe"

[Setup]
AppId={{7E1B6C4E-5B44-4B9F-9C2A-HAZEMTUNNEL10}
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
