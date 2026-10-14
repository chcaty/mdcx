; Build only via scripts/package-windows.ps1. No download or external runtime is used.
#ifndef PayloadDirectory
  #error PayloadDirectory must be supplied
#endif
#ifndef ArtifactDirectory
  #error ArtifactDirectory must be supplied
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef BuildLabel
  #define BuildLabel "local"
#endif
#define AppName "MDCx Organizer Preview"

[Setup]
AppId={{1BD456B0-B27A-44EC-B38C-550684593F2B}
AppName={#AppName}
AppVersion={#AppVersion}-{#BuildLabel}
VersionInfoVersion={#AppVersion}.0
AppPublisher=MDCx Organizer contributors
DefaultDirName={localappdata}\Programs\MDCxOrganizer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#ArtifactDirectory}
OutputBaseFilename=MDCxOrganizer-{#AppVersion}-{#BuildLabel}-win-x64-setup
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Organizer.Desktop.exe
LicenseFile={#PayloadDirectory}\licenses\MDCx-LICENSE.txt
InfoBeforeFile={#PayloadDirectory}\README-WINDOWS.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableStartupPrompt=yes
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
SignedUninstaller=no
SetupLogging=yes

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\Organizer.Desktop.exe"; WorkingDir: "{app}"

; Deliberately no [Run], [InstallDelete] or [UninstallDelete] section.
; Inno removes its logged files only. User indexes and scanned media are never owned.
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallPath, DataPath: String;
begin
  Result := '';
  InstallPath := AddBackslash(Lowercase(ExpandConstant('{app}')));
  DataPath := AddBackslash(Lowercase(ExpandConstant('{localappdata}\MDCxOrganizer')));
  if (Pos(InstallPath, DataPath) = 1) or (Pos(DataPath, InstallPath) = 1) then
    Result := 'Choose a separate program folder. The MDCxOrganizer local data directory and its parent folders cannot be used for installed binaries.';
  if not FileExists(ExpandConstant('{sys}\ucrtbase.dll')) then
    Result := 'The Windows Universal C Runtime is missing. Install supported Windows updates before installing this preview.';
end;
