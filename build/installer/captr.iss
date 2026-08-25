; Captr installer (SPEC §11). Compiled by build/make-installer.ps1 with Inno Setup —
; deliberately NOT WiX (SPEC §2: WiX v6 requires a maintenance fee for commercial
; use; Inno Setup's licence explicitly permits commercial use with no fee).
;
; Behaviours required by SPEC §11, and where they live in this script:
;   * silent install:            standard Inno switches /SILENT /VERYSILENT
;   * suppress shortcuts:        /MERGETASKS="!shortcuts"
;   * preset working folder:     /WORKINGFOLDER="D:\Recordings"  (see [Code])
;   * per-machine by default:    PrivilegesRequired=admin (per-user via /CURRENTUSER)
;   * command line on PATH:      opt-out checkbox -> user PATH, no duplicate
;                                entries, and removed again on uninstall
;   * NO scheduled task/service/agent installed: nothing here registers one - the
;     application starts on demand (SPEC SPEC 11), and that is a feature, not a gap.
;   * preflight:                 ArchitecturesAllowed + MinVersion below
;   * refuse while recording:    InitializeSetup asks the CLI; /FORCESTOP overrides
;     after finalising the recording first
;   * existing install detected: InitializeSetup says which of reinstall, upgrade,
;     or downgrade is about to happen and asks first. Silent installs get the
;     default answer, so /VERYSILENT still reinstalls and upgrades unattended.
;   * refuse downgrade:          numeric version comparison; /ALLOWDOWNGRADE overrides
;   * default settings on a fresh machine: ssPostInstall runs `captr settings init`
;     AS THE ORIGINAL USER, so a first run has real settings rather than none
;   * uninstall keeps data:      no [UninstallDelete] of user data; after Inno's own
;     confirmation the user is asked with two labelled choices defaulting to KEEP,
;     and deleting is confirmed twice (skipped entirely in silent uninstall = keep)

#include "version.iss"

; The product GUID, defined ONCE. It is written two ways because two consumers need
; two different escapings, and getting that wrong fails silently:
;   * [Setup] AppId needs the leading brace DOUBLED, or Inno tries to expand
;     {7E1C...} as a constant.
;   * the registry path in [Code] needs it SINGLE, because a Pascal string literal is
;     not constant-expanded.
; Using {#SetupSetting("AppId")} for the registry path handed back the DOUBLED form,
; so the uninstall key was never found and an existing installation was invisible:
; no already-installed prompt, no upgrade notice, and no downgrade refusal.
#define AppGuid "7E1C7C2E-5A15-4F60-92D1-0CAB70000001"

[Setup]
AppId={{{#AppGuid}}
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
; The installer's own icon, and the icon Programs and Features (appwiz.cpl) shows
; beside the entry. The uninstall icon points at the installed executable, whose
; embedded icon is the same file - so Explorer, the Start menu, Alt-Tab, and
; Programs and Features all show one consistent Captr icon.
SetupIconFile=..\..\src\Captr.App\Assets\captr.ico
UninstallDisplayIcon={app}\Captr.App.exe
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Tasks]
Name: "shortcuts"; Description: "Create a Start Menu shortcut"
; Ticked by default (Inno ticks a task unless it is marked unchecked): the CLI is half
; the product -- SPEC 10 requires full command-line parity so Task Scheduler can drive
; it -- and it is useless if the shell cannot find it.
Name: "addtopath"; Description: "Add Captr to my PATH so I can run 'captr' from a terminal"

[Files]
Source: "..\..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Captr"; Filename: "{app}\Captr.App.exe"; Tasks: shortcuts

[Registry]
; The USER PATH, whether or not this is a per-machine install: the person running the
; installer is the one who wants 'captr' in their terminal, and a user entry needs no
; elevation to add now or to remove on uninstall. The duplicate check is what SPEC SPEC 11
; demands so that upgrades never stack entries.
Root: HKCU; Subkey: "Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
var
  RemoveDataOnUninstall: Boolean;

// ---- PATH helper: true only when the app dir is not already on PATH ----
function NeedsAddPath(Param: string): Boolean;
var
  Existing: string;
begin
  // Only the user PATH is ever written, so only the user PATH is checked. Comparing
  // with a separator on both sides prevents a partial match - "C:\Captr2" must not
  // look like "C:\Captr" is already present.
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Existing) then begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Lowercase(Param) + ';', ';' + Lowercase(Existing) + ';') = 0;
end;

// ---- Take the app directory back out of the user PATH on uninstall ----
// Without this, uninstalling leaves an entry pointing at a folder that no longer
// exists, and each later install appends another one.
procedure RemoveFromUserPath(Dir: string);
var
  Existing: string;
  Padded: string;
  Position: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Existing) then
    exit;

  // Pad with separators so the first and last entries match the same way as any
  // other, then remove the entry together with its leading ';'.
  Padded := ';' + Existing + ';';
  Position := Pos(';' + Lowercase(Dir) + ';', Lowercase(Padded));
  if Position = 0 then
    exit;

  Delete(Padded, Position, Length(Dir) + 1);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path',
    Copy(Padded, 2, Length(Padded) - 2));
