; SwissOlly Inno Setup Script
; Compiles into a single SwissOllySetup.exe installer per architecture.
;
; Parameters (passed via /D on the command line):
;   MyAppVersion  - Version string (e.g. "1.0.1")
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
  #define MyAppVersion "1.0.1"
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
ArchitecturesAllowed={#MyAppArch}
ArchitecturesInstallIn64BitMode={#MyAppArch}
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchonstartup"; Description: "Launch SwissOlly when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Include everything from the publish folder
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
; Launch on startup (optional, user chooses during install)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: launchonstartup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up app data on uninstall (optional — comment out to preserve user data)
; Type: filesandordirs; Name: "{localappdata}\SwissOlly"

[Code]
// Close SwissOlly if running before install/upgrade
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if CheckForMutexes('{#MyAppName}Mutex') then
  begin
    Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
  // A pre-rebrand copy holds its database open, which would stop the app's first-launch
  // migration from moving %LocalAppData%\Vaktr. No-op when it is not running.
  Exec('taskkill', '/F /IM Vaktr.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// The in-game overlay reads true per-process frame rate from an ETW session on the
// Microsoft-Windows-DXGI provider. Creating a real-time ETW session requires membership in the
// built-in "Performance Log Users" group (SID S-1-5-32-559) — or Administrator, which we
// explicitly do not want SwissOlly to run as.
//
// Setup already runs elevated, so grant it here once at install time. SwissOlly itself then runs as
// a normal user process. Membership only lands in a freshly built logon token, so the user must
// sign out and back in before exact FPS becomes available; until then the overlay falls back to
// approximate compositor-based FPS on its own and says so in the control deck.
procedure GrantPerformanceLogUsers();
var
  ResultCode: Integer;
begin
  // Addressed by SID, not by name: the group is called something different on every
  // localised Windows install. Add-LocalGroupMember ships in-box on 1607+ and our
  // MinVersion is 17763, so it is always present.
  //
  // Caveat: this adds the account running Setup. With normal consent-prompt UAC that is the
  // user who will play games. If Setup was elevated with a *different* admin account, the
  // playing account is not granted and the overlay stays on approximate FPS.
  if not Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -Command "try { Add-LocalGroupMember -SID ''S-1-5-32-559'' -Member (''{0}\{1}'' -f $env:USERDOMAIN,$env:USERNAME) -ErrorAction Stop } catch { exit 2 }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Could not launch group-add; overlay will use approximate FPS.')
  else
    Log('Performance Log Users add exit code ' + IntToStr(ResultCode)
        + ' (2 = already a member or not permitted, which is non-fatal).');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    GrantPerformanceLogUsers();
end;
