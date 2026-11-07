; Inno Setup script for XTreme. Build with tools/make-installer.ps1.
#define AppName "XTreme"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
; the AppId never changes: a new installer upgrades the install in place
AppId={{6F1C2B7E-3D54-4A8B-9C0E-2A7B5D1E8F43}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=xtrimdr
DefaultDirName={autopf}\XTreme
DefaultGroupName=XTreme
UninstallDisplayIcon={app}\XTreme.exe
SetupIconFile=..\src\XtremeDaw.App\Assets\XTreme.ico
LicenseFile=..\LICENSE
OutputDir=..\artifacts
; no version in the name: the README's download button links to releases/latest/download/XTreme-Setup.exe
OutputBaseFilename=XTreme-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; .xtp projects open with a double click (the [Registry] entries)
ChangesAssociations=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
spanish.AssocXtp=Asociar archivos .xtp a XTreme
english.AssocXtp=Associate .xtp files with XTreme

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
; checked by default (#254); unchecked leaves .xtp files with whatever opens them today
Name: "xtpassoc"; Description: "{cm:AssocXtp}"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[Registry]
; HKA: the machine's classes in an admin install, the user's otherwise
Root: HKA; Subkey: "Software\Classes\.xtp"; ValueType: string; ValueName: ""; ValueData: "XTreme.Project"; Flags: uninsdeletevalue; Tasks: xtpassoc
Root: HKA; Subkey: "Software\Classes\.xtp\OpenWithProgids"; ValueType: string; ValueName: "XTreme.Project"; ValueData: ""; Flags: uninsdeletevalue; Tasks: xtpassoc
Root: HKA; Subkey: "Software\Classes\XTreme.Project"; ValueType: string; ValueName: ""; ValueData: "XTreme Project"; Flags: uninsdeletekey; Tasks: xtpassoc
Root: HKA; Subkey: "Software\Classes\XTreme.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\XTreme.exe,0"; Tasks: xtpassoc
Root: HKA; Subkey: "Software\Classes\XTreme.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\XTreme.exe"" ""%1"""; Tasks: xtpassoc

[Icons]
Name: "{group}\XTreme"; Filename: "{app}\XTreme.exe"
Name: "{autodesktop}\XTreme"; Filename: "{app}\XTreme.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\XTreme.exe"; Description: "{cm:LaunchProgram,XTreme}"; Flags: nowait postinstall skipifsilent
