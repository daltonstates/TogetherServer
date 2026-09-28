#ifndef AppSource
  #define AppSource "..\local-data\release-candidate\TogetherServer.exe"
#endif
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef OutputDirectory
  #define OutputDirectory "..\local-data\installer"
#endif

[Setup]
AppId={{B77B506D-42E1-4B06-B566-C287165BD234}
AppName=TogetherServer
AppVerName=TogetherServer {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=TogetherServer
AppPublisherURL=https://github.com/daltonstates/TogetherServer
AppSupportURL=https://github.com/daltonstates/TogetherServer/issues
AppUpdatesURL=https://github.com/daltonstates/TogetherServer/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=TogetherServer
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=TogetherServer installer
DefaultDirName={localappdata}\Programs\TogetherServer
DefaultGroupName=TogetherServer
DisableProgramGroupPage=auto
AllowNoIcons=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDirectory}
OutputBaseFilename=TogetherServer-Setup-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AppMutex=Local\TogetherServer.Application
UninstallDisplayIcon={app}\TogetherServer.exe
UsePreviousAppDir=yes
UsePreviousGroup=yes

[Files]
Source: "{#AppSource}"; DestDir: "{app}"; DestName: "TogetherServer.exe"; Flags: replacesameversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{group}\TogetherServer"; Filename: "{app}\TogetherServer.exe"; WorkingDir: "{app}"; Comment: "Host or join a game server with TogetherServer"
Name: "{autodesktop}\TogetherServer"; Filename: "{app}\TogetherServer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TogetherServer.exe"; Parameters: "{code:GetConfigureParameters}"; Flags: runhidden waituntilterminated; Check: ShouldConfigure
Filename: "{app}\TogetherServer.exe"; Description: "Open TogetherServer"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\TogetherServer.exe"; Parameters: "--installer-remove-startup"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
Type: files; Name: "{app}\TogetherServer.exe.previous"

[Code]
var
  RolePage: TInputOptionWizardPage;
  PreferencePage: TInputOptionWizardPage;

function BooleanParameter(const Name: String): Boolean;
var
  Value: String;
begin
  Value := Lowercase(ExpandConstant('{param:' + Name + '|0}'));
  Result := (Value = '1') or (Value = 'yes') or (Value = 'true');
end;

procedure InitializeWizard;
var
  InitialMode: String;
begin
  RolePage := CreateInputOptionPage(wpSelectDir,
    'Optional app setup', 'Where should TogetherServer open?',
    'Choose a starting page, or decide later in the app. This does not configure, start, or expose a game server.',
    True, False);
  RolePage.Add('Decide later in TogetherServer');
  RolePage.Add('Host game servers');
  RolePage.Add('Join a friend''s server');
  InitialMode := Lowercase(ExpandConstant('{param:INITIALMODE|choose}'));
  if InitialMode = 'host' then
    RolePage.SelectedValueIndex := 1
  else if InitialMode = 'friend' then
    RolePage.SelectedValueIndex := 2
  else
    RolePage.SelectedValueIndex := 0;

  PreferencePage := CreateInputOptionPage(RolePage.ID,
    'Optional Windows preferences', 'Choose any conveniences you want now',
    'Both settings are off by default and can be changed later in TogetherServer.',
    False, False);
  PreferencePage.Add('Open TogetherServer at Windows sign-in');
  PreferencePage.Add('Close to the tray instead of quitting');
  PreferencePage.Values[0] := BooleanParameter('STARTWITHWINDOWS');
  PreferencePage.Values[1] := BooleanParameter('CLOSETOTRAY');
end;

function ShouldConfigure: Boolean;
begin
  Result := (RolePage.SelectedValueIndex <> 0) or PreferencePage.Values[0] or PreferencePage.Values[1];
end;

function GetConfigureParameters(Param: String): String;
begin
  Result := '--installer-configure';
  if RolePage.SelectedValueIndex = 1 then
    Result := Result + ' --mode host'
  else if RolePage.SelectedValueIndex = 2 then
    Result := Result + ' --mode friend';
  if PreferencePage.Values[0] then
    Result := Result + ' --launch-at-login';
  if PreferencePage.Values[1] then
    Result := Result + ' --close-to-tray';
end;
