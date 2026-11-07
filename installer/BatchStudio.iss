; Inno Setup script - built by GitHub Actions (see .github/workflows/build.yml)
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C1E9B0E-4F7A-4B7C-9E61-2B7D0B6A5A11}
AppName=BATCH STUDIO
AppVersion={#AppVersion}
AppPublisher=BATCH STUDIO
DefaultDirName={autopf}\BATCH STUDIO
DefaultGroupName=BATCH STUDIO
DisableProgramGroupPage=yes
OutputBaseFilename=BatchStudio-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
UninstallDisplayIcon={app}\BatchStudio.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BATCH STUDIO"; Filename: "{app}\BatchStudio.exe"
Name: "{autodesktop}\BATCH STUDIO"; Filename: "{app}\BatchStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BatchStudio.exe"; Description: "{cm:LaunchProgram,BATCH STUDIO}"; Flags: nowait postinstall skipifsilent
