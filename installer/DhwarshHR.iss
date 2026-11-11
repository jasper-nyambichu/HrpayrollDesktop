; Dhwarsh Employee Management: the branch PC installer, by DhwarshHub. No client organisation is named
; here: its name and logo come from the server it connects to.
; Build it with build-installer.ps1, which publishes the app first. One setup .exe installs:
;   - the app, with its own .NET runtime (nothing else to install)
;   - the SecuGen SDK files the app uses (already in the app folder)
;   - the SecuGen fingerprint reader drivers, so a reader plugged in later works straight away
;   - the Microsoft Visual C++ runtime the SecuGen SDK needs, when the PC doesn't have it
; Data (scans waiting to send, this PC's reader activation, fingerprints) lives in C:\ProgramData\Dhwarsh HR and
; is kept on uninstall and on upgrade.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

#define AppName "Dhwarsh Employee Management"
; Earlier names, so an upgrade removes their shortcuts.
#define OldAppName "Dhwarsh HR Attendance"
#define OldAppName2 "Kisiimatt Management System"
#define AppExe "HrpayrollDesktop.exe"
#define DataDir "{commonappdata}\Dhwarsh HR"

; Organisation installers (build-installer.ps1 passes these): the organisation's name, file name and server
; are built in, so the branch PC types nothing. Without them this is the generic installer, which asks for the
; server and is the one published for automatic updates (an update never changes a PC's server).
#ifdef OrgServer
  #define SetupFileName OrgFile + "-Setup-" + AppVersion
  #define VerName AppName + " " + AppVersion + " for " + OrgName
#else
  #define OrgServer ""
  #define OrgName ""
  #define SetupFileName "DhwarshHR-Setup-" + AppVersion
  #define VerName AppName + " " + AppVersion
#endif

[Setup]
; Never change AppId: it is how an upgrade finds the copy already installed.
AppId={{B62696C1-198E-4E15-8415-9E78855D6B83}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#VerName}
AppPublisher=DhwarshHub
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Dhwarsh HR
; The install folder, data folder, update task and setup file name keep the old name on purpose:
; installed PCs keep their data and still find their updates.
DefaultGroupName={#AppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
; Drivers and the C++ runtime are machine-wide, so setup needs an administrator.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 or 11.
MinVersion=10.0
OutputDir=Output
OutputBaseFilename={#SetupFileName}
SetupIconFile=..\HrpayrollDesktop\Assets\dhwarsh.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app is closed by [Code] below, because it asks "Exit?" when Windows asks it to close.
CloseApplications=no
SetupLogging=yes

[Tasks]
Name: "autostart"; Description: "Start the attendance app automatically when Windows starts (recommended for the branch PC)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Dirs]
; Every Windows user of the branch PC can record scans, not just the one who installed it.
Name: "{#DataDir}"; Permissions: users-modify

[Files]
Source: "out\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\SecuGen\Drivers\*"; DestDir: "{app}\Drivers\SecuGen"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
; Automatic updates. In Program Files, so only administrators can change the script or the key it trusts.
Source: "updater\update.ps1"; DestDir: "{app}\updater"; Flags: ignoreversion
Source: "updater\register-tasks.ps1"; DestDir: "{app}\updater"; Flags: ignoreversion
Source: "updater\update-public-key.xml"; DestDir: "{app}\updater"; Flags: ignoreversion

[InstallDelete]
; Shortcuts left by earlier names of the app (1.3.x "Dhwarsh HR Attendance", 1.4.0 "Kisiimatt Management System").
Type: filesandordirs; Name: "{commonprograms}\Dhwarsh HR"
Type: files; Name: "{autodesktop}\{#OldAppName}.lnk"
Type: files; Name: "{commonstartup}\{#OldAppName}.lnk"
Type: filesandordirs; Name: "{commonprograms}\{#OldAppName2}"
Type: files; Name: "{autodesktop}\{#OldAppName2}.lnk"
Type: files; Name: "{commonstartup}\{#OldAppName2}.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Reinstall fingerprint reader drivers"; Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\Drivers\SecuGen\*.inf"" /subdirs /install"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: autostart

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the Microsoft Visual C++ runtime..."; Check: VCRuntimeNeeded; Flags: waituntilterminated
; Adds every SecuGen reader model's signed driver to Windows and installs it on any reader already plugged in.
; Output goes to driver-install.log in the data folder, for support.
Filename: "{cmd}"; Parameters: "/c """"{sys}\pnputil.exe"" /add-driver ""{app}\Drivers\SecuGen\*.inf"" /subdirs /install > ""{#DataDir}\driver-install.log"" 2>&1"""; StatusMsg: "Installing the SecuGen fingerprint reader drivers..."; Flags: runhidden waituntilterminated
; The hourly update check (as SYSTEM) and the task that reopens the app after an update.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\updater\register-tasks.ps1"" -AppDir ""{app}"""; StatusMsg: "Setting up automatic updates..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\updater\register-tasks.ps1"" -Remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveUpdateTasks"

[UninstallDelete]
; Only the app's own leftovers. Attendance data in {#DataDir} is kept on purpose.
Type: filesandordirs; Name: "{app}\Drivers"
Type: filesandordirs; Name: "{app}\updates"

[Code]
// The SecuGen SDK is built with Visual C++ 2015-2022 and needs its runtime (14.40 or newer is current).
function VCRuntimeNeeded: Boolean;
var
  Installed, Minor: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed)
    and (Installed = 1)
    and RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Minor', Minor)
    and (Minor >= 40));
end;

procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  // Scans are saved to disk as they happen, so stopping it loses nothing.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

// ---- Which organisation's server this PC uses ----
// Each organisation runs its own server. The address is saved in {#DataDir}\server.json, which updates never
// touch, so an automatic update can never move a PC to another organisation. A PC with no server.json uses
// the address in appsettings.json (the PCs installed before this step existed).
// Silent installs: pass /SERVER=https://... to set it; without it the PC keeps what it has.

var
  ServerPage: TInputQueryWizardPage;
  ServerUrl: String;
  ServerChosen: Boolean;

function ServerFile: String;
begin
  Result := ExpandConstant('{#DataDir}\server.json');
end;

// The text value of "Key":"..." in compact JSON, or '' (also for null or a missing key).
function JsonText(const Json, Key: String): String;
var
  P: Integer;
  Rest: String;
begin
  Result := '';
  P := Pos('"' + Key + '":"', Json);
  if P = 0 then Exit;
  Rest := Copy(Json, P + Length(Key) + 4, MaxInt);
  P := Pos('"', Rest);
  if P > 0 then Result := Copy(Rest, 1, P - 1);
end;

function SavedServer: String;
var
  Json: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(ServerFile, Json) then Result := JsonText(String(Json), 'BackendBaseUrl');
end;

function CleanUrl(Url: String): String;
begin
  Result := Trim(Url);
  while (Length(Result) > 0) and (Result[Length(Result)] = '/') do
    SetLength(Result, Length(Result) - 1);
end;

function AlreadyInstalled: Boolean;
var
  Key: String;
begin
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B62696C1-198E-4E15-8415-9E78855D6B83}_is1';
  Result := RegKeyExists(HKLM64, Key) or RegKeyExists(HKLM32, Key);
end;

// Asks the server who it belongs to, so the installer can show the organisation's name before saving.
function Reach(const Url: String; var Name: String): Boolean;
var
  Http: Variant;
  Status: Integer;
  Body: String;
begin
  Result := False;
  Name := '';
  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.SetTimeouts(8000, 8000, 15000, 15000);
    Http.Open('GET', Url + '/public/organisation', False);
    Http.Send('');
    Status := Http.Status;
    Body := Http.ResponseText;
    if (Status = 200) and (Pos('"success":true', Body) > 0) then
    begin
      Name := JsonText(Body, 'name');
      Result := True;
    end;
  except
    Result := False;
  end;
end;

// The server this PC uses now: its server.json, or else the address in the installed app's appsettings.json
// (PCs installed before server.json existed). '' on a PC that has never had the app.
function CurrentServer: String;
var
  Key, Dir: String;
  Json: AnsiString;
begin
  Result := SavedServer;
  if Result <> '' then Exit;
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B62696C1-198E-4E15-8415-9E78855D6B83}_is1';
  if RegQueryStringValue(HKLM64, Key, 'Inno Setup: App Path', Dir) or RegQueryStringValue(HKLM32, Key, 'Inno Setup: App Path', Dir) then
    if LoadStringFromFile(AddBackslash(Dir) + 'appsettings.json', Json) then
    begin
      // appsettings.json is indented ("Key": "value"); make it compact before reading.
      Result := String(Json);
      StringChangeEx(Result, '": "', '":"', True);
      Result := CleanUrl(JsonText(Result, 'BackendBaseUrl'));
    end;
end;

procedure InitializeWizard;
var
  Hint: String;
begin
  ServerUrl := CleanUrl(ExpandConstant('{param:SERVER|}'));
  if '{#OrgServer}' <> '' then
  begin
    if ServerUrl = '' then ServerUrl := '{#OrgServer}';
    // Run silently, an organisation installer sets up a new PC or one already on this server, but never
    // moves a PC from another organisation's server without someone confirming it on screen.
    ServerChosen := (CurrentServer = '') or (CompareText(CurrentServer, ServerUrl) = 0);
    Hint := 'This installer is for {#OrgName}. The PC will send attendance to {#OrgName}''s server. Click Next to check the connection.';
  end
  else
  begin
    ServerChosen := ServerUrl <> '';
    Hint := 'Type the server address your organisation gave you, starting with https://. Setup checks it and shows the organisation''s name before continuing.';
    if AlreadyInstalled then
      Hint := Hint + #13#10#13#10 + 'Leave it as it is to keep the server this PC already uses.';
  end;

  ServerPage := CreateInputQueryPage(wpSelectTasks, 'Organisation server',
    'Which organisation''s server should this PC send attendance to?', Hint);
  ServerPage.Add('Server address:', False);
  if ServerUrl <> '' then
    ServerPage.Values[0] := ServerUrl
  else
    ServerPage.Values[0] := SavedServer;
  // An organisation's installer only ever connects to that organisation.
  if '{#OrgServer}' <> '' then ServerPage.Edits[0].ReadOnly := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Url, Name, Old, Shown: String;
begin
  Result := True;
  if WizardSilent or (CurPageID <> ServerPage.ID) then Exit;

  Url := CleanUrl(ServerPage.Values[0]);
  Old := CurrentServer;
  if Url = '' then
  begin
    if AlreadyInstalled and ('{#OrgServer}' = '') then
    begin
      ServerChosen := False;   // keep whatever this PC uses now
      Exit;
    end;
    MsgBox('Type the server address for this organisation.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if Pos('https://', Lowercase(Url)) <> 1 then
  begin
    MsgBox('The server address must start with https://', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Reach(Url, Name) then
  begin
    Shown := Name;
    if Shown = '' then Shown := '(the organisation has not set its name yet)';
    Result := MsgBox('This PC will send attendance to:' + #13#10#13#10 + Shown + #13#10 + Url + #13#10#13#10 +
      'Is this the right organisation?', mbConfirmation, MB_YESNO) = IDYES;
  end
  else
    Result := MsgBox('Setup could not reach ' + Url + #13#10#13#10 +
      'Check the address and the internet connection. Use it anyway?', mbError, MB_YESNO) = IDYES;

  if Result and (Old <> '') and (CompareText(Old, Url) <> 0) then
    Result := MsgBox('This PC was set up for a different server:' + #13#10 + Old + #13#10#13#10 +
      'After switching, it must be activated again, and scans it has not sent yet will not reach the new server. Switch?',
      mbConfirmation, MB_YESNO) = IDYES;

  if Result then
  begin
    ServerUrl := Url;
    ServerChosen := True;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and ServerChosen then
    if not SaveStringToFile(ServerFile, '{"Agent":{"BackendBaseUrl":"' + ServerUrl + '"}}', False) then
      MsgBox('Could not save the server address to ' + ServerFile + '.', mbError, MB_OK);
end;