end;

// ---- Finding an existing installation ----------------------------------------
// Both registry roots are searched, deliberately, rather than HKA.
//
// HKA resolves to HKLM for a per-machine install and HKCU for a per-user one - but
// InitializeSetup runs BEFORE the install mode has been decided, so HKA there is a
// coin toss. Using it meant a per-user installation was invisible to a setup that
// happened to resolve HKA to HKLM: no "already installed" prompt, no upgrade notice,
// and no downgrade refusal. Asking both roots is correct in every combination,
// including "installed per-user, now installing per-machine".
function ReadInstalledValue(ValueName: string; var Value: string): Boolean;
var
  Key: string;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  Result := RegQueryStringValue(HKCU, Key, ValueName, Value)
         or RegQueryStringValue(HKLM, Key, ValueName, Value)
         or RegQueryStringValue(HKLM32, Key, ValueName, Value);
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
  if ReadInstalledValue('InstallLocation', Location) then begin
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
  if not ReadInstalledValue('DisplayVersion', Result) then
    Result := '';
end;

// ---- Comparing versions NUMERICALLY -------------------------------------------
// The previous check used CompareStr, which compares text: by that measure "0.10.0"
// is LOWER than "0.9.0", so the first double-digit minor release would have been
// refused as a downgrade. StrToVersion/ComparePackedVersion do it properly.
// A pre-release suffix ("0.2.0-beta.1") is not part of the numeric version, so it is
// trimmed before comparing.
function ComparableVersion(Text: string): Int64;
var
  Dash: Integer;
  Packed: Int64;
begin
  Dash := Pos('-', Text);
  if Dash > 0 then
    Text := Copy(Text, 1, Dash - 1);

  if StrToVersion(Text, Packed) then
    Result := Packed
  else
    Result := 0;
end;

// -1 when A is older than B, 0 when equal, 1 when A is newer.
function CompareVersions(A, B: string): Integer;
begin
  Result := ComparePackedVersion(ComparableVersion(A), ComparableVersion(B));
end;

// ---- What to do about an existing installation ---------------------------------
// SPEC 11 requires a downgrade to be refused rather than silently applied. Beyond
// that, a person running an installer over an existing install deserves to be told
// what is about to happen: reinstalling the same version, moving up, or moving down
// are three quite different things and used to be indistinguishable.
//
// Silent installs keep working: SuppressibleMsgBox returns the default answer given
// here, so /VERYSILENT reinstalls and upgrades proceed and a downgrade still needs
// /ALLOWDOWNGRADE=yes.
function ConfirmAgainstInstalledVersion(): Boolean;
var
  Existing: string;
  Order: Integer;
