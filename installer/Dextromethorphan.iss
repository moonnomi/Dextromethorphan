#ifndef Runtime
  #define Runtime "win-x64"
#endif
#define AppName "Dextromethorphan"
#define AppVersion "0.1.0"
#define Root ".."

[Setup]
AppId={{A78EC155-3F40-49A5-B2B2-8F8B934FC6C5}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir={#Root}\artifacts
OutputBaseFilename=Dextromethorphan-{#Runtime}-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
PrivilegesRequired=lowest
WizardStyle=modern

[Files]
Source: "{#Root}\artifacts\publish\{#Runtime}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Dextromethorphan.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Dextromethorphan.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\Dextromethorphan.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
