; Inno Setup script - built by GitHub Actions (see .github/workflows/build.yml)
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C1E9B0E-4F7A-4B7C-9E61-2B7D0B6A5A11}
AppName=DEEPMATH
AppVersion={#AppVersion}
AppPublisher=DEEPMATH
DefaultDirName={autopf}\DEEPMATH
DefaultGroupName=DEEPMATH
DisableProgramGroupPage=yes
UsePreviousGroup=no
OutputBaseFilename=DEEPMATH-Setup-{#AppVersion}
SetupIconFile=..\src\BatchStudio.App\Assets\deepmath.ico
AppVerName=DEEPMATH {#AppVersion}
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

[InstallDelete]
; shortcuts of the old name (BATCH STUDIO) are replaced by DEEPMATH ones
Type: files; Name: "{autoprograms}\BATCH STUDIO.lnk"
Type: filesandordirs; Name: "{autoprograms}\BATCH STUDIO"
Type: files; Name: "{autodesktop}\BATCH STUDIO.lnk"

[Icons]
Name: "{group}\DEEPMATH"; Filename: "{app}\BatchStudio.exe"
Name: "{autodesktop}\DEEPMATH"; Filename: "{app}\BatchStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BatchStudio.exe"; Description: "{cm:LaunchProgram,DEEPMATH}"; Flags: nowait postinstall skipifsilent