begin
  Existing := InstalledVersion();
  if Existing = '' then begin
    Result := True;   // Nothing installed: an ordinary first install.
    exit;
  end;

  Order := CompareVersions('{#CaptrVersion}', Existing);

  if Order = 0 then begin
    Result := SuppressibleMsgBox(
      'Captr ' + Existing + ' is already installed.' + #13#10#13#10 +
      'Continuing will reinstall it over the top, which repairs a damaged installation.' + #13#10 +
      'Your recordings, settings, and pending transfers are kept either way.' + #13#10#13#10 +
      'Reinstall Captr ' + Existing + '?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
    exit;
  end;

  if Order > 0 then begin
    Result := SuppressibleMsgBox(
      'Captr ' + Existing + ' is installed. This will upgrade it to ' + '{#CaptrVersion}' + '.' + #13#10#13#10 +
      'Your recordings, settings, stored credentials, and pending transfers are all kept, ' +
      'and settings are migrated forward automatically.' + #13#10#13#10 +
      'Upgrade now?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
    exit;
  end;

  // Order < 0: the installed version is NEWER than this one.
  if ExpandConstant('{param:ALLOWDOWNGRADE|no}') = 'yes' then begin
    Result := True;
    exit;
  end;

  Result := SuppressibleMsgBox(
    'A NEWER version of Captr is already installed.' + #13#10#13#10 +
    '    Installed:  ' + Existing + #13#10 +
    '    This setup: ' + '{#CaptrVersion}' + #13#10#13#10 +
    'Downgrading is not recommended. Settings are migrated FORWARD only, so a settings ' +
    'file written by ' + Existing + ' may not be readable by ' + '{#CaptrVersion}' + ' - ' +
    'Captr would then start from defaults.' + #13#10#13#10 +
    'Downgrade to ' + '{#CaptrVersion}' + ' anyway?',
    mbError, MB_YESNO, IDNO) = IDYES;
end;

function InitializeSetup(): Boolean;
var
  ExitCode: Integer;
begin
  // SPEC 11: refuse while recording unless forced; forced = finalise FIRST.
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

  Result := ConfirmAgainstInstalledVersion();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  WorkingFolder: string;
  Params: string;
begin
  if CurStep = ssInstall then begin
    // End the idle host so files are replaceable (recorder is idle here).
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Captr.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  end;

  if CurStep = ssPostInstall then begin
    // Give a fresh machine a real settings.json instead of nothing until the user
    // first opens Settings and saves something.
    //
    // Done by asking the CLI rather than by writing JSON here: the defaults, the
    // current schema version, and the validation then all come from the product, and
    // this script cannot drift from them. It never overwrites an existing file, so an
    // upgrade keeps the user's configuration.
    //
    // ExecAsOriginalUser, not Exec: a per-machine install runs elevated, possibly as a
    // DIFFERENT administrator account, and settings belong to the person who ran
    // setup. Running elevated would write them into the admin's profile, where the
    // user would never see them.
    Params := 'settings init';

    // /WORKINGFOLDER=... presets the working folder for the installing user
    // (other users keep defaults; documented in docs/user-guide/installation.md).
    WorkingFolder := ExpandConstant('{param:WORKINGFOLDER|}');
    if WorkingFolder <> '' then
      Params := Params + ' --working-folder "' + WorkingFolder + '"';

    ExecAsOriginalUser(ExpandConstant('{app}\captr.exe'), Params, ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ExitCode);
  end;
end;

// ---- Uninstall: the user chooses, and the default is KEEP (SPEC 11) ------------
// Pascal Script has no array literal, and a line beginning with '[' would be read
// as a section tag, so the button labels are built one at a time.
function KeepOrDeleteLabels(): TArrayOfString;
begin
  SetArrayLength(Result, 2);
  Result[0] := '&Keep my recordings and settings';
  Result[1] := '&Delete everything Captr has stored';
end;

// Asked from CurUninstallStepChanged(usUninstall), NOT from InitializeUninstall.
// InitializeUninstall runs BEFORE Inno's own "are you sure you want to remove Captr?"
// confirmation, so asking there put the question about the user's footage in front of
// the question about whether they even meant to uninstall - and asked it of people
// who then said no.
procedure AskWhetherToKeepData();
var
  Choice: Integer;
begin
  RemoveDataOnUninstall := False;

  // A silent uninstall NEVER removes data. There is no one to ask, and the safe
  // answer to an unasked question about somebody's footage is "keep it".
  if UninstallSilent() then
    exit;

  // A task dialog with two labelled choices rather than a Yes/No box. The old
  // question was "Also delete recordings, settings, and the queue?" with Yes/No,
  // which is easy to answer wrongly in a hurry - and answering it wrongly destroys
  // recordings. Here each choice says what it does, and keeping is the default.
  Choice := TaskDialogMsgBox(
    'Keep your recordings and settings?',
    'Captr stores your recordings, settings, and pending transfers in' + #13#10 +
    ExpandConstant('{localappdata}\Captr') + #13#10#13#10 +
    'Keeping them means a later reinstall picks up exactly where you left off - ' +
    'same working folder, same destinations, same hotkeys.' + #13#10#13#10 +
    'Stored credentials are not removed either way. Remove one with:' + #13#10 +
    '    captr auth delete "<name>"',
    mbConfirmation, MB_YESNO, KeepOrDeleteLabels(), 0);

  RemoveDataOnUninstall := (Choice = IDNO);

  if RemoveDataOnUninstall then
    // Deleting footage is the one irreversible thing an uninstaller can do, so it is
    // confirmed twice and still defaults to keeping.
    RemoveDataOnUninstall := SuppressibleMsgBox(
      'This permanently deletes every recording in' + #13#10 +
      ExpandConstant('{localappdata}\Captr') + #13#10#13#10 +
      'This cannot be undone. Are you sure?',
      mbCriticalError, MB_YESNO, IDNO) = IDYES;
end;

// ---- Notification-area leftovers ---------------------------------------------
//
// Windows remembers every program that has ever shown a tray icon under
//   HKCU\Control Panel\NotifyIconSettings
// so it can offer it in Settings > Personalisation > Taskbar > "Other system tray
// icons". Nothing ever removes those entries, so an uninstalled Captr goes on being
// listed there for ever - and a machine that once had a per-user install and now has
// a per-machine one lists Captr TWICE, which is exactly what this fixes.
//
// The subkey names are hashes, so the entries have to be found by their
// ExecutablePath value. Windows stores that path with the containing known folder
// folded into its GUID, so the GUID has to be expanded before comparing.

function UnfoldKnownFolder(Path: string): string;
begin
  Result := Path;
  // ProgramFiles (x64), ProgramFiles (x86), LocalAppData - the three places Captr
  // has ever been installed to.
  StringChangeEx(Result, '{6D809377-6AF0-444B-8957-A3773F02200E}', ExpandConstant('{commonpf64}'), True);
  StringChangeEx(Result, '{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}', ExpandConstant('{commonpf32}'), True);
  StringChangeEx(Result, '{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}', ExpandConstant('{localappdata}'), True);
end;

procedure RemoveTrayIconRegistration(ExePath: string);
var
  Names: TArrayOfString;
  I: Integer;
  Recorded: string;
begin
  if not RegGetSubkeyNames(HKEY_CURRENT_USER, 'Control Panel\NotifyIconSettings', Names) then
    exit;

  for I := 0 to GetArrayLength(Names) - 1 do begin
    if RegQueryStringValue(HKEY_CURRENT_USER,
         'Control Panel\NotifyIconSettings\' + Names[I], 'ExecutablePath', Recorded) then begin
      // Exact path match only: a machine may legitimately have another Captr
      // installed somewhere else, and removing ITS entry would be a bug.
      if CompareText(UnfoldKnownFolder(Recorded), ExePath) = 0 then
        RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER,
          'Control Panel\NotifyIconSettings\' + Names[I]);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall runs after Inno has confirmed the uninstall and before anything is
  // removed, which is the only point at which asking about the user's data makes
  // sense.
  if CurUninstallStep = usUninstall then
    AskWhetherToKeepData();

  if CurUninstallStep = usPostUninstall then begin
    RemoveFromUserPath(ExpandConstant('{app}'));
    RemoveTrayIconRegistration(ExpandConstant('{app}\Captr.App.exe'));
  end;

  if (CurUninstallStep = usPostUninstall) and RemoveDataOnUninstall then begin
    DelTree(ExpandConstant('{userappdata}\Captr'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\Captr'), True, True, True);
  end;
end;
