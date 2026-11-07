; Kisiimatt Management System (formerly Dhwarsh HR Attendance): the branch PC installer.
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

#define AppName "Kisiimatt Management System"
; The name before the rename, so an upgrade removes its old shortcuts.
#define OldAppName "Dhwarsh HR Attendance"
#define AppExe "HrpayrollDesktop.exe"
#define DataDir "{commonappdata}\Dhwarsh HR"

[Setup]
; Never change AppId: it is how an upgrade finds the copy already installed.
AppId={{B62696C1-198E-4E15-8415-9E78855D6B83}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Kisiimatt
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
OutputBaseFilename=DhwarshHR-Setup-{#AppVersion}
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
; Shortcuts from before the rename to {#AppName}.
Type: filesandordirs; Name: "{commonprograms}\Dhwarsh HR"
Type: files; Name: "{autodesktop}\{#OldAppName}.lnk"
Type: files; Name: "{commonstartup}\{#OldAppName}.lnk"

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
