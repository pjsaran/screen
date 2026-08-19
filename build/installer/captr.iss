; Captr installer (SPEC §11). Compiled by build/make-installer.ps1 with Inno Setup —
; deliberately NOT WiX (SPEC §2: WiX v6 requires a maintenance fee for commercial
; use; Inno Setup's licence explicitly permits commercial use with no fee).
;
; Behaviours required by SPEC §11, and where they live in this script:
;   * silent install:            standard Inno switches /SILENT /VERYSILENT
;   * suppress shortcuts:        /MERGETASKS="!shortcuts"
;   * preset working folder:     /WORKINGFOLDER="D:\Recordings"  (see [Code])
;   * per-machine by default:    PrivilegesRequired=admin (per-user via /CURRENTUSER)
;   * command line on PATH:      [Registry] + NeedsAddPath (no duplicate entries)
;   * NO scheduled task/service/agent installed: nothing here registers one — the
;     application starts on demand (SPEC §11), and that is a feature, not a gap.
;   * preflight:                 ArchitecturesAllowed + MinVersion below
;   * refuse while recording:    InitializeSetup asks the CLI; /FORCESTOP overrides
;     after finalising the recording first
;   * refuse downgrade:          InitializeSetup version check; /ALLOWDOWNGRADE overrides
;   * uninstall keeps data:      no [UninstallDelete] of user data; removal is an
;     unticked-by-default question (skipped entirely in silent uninstall = keep)

#include "version.iss"

[Setup]
AppId={{7E1C7C2E-5A15-4F60-92D1-0CAB70000001}
AppName=Captr
AppVersion={#CaptrVersion}
AppVerName=Captr {#CaptrVersion}
AppPublisher=Captr
VersionInfoVersion={#CaptrVersion}
DefaultDirName={autopf}\Captr
OutputBaseFilename=captr-setup-{#CaptrVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Captr.App.exe
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Tasks]
Name: "shortcuts"; Description: "Create a Start Menu shortcut"

[Files]
Source: "..\..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Captr"; Filename: "{app}\Captr.App.exe"; Tasks: shortcuts

[Registry]
; Machine PATH for per-machine installs, user PATH for per-user — with the
; duplicate check SPEC §11 demands (upgrades must not stack entries).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Check: IsAdminInstallMode and NeedsAddPath(ExpandConstant('{app}'))
Root: HKCU; Subkey: "Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Check: (not IsAdminInstallMode) and NeedsAddPath(ExpandConstant('{app}'))

[Code]
var
  RemoveDataOnUninstall: Boolean;

// ---- PATH helper: true only when the app dir is not already on PATH ----
function NeedsAddPath(Param: string): Boolean;
var
  Existing: string;
  RootKey: Integer;
  SubKey: string;
begin
  if IsAdminInstallMode then begin
    RootKey := HKLM;
    SubKey := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  end else begin
    RootKey := HKCU;
    SubKey := 'Environment';
  end;
  if not RegQueryStringValue(RootKey, SubKey, 'Path', Existing) then begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Lowercase(Param) + ';', ';' + Lowercase(Existing) + ';') = 0;
end;

// ---- Ask the installed CLI what the recorder is doing ----
// Exit codes are the documented CLI contract: 0 recording, 10 idle, 11 paused.
// NOTE: '{app}' is not available during InitializeSetup, so the EXISTING
// installation is located via its uninstall registry key. A fresh machine has
// neither, and the check is correctly skipped.
function InstalledCliPath(): string;
var
  Location: string;
begin
  Result := '';
  if RegQueryStringValue(HKA,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'InstallLocation', Location) then begin
    Location := RemoveBackslash(Location);
    if FileExists(Location + '\captr.exe') then
      Result := Location + '\captr.exe';
  end;
end;

function RecorderState(var ExitCode: Integer): Boolean;
var
  Cli: string;
begin
  Cli := InstalledCliPath();
  Result := (Cli <> '') and
            Exec(Cli, 'status', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function TryStopRecording(): Boolean;
var
  Cli: string;
  ExitCode: Integer;
  Attempt: Integer;
begin
  Cli := InstalledCliPath();
  Exec(Cli, 'stop', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  // Finalisation runs in the background; poll status until idle (max ~2 min).
  for Attempt := 1 to 60 do begin
    Sleep(2000);
    if RecorderState(ExitCode) and (ExitCode = 10) then begin
      Result := True;
      exit;
    end;
  end;
  Result := False;
end;

function InstalledVersion(): string;
begin
  // Inno writes DisplayVersion under our AppId's uninstall key.
  if not RegQueryStringValue(HKA,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
      'DisplayVersion', Result) then
    Result := '';
end;

function VersionIsNewerThanInstalled(): Boolean;
var
  Old: string;
begin
  Old := InstalledVersion();
  Result := (Old = '') or
            (CompareStr('{#CaptrVersion}', Old) >= 0); // same or newer
end;

function InitializeSetup(): Boolean;
var
  ExitCode: Integer;
begin
  // SPEC §11: refuse while recording unless forced; forced = finalise FIRST.
  if RecorderState(ExitCode) and ((ExitCode = 0) or (ExitCode = 11)) then begin
    if ExpandConstant('{param:FORCESTOP|no}') = 'yes' then begin
      if not TryStopRecording() then begin
        SuppressibleMsgBox('A recording was in progress and could not be stopped cleanly. Setup cannot continue.',
          mbCriticalError, MB_OK, IDOK);
        Result := False;
        exit;
      end;
    end else begin
      SuppressibleMsgBox('A recording is in progress. Finish it first, or re-run setup with /FORCESTOP=yes to stop and finalise it automatically.',
        mbError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  end;

  // SPEC §11: never silently downgrade.
  if (not VersionIsNewerThanInstalled()) and
     (ExpandConstant('{param:ALLOWDOWNGRADE|no}') <> 'yes') then begin
    SuppressibleMsgBox('A newer version of Captr (' + InstalledVersion() + ') is already installed. ' +
      'Re-run with /ALLOWDOWNGRADE=yes if you really intend to downgrade.',
      mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  SettingsDir: string;
  WorkingFolder: string;
begin
  if CurStep = ssInstall then begin
    // End the idle host so files are replaceable (recorder is idle here).
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Captr.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  end;

  if CurStep = ssPostInstall then begin
    // /WORKINGFOLDER=... presets the working folder for the installing user
    // (other users keep defaults; documented in docs/install.md).
    WorkingFolder := ExpandConstant('{param:WORKINGFOLDER|}');
    if WorkingFolder <> '' then begin
      SettingsDir := ExpandConstant('{userappdata}\Captr');
      ForceDirectories(SettingsDir);
      if not FileExists(SettingsDir + '\settings.json') then
        SaveStringToFile(SettingsDir + '\settings.json',
          '{ "schemaVersion": 1, "workingFolder": "' + WorkingFolder + '" }', False);
    end;
  end;
end;

// ---- Uninstall: keep data by default (SPEC §11) ----
function InitializeUninstall(): Boolean;
begin
  RemoveDataOnUninstall := False;
  if not UninstallSilent() then
    // Default answer NO; silent uninstall never removes data.
    RemoveDataOnUninstall :=
      SuppressibleMsgBox('Also delete recordings, settings, and the delivery queue?' + #13#10 +
        'Stored credentials are NOT removed (remove them with: captr auth delete <name>).' + #13#10#13#10 +
        'Choose No to keep everything (recommended).',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveDataOnUninstall then begin
    DelTree(ExpandConstant('{userappdata}\Captr'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\Captr'), True, True, True);
  end;
end;
