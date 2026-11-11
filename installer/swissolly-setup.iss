; SwissOlly Inno Setup Script
; Compiles into a single SwissOllySetup.exe installer per architecture.
;
; Parameters (passed via /D on the command line):
;   MyAppVersion  - Version string (e.g. "1.2.0"); must be numeric, it is also the file version
;   MyAppArch     - Architecture: "x64compatible" (default) or "arm64"
;   MyPublishDir  - Publish output directory (default: "..\publish\x64")
;   MyOutputSuffix - Suffix for the output filename (default: "-x64")
;
; Prerequisites:
;   1. Install Inno Setup: https://jrsoftware.org/isdl.php
;   2. Build the app:
;        dotnet publish SwissOlly.App/SwissOlly.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish/x64
;   3. Open this file in Inno Setup Compiler and click Build > Compile
;   4. The installer will be created in installer/Output/

#define MyAppName "SwissOlly"
#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#define MyAppPublisher "SwissOlly"
#define MyAppURL "https://github.com/swissolly/SwissOlly"
#define MyAppExeName "SwissOlly.exe"
#define MyAppDescription "System Telemetry Dashboard"

; Architecture defaults
#ifndef MyAppArch
  #define MyAppArch "x64compatible"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\publish\x64"
#endif
#ifndef MyOutputSuffix
  #define MyOutputSuffix "-x64"
#endif

