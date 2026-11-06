; MeetWise installer. Build with build-installer.ps1 (needs Inno Setup 6).
; Per-user install, no admin rights. Settings, meetings and models live outside the install folder, so upgrades never touch them:
;   %AppData%\MeetWise (settings, voices), %LocalAppData%\MeetWise (database, downloaded models)

#define MyAppName "MeetWise"
#define MyAppExeName "MeetWise.App.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\MeetWise"
#endif

[Setup]
AppId={{5B1D7A52-8C4E-4F0B-9A63-2E7D4C9F1B88}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={localappdata}\Programs\MeetWise
DefaultGroupName=MeetWise
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=MeetWise-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start MeetWise when I sign in (so meetings and reminders are noticed)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MeetWise"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if MsgBox('Your meetings, settings and downloaded models are kept. Remove them manually from %AppData%\MeetWise and %LocalAppData%\MeetWise if you want a clean slate.', mbInformation, MB_OKCANCEL) = IDCANCEL then
    Result := False;
end;