[Setup]
; Unchanged since the app was called Vaktr, so upgrading an old install replaces it rather
; than installing alongside it. Never change this GUID.
AppId={{B8F2A1C4-5D6E-4F7A-8B9C-0D1E2F3A4B5C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=SwissOllySetup-{#MyAppVersion}{#MyOutputSuffix}
SetupIconFile=..\SwissOlly.App\Assets\SwissOlly.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; The HKCU Run entry is deliberate: with consent-prompt UAC, Setup's HKCU is the person who
; installed and will use SwissOlly. See the caveat on GrantPerformanceLogUsers below.
UsedUserAreasWarning=no
ArchitecturesAllowed={#MyAppArch}
ArchitecturesInstallIn64BitMode={#MyAppArch}
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchonstartup"; Description: "Start SwissOlly in the notification area when I sign in"; GroupDescription: "Startup:"; Flags: unchecked
Name: "exactfps"; Description: "Allow exact FPS: adds you to the ""Performance Log Users"" group (works after you sign out and back in; removed on uninstall)"; GroupDescription: "In-game overlay:"

[Files]
; Include everything from the publish folder
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; SwissOlly's license and the licenses of the libraries it ships, beside the exe.
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDescription}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "{#MyAppDescription}"

[InstallDelete]
; Upgrading a pre-rebrand install: drop the old Vaktr.exe, Vaktr.dll and companions left in {app}.
Type: files; Name: "{app}\Vaktr*"

[Registry]
; The pre-rebrand startup entry points at an exe that no longer exists.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Vaktr"; Flags: deletevalue
; Start with Windows (optional). The same value the app's "Start with Windows" setting writes;
; --tray starts it in the notification area instead of opening the window.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: launchonstartup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings and history in %LocalAppData%\SwissOlly are kept on purpose, so a reinstall picks up
; where it left off. Delete that folder by hand to remove them.

[Code]
const
  PerformanceLogUsersSid = 'S-1-5-32-559';
  // Where Setup records a group membership it added, so uninstall removes only that.
  GrantKey = 'Software\SwissOlly';
  GrantValue = 'PerformanceLogUsersGrantedTo';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

// Close SwissOlly if running before install/upgrade. It closes to the notification area
// instead of exiting, so a polite close request would not end it.
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  if CheckForMutexes('{#MyAppName}Mutex') then
  begin
    Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  CloseRunningApp();
  // A pre-rebrand copy holds its database open, which would stop the app's first-launch
  // migration from moving %LocalAppData%\Vaktr. No-op when it is not running.
  Exec('taskkill', '/F /IM Vaktr.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  CloseRunningApp();
end;

// An upgrade keeps "start when I sign in" as it was: the task starts ticked when the current or
// pre-rebrand startup entry exists ([Registry] replaces the old Vaktr value with the new one).
procedure InitializeWizard();
begin
  if RegValueExists(HKCU, RunKey, '{#MyAppName}') or RegValueExists(HKCU, RunKey, 'Vaktr') then
    WizardSelectTasks('launchonstartup');
end;

function AccountName(): String;
begin
  Result := GetEnv('USERDOMAIN') + '\' + GetUserNameString();
end;

// The group's name differs on every localised Windows install, so look it up by its SID.
function PerformanceLogUsersName(): String;
var
  Locator, Service, Groups, Group: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Groups := Service.ExecQuery('SELECT Name FROM Win32_Group WHERE LocalAccount = TRUE AND SID = ''' + PerformanceLogUsersSid + '''');
    if Groups.Count > 0 then
    begin
      Group := Groups.ItemIndex(0);
      Result := Group.Name;
    end;
  except
    Log('Could not look up the Performance Log Users group: ' + GetExceptionMessage());
  end;
end;

function RunNetLocalGroup(const GroupName, Account, Action: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\net.exe'), 'localgroup "' + GroupName + '" "' + Account + '" ' + Action,
      '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

// The in-game overlay reads true per-process frame rate from an ETW session on the
// Microsoft-Windows-DXGI provider. Creating a real-time ETW session requires membership in the
// built-in "Performance Log Users" group (SID S-1-5-32-559) or Administrator, which SwissOlly
// deliberately never runs as. Setup is already elevated, so it grants the group once, only when
// the user ticks the task; SwissOlly itself always runs as a normal user process. The membership
// lands in the next logon token, so it applies after signing out and back in; until then the
// overlay shows "--" for FPS rather than a misleading estimate.
//
// Caveat: this adds the account running Setup. With the usual consent-prompt UAC that is the
// person who will play games. If Setup was elevated with a different admin account, that account
// is added instead and the overlay keeps showing "--".
procedure GrantPerformanceLogUsers();
var
  GroupName, Account: String;
  ResultCode: Integer;
begin
  GroupName := PerformanceLogUsersName();
  if GroupName = '' then
  begin
    Log('Performance Log Users group not found; the overlay will show no FPS.');
    exit;
  end;

  Account := AccountName();
  ResultCode := RunNetLocalGroup(GroupName, Account, '/add');
  if ResultCode = 0 then
  begin
    RegWriteStringValue(HKLM, GrantKey, GrantValue, Account);
    Log('Added ' + Account + ' to ' + GroupName + '.');
  end
  else
    // 2 is what net.exe returns for "already a member" too; nothing to record or undo then.
    Log('net localgroup /add returned ' + IntToStr(ResultCode) + ' (already a member, or not permitted).');
end;

procedure RevokePerformanceLogUsers();
var
  GroupName, Account: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM, GrantKey, GrantValue, Account) then
    exit;

  GroupName := PerformanceLogUsersName();
  if GroupName <> '' then
  begin
    ResultCode := RunNetLocalGroup(GroupName, Account, '/delete');
    Log('net localgroup /delete for ' + Account + ' returned ' + IntToStr(ResultCode) + '.');
  end;

  RegDeleteValue(HKLM, GrantKey, GrantValue);
  RegDeleteKeyIfEmpty(HKLM, GrantKey);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('exactfps') then
    GrantPerformanceLogUsers();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RevokePerformanceLogUsers();
    // The app's own "Start with Windows" setting writes this value too, not only Setup's task.
    RegDeleteValue(HKCU, RunKey, '{#MyAppName}');
    RegDeleteValue(HKCU, StartupApprovedKey, '{#MyAppName}');
  end;
end;
