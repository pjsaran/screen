# Release review

A review of the whole of Captr ahead of a signed production release: the code,
its security, the build and release tooling, the installer, and the user
experience. Every finding was checked against the code before it was accepted;
anything that could not be pointed to or reproduced was dropped. Each fix is its
own commit with a message that says why. `TASKS.md` is the working checklist;
this file is the report.

"Tested" below means a test was run and passed on this machine. Anything that
needs a real certificate, a real SharePoint tenant, real multi-monitor hardware,
or session 0 on a real machine is listed under **Manual verification needed**,
with the exact steps in `docs/developer-guide/release-verification.md`.

## Test results

### Baseline (fresh clone of commit 2986218, before any change)

| Run | Result |
|---|---|
| `build.ps1 -Installer`: format gate, licence gate, warnings-as-errors build | pass |
| Unit tests (Captr.Core.Tests) | 329 / 329 |
| Integration, default build (Ffmpeg, Chaos) | 13 / 13 |
| Integration, Published | 9 / 9 |
| Installer compiled | yes |
| Categories the default build skips (Display, Gpu, Soak) + 14 uncategorised tests the build never ran (TST-1) | 31 / 31 |
| `dotnet list package --vulnerable --include-transitive` | no vulnerable packages |

Later runs of the untouched baseline on the same machine failed
`HostCrashReadoptionTests` intermittently. The causes were real defects (REC-20:
the orphaned encoder was hard-killed, truncating its last segment; and REC-21, a
regression introduced and fixed during this work). It passes consistently now.

### Final

See **Final run** at the end of this file.

## Threat model (summary)

**Assets.** Screen recordings (segments, outputs, journals); the SharePoint client
secret (Credential Manager, DPAPI) and MSAL token cache; `settings.json`, which
decides where footage goes; `transfers.db`, which holds pre-authenticated upload
URLs; and the ability to start, stop or redirect a recording.

**Who can reach them, and what now stands in the way.**

| Attacker | Entry points | Mitigations now in place |
|---|---|---|
| (a) The local user | CLI, window, settings, scheduled tasks | Trusted. Foot-guns closed: session-0 tasks refused (SEC-7), naming and folder rules checked while typing (CLI-14/18/19), plain errors with next steps. |
| (b) Another standard user on the PC | The named pipe; folders under a drive root (inherit "Authenticated Users: Modify"); `C:\` (anyone can create `C:\tools`); installer `taskkill` | Pipe created first-instance with an explicit owner; the client refuses a pipe it does not own (SEC-1). Session folders get a protected ACL (SEC-2). FFmpeg is run only from where Captr installed it, digest-verified before every use (SEC-3). Setup closes only the installing user's Captr unless `/FORCESTOP=yes` (SEC-8). |
| (c) Low-integrity process of the same user | Squatting the pipe or mutex name | First-instance pipe creation; the host refuses to serve under a name someone else created (SEC-1). |
| (d) Hostile settings or planted session files | `settings import`; `integrity.json`, journals and segments in the working folder | Import validates and states what it changes (CLI-9/SEC-11). Re-send and verify accept only bare names inside the recording's folder (SEC-5). Orphan adoption requires the bundled FFmpeg image (SEC-4). Retention matches folders exactly (SEC-9). |
| (e) Network attacker | Graph traffic; SMB/UNC destinations | Graph upload URLs must be HTTPS (SEC-10, part). UNC shares work but remain subject to the network they are on; recommend signed SMB for shares (see Decisions). No update channel exists. |
| Supply chain | FFmpeg, signtool, Artifact Signing client, Inno Setup, NuGet | FFmpeg pinned by SHA-256 and by per-binary image digest; signtool and Artifact Signing pinned by NuGet SHA-512; Inno installer and compiler pinned by SHA-256 (BLD-20); CI actions pinned to commit SHAs with a read-only token (BLD-17); no vulnerable packages. |

Secrets: never on a command line (PFX loaded in-process, BLD-6), never in logs
(structured redaction), never in support bundles or crash reports (line scrubbing,
JWT masking, identity pseudonymisation, XFR-7), and never in the repository.

## Findings

Severity is the one assigned after checking the evidence (reviewers sometimes
rated higher or lower). `=` marks the same defect reported more than once. Commit
hashes are on the release branch.

### Critical and High — all fixed, each with a regression test

| ID | Finding | Location | Status |
|---|---|---|---|
| TST-1 | 14 integration tests had no category and never ran in `build.ps1` or CI | `build/build.ps1`, tests | Fixed 13cf4da; `TestCategoryTests` fails any uncategorised test |
| TST-2 | Hardware tests ran against a stale `publish/` and tested old code | `build/build.ps1`, `PublishedPayload.cs` | Fixed 6f6a1b1 |
| SEC-1 (=CLI-5, CLI-6, SEC-6) | Another user could create the command pipe first and control the host; the client never checked the server's owner | `Ipc/IpcServer.cs`, `IpcClient.cs`, `HostRuntime.cs` | Fixed 5fc6541; `PipeOwnershipTests` |
| SEC-3 (=BLD-4, REC-8, REC-15, SEC-4, BLD-19) | FFmpeg search climbed to `C:\tools`; no runtime hash check; orphan adoption trusted the journal's image path | `Supervision/FfmpegLocator.cs`, `ProcessAdoption.cs` | Fixed e45942b; `FfmpegIntegrityTests`, `PinnedFfmpegDigestTests`, E2E tampered-FFmpeg test |
| SEC-7 (=CLI-3) | A session-0 scheduled task recorded black frames and reported success | `Sessions/SessionPlanner.cs` | Fixed e10295b; `SessionZeroTests`, E2E |
| BLD-1 | Elevated setup ran a `captr.exe` path read from HKCU (user-writable) | `installer/captr.iss` | Fixed 2589b04; `InstallerScriptTests` |
| BLD-2 | A `/VERYSILENT` upgrade waited for ever on an invisible prompt | `installer/captr.iss` | Fixed 2589b04; installer E2E silent upgrade and downgrade tests |
| CLI-1 (=UI-6) | Two starts at once made two sessions; the first became unstoppable | `Hosting/HostService.cs` | Fixed 5fc6541; `HostServiceStartTests` |
| CLI-2 | `start` while the previous recording finalised reported success and recorded nothing | `Hosting/HostService.cs` | Fixed 5fc6541; `HostServiceStartTests` |
| CLI-4 (=UI-4, XFR-11 part) | A wrong-typed value or a newer schema in `settings.json` killed the host and the window | `Settings/SettingsStore.cs` | Fixed 0b518fe; settings store tests |
| REC-1 | An exception in the session loop skipped finalisation silently and could leave FFmpeg unsupervised | `Sessions/RecordingSession.cs` | Fixed b97037c; `RecordingSessionBehaviourTests` |
| REC-2 (=REC-10, CLI-24) | The message-only window never received sleep, display or log-off broadcasts | `WindowsEvents/MessageOnlyWindow.cs` → `SystemEventWindow.cs` | Fixed b97037c; `SystemEventWindowTests` |
| REC-3 | Settings changed while paused were dropped: an excluded display was recorded | `Sessions/RecordingSession.cs` | Fixed b97037c; behaviour tests |
| REC-4 | A topology rebuild re-included a display excluded mid-session | `Sessions/RecordingSession.cs` | Fixed b97037c; behaviour tests |
| REC-5 | Software-fallback arguments were frozen at start (wrong displays, fps, segment group) | `Sessions/RecordingSession.cs`, `SessionPlanner.cs` | Fixed b97037c |
| REC-6 | `captr recover` during a recording adopted and killed the live encoder | `Sessions/RecoveryScanner.cs` | Fixed b97037c, 6637af0; `RecoveryOfLiveSessionsTests` |
| REC-7 | Finalisation needed a second full copy of the footage, and failed exactly when the disk was low | `Sessions/FinalizationPipeline.cs` | Fixed b97037c; `FinalizationPipelineTests` (hard link, space preflight) |
| REC-9 | The display-required request was per-thread and could vanish mid-recording | `WindowsEvents/ExecutionStateHolder.cs` | Fixed 276939b; `ExecutionStateHolderTests` |
| REC-19 | Recovery reported lost footage as recorded | `Sessions/FinalizationPipeline.cs` | Fixed 6d271db |
| REC-20 | Recovery hard-killed an orphaned encoder, truncating its last segment | `Supervision/FfmpegProcess.cs`, `ConsoleSignal.cs` | Fixed 6d271db; `HostCrashReadoptionTests` |
| REC-21 | Regression from REC-2: every display broadcast restarted the encoder, adding gaps | `Sessions/RecordingSession.cs` | Fixed 368c7f7 |
| XFR-1 (=REC-13) | UNC destinations and working folders always failed free-space checks | `Transfers/FolderDestination.cs`, `Common/FreeSpace.cs` | Fixed 078c2ab; UNC E2E test |
| XFR-2 | Any unexpected exception killed the transfer worker for the life of the host | `Transfers/TransferWorker.cs` | Fixed 078c2ab; worker E2E tests |
| XFR-3 | An expired or completed upload session never recovered; Retry reused the dead URL | `Transfers/GraphUploader.cs`, `TransferQueue.cs` | Fixed 078c2ab; worker E2E tests |
| XFR-6 | The support bundle silently left out the log the running recorder was writing | `Diagnostics/SupportBundle.cs` | Fixed 00172fc; `SupportBundleTests` |
| UI-1 (=XFR-5) | No crash handler anywhere; several UI actions terminated the app | `App.xaml.cs`, host, CLI | Fixed f3ef600; `CrashReporterTests` (the WPF handler itself: manual check) |
| UI-2 | A recording that failed on its own was shown exactly like one somebody stopped | `Services/HostConnection.cs`, Home, tray | Fixed f3ef600; `StatusPresentationTests`, disk-full E2E |
| UI-3 | Delete was offered on the live recording and destroyed its closed segments | `ViewModels/RecordingsViewModel.cs` | Fixed f3ef600; `RecoveryOfLiveSessionsTests` |

### Medium

| ID | Finding | Location | Status |
|---|---|---|---|
| SEC-2 | Session folders under a drive root inherited "Authenticated Users: Modify" | `Sessions/SessionJournal.cs`, `DiskGuard.cs`, `EncoderTrial.cs` | Fixed 9726a6e; `WorkingFolderAclTests` |
| SEC-8 (=BLD-3) | Setup killed every user's Captr and checked recording state as the wrong user | `installer/captr.iss` | Fixed 2589b04 |
| SEC-9 | Retention matched sessions by string prefix and could delete a recording's only copy | `Transfers/RetentionCleaner.cs` | Fixed 5927cb0; `RetentionCleanerTests` |
| BLD-5 | PATH change not broadcast | `installer/captr.iss` | Fixed 2589b04 |
| BLD-6 | PFX password on signtool's command line | `build/sign.ps1` | Fixed 0844b31 (in-process signing) |
| BLD-7 | Signing variable names disagreed across scripts and docs | scripts, docs | Fixed 0844b31, 4bf2f5a, docs |
| BLD-8 | Signing covered four files, no uninstaller, no verification; `signed` meant "a variable was set" | `sign.ps1`, `captr.iss`, `make-installer.ps1` | Fixed 0844b31; `SigningScriptTests` |
| BLD-9 | The tracked `version.iss` was rewritten by every build, which blocked `new-release` | `make-installer.ps1` | Fixed 0eb0da0 |
| BLD-10 | `verify-install.ps1` edited the wrong settings file and never restored it | `build/verify-install.ps1` | Fixed 1a1b9aa; run against a temp install, real settings byte-identical |
| BLD-11 | `/WORKINGFOLDER` with a trailing backslash broke the quoted argument | `installer/captr.iss` | Fixed 2589b04; installer E2E uses one |
| BLD-13 | PDBs and XML docs shipped; builds not reproducible | `build.ps1`, `make-installer.ps1` | Fixed 0844b31 (symbols archived, CI build flag, no wall-clock in shipped JSON) |
| BLD-16 | Third-party notices and the FFmpeg source offer not shipped; no contact for the offer | `build.ps1`, docs | Notices ship (0844b31). **Contact address needs you** |
| CLI-7 | Idle-exit races; a host that lost the mutex race exited at once | `Hosting/HostRuntime.cs` | Fixed 5fc6541 |
| CLI-8 | `settings init` defeated `.bak` recovery | `Cli/CliApplication.cs` | Fixed 9e8a268; CLI E2E |
| CLI-9 (=SEC-11) | `settings import` bypassed the recording lock and said nothing of what changed | `Cli/CliApplication.cs` | Fixed 9e8a268; CLI E2E |
| CLI-10 | Unbounded `retentionDays` stopped the host starting | `Settings/SettingsValidator.cs` | Fixed 9e8a268; validator tests |
| CLI-11 | `--fps` not validated | `Cli/CliApplication.cs` | Fixed 9e8a268; CLI E2E (exit 2) |
| CLI-12 | `start` after a crash timed out while recovery ran | `Hosting/HostRuntime.cs` | Fixed 5fc6541 |
| CLI-13 | Commands leaked stack traces and emitted no JSON on errors | `Cli/CliApplication.cs` | Fixed 9e8a268; CLI E2E |
| CLI-14 | One naming failure silently cancelled every transfer for the recording | `Hosting/HostService.cs` | Fixed dd1dc6a; `OutputHandOffTests` |
| REC-11 | Slow-encoding warning fired once per session | `Supervision/EncoderSupervisor.cs` | Fixed b97037c |
| REC-12 | Kill without waiting for exit; `MainModule` read raced process start | `Supervision/FfmpegProcess.cs` | Fixed 6d271db (and the same race in a test, 193ddb1) |
| XFR-4 | Test connection passed a wrong secret from the MSAL token cache | `Transfers/MsalTokenProvider.cs` | Fixed 078c2ab |
| XFR-7 | Support bundle: user, PC and paths not redacted; user not told what it held; over-redaction | `Diagnostics/SupportBundle.cs` | Fixed 00172fc; `SupportBundleTests` |
| XFR-8 | Send again twice queued duplicate uploads | `Hosting/HostService.cs` | Fixed 6089208; `ResendTests` |
| XFR-9 | Stop could be overwritten by the worker; a timer callback could crash the host | `Transfers/TransferQueue.cs`, `TransferWorker.cs` | Fixed 078c2ab |
| XFR-10 | SharePoint uploads verified by size only | `Transfers/GraphUploader.cs` | **Deferred** — see Decisions |
| XFR-11 | Diagnostics reported invalid settings as OK | `Diagnostics/HealthReport.cs` | Fixed 0b518fe |
| UI-5 | The status poll loop died for good on an unexpected exception | `Services/HostConnection.cs` | Fixed f3ef600 |
| UI-7 | Hotkeys: not re-registered on save, not validated, self-clash misreported, Shift+letter accepted | `Services/HotkeyManager.cs`, `SettingsValidator.cs` | Fixed cd26563; `HotkeyCombinationTests`, validator tests |
| UI-8 | Tab was trapped in the hotkey box and cleared the hotkey | `Views/HotkeyBox.cs` | Fixed cd26563 (manual check) |
| UI-9 | A new destination with a duplicate name overwrote the other's secret | `Views/DestinationEditorWindow.xaml.cs` | Fixed 939933f (manual check) |
| UI-10 | libx264 labelled "Hardware accelerated" | `ViewModels/HomeViewModel.cs` | Fixed 939933f |
| UI-11 | Reset to defaults: no confirmation, bypassed the lock, orphaned secrets | `ViewModels/SettingsViewModel.cs` | Fixed cd26563 (manual check) |
| UI-12 | Transfers action errors never shown; a bad queue crashed window start-up | `ViewModels/TransfersViewModel.cs` | Fixed c1e0a79 (manual check) |

### Low

| ID | Finding | Location | Status |
|---|---|---|---|
| SEC-5 | Re-send and verify trusted `integrity.json` file names | `Hosting/HostService.cs`, `IntegrityRecord.cs` | Fixed 6089208; `ResendTests` |
| SEC-10 | Upload URL not required to be HTTPS; no certificate credential | `Transfers/GraphUploader.cs` | HTTPS fixed 078c2ab; certificate credential **deferred** (with XFR-14) |
| SEC-12 | `.partial` opened with `FileMode.Create` in shared folders | `Transfers/FolderDestination.cs` | Fixed 078c2ab |
| REC-14 | Finalisation picked up leftover `.repaired.mkv` files | `Sessions/FinalizationPipeline.cs` | Fixed b97037c |
| REC-16 | Overlay escaping wrong for `%` and `\`; other dead parameters | `Encoders/DrawTextEscaper.cs` | Escaping fixed 3a70de2, proved on real FFmpeg (`DrawTextOnRealFfmpegTests`). Dead parameters (`adoptedProcess`, `startOffset`, zero `ClockJumped`) **left** — harmless, recommend removal in a clean-up |
| REC-17 | Chaos test assertion always true | `tests/.../ChaosTests.cs` | Fixed e6ef740 |
| REC-18 | Disk guard double-counted the ballast | `Sessions/DiskGuard.cs` | Fixed b97037c |
| XFR-12 | Folder copy not flushed; `.partial` left on cancel | `Transfers/FolderDestination.cs` | Fixed 078c2ab |
| XFR-13 | Renaming a destination deleted its old secret before the save | `Views/DestinationEditorWindow.xaml.cs` | Fixed 939933f |
| XFR-14 | No certificate credential for SharePoint | `Transfers/MsalTokenProvider.cs` | **Deferred** — see Decisions |
| XFR-15 | 408/423 treated as permanent; `Retry-After` dates ignored | `Transfers/GraphUploader.cs` | Fixed 078c2ab |
| XFR-16 | No worker-level transfer tests | tests | Fixed 078c2ab, cc53070 |
| CLI-15 | `resend <relative path>` resolved in the host's directory | `Cli/CliApplication.cs` | Fixed 57d0793; CLI E2E |
| CLI-16 | retry/stop reported success for unknown ids | `Hosting/HostService.cs` | Fixed 5fc6541; CLI E2E (exit 1) |
| CLI-17 | pause/resume reported a state they did not check | `Hosting/HostService.cs` | Fixed 5fc6541 |
| CLI-18 | Reserved names incomplete; not applied to folder segments | `Naming/OutputNamer.cs` | Fixed dd1dc6a; naming tests |
| CLI-19 | Destination folder validation accepted relative and ADS paths | `Naming/OutputNamer.cs` | Fixed dd1dc6a; naming and validator tests |
| CLI-20 | Client leaked a pipe handle on a failed handshake | `Ipc/IpcClient.cs` | Fixed 5fc6541 |
| CLI-21 | No IPC request timeouts; malformed envelope unlogged | `Ipc/IpcClient.cs`, `IpcServer.cs` | Fixed 5fc6541 (server), 57d0793 (client limits) |
| CLI-22 | Ctrl+C exit code 130 undocumented | docs | Documented (user guide) |
| CLI-23 | Masked secret entry: non-BMP characters, unwiped buffers, no console | `Cli/CliApplication.cs` | Fixed 57d0793; CLI E2E for the piped limit |
| UI-13 | Transfers refresh pile-up and focus loss | `ViewModels/TransfersViewModel.cs` | Fixed c1e0a79 |
| UI-14 | Display preview cached a failure; kept full-resolution bitmaps | `Services/DisplayPreviewService.cs` | Fixed a2d865a |
| UI-15 | starting/suspended states fell through to Idle | `MainWindow.xaml.cs`, `TrayPresenter.cs` | Fixed 939933f; `StatusPresentationTests` |
| UI-16 | A second launch could not bring the window to the front | `App.xaml.cs` | Fixed 939933f (manual check) |
| UI-17 | Dead code, an unused package, stale comments; elapsed clock wrapped after 24 h | App | Fixed a2d865a, 939933f |
| BLD-12 | Uninstall had no running-recording check | `installer/captr.iss` | Fixed 2589b04 |
| BLD-14 | Pre-release versions accepted but broke the build | `build/set-version.ps1` | Fixed 1a1b9aa; `VersionScriptTests` |
| BLD-15 | Docs called the GPL FFmpeg build LGPL | docs | Fixed (developer guide) |
| BLD-17 | CI: actions not SHA-pinned, no permissions block, no timeout | `.github/workflows/ci.yml` | Fixed 0844b31 |
| BLD-18 | The line-ending normaliser never matched Windows paths; BOMs committed | `build/tools/normalise-line-endings.ps1` | Fixed 91987e6; `NormaliseLineEndingsTests` |
| BLD-20 | Release script details: local-only tag check, attachments claim, unpinned existing Inno, ignored exit code | `new-release.ps1`, `make-installer.ps1` | Fixed 1a1b9aa, docs |
| DOC-1 | `design-decisions.md` had contradictory tray-icon entries | docs | Clarified (note added; entries kept) |
| DOC-2 | `architecture.md` said settings were in `%APPDATA%` | docs | Fixed |

### Found during this work, beyond the review

- The older integration tests read and wrote the developer's real
  `%LOCALAPPDATA%\Captr\settings.json` and would talk to a running recorder;
  the whole suite now has its own data root (7cfc8ec).
- A start refused after the encoder trial left an empty "recording" folder
  behind (1eccd2c).
- `captr status` said nothing about a recording that had stopped on its own
  (1eccd2c).
- `new-release.ps1` would publish an unsigned release after a warning (975f5dc).
- An FFmpeg adoption unit test failed intermittently on a `MainModule` race
  (193ddb1).

## Code signing

- `build/Sign-Artifacts.ps1` signs every executable and DLL Captr ships that no
  trusted publisher has already signed, plus the installer and (through Inno's
  `SignTool` and `SignedUninstaller`) the uninstaller. SHA-256 digests; RFC 3161
  timestamps with fallback servers; three methods chosen from the environment:
  PFX (from a file or a base64 CI secret, loaded in-process), a certificate in the
  Windows store (tokens, EV), and Azure Artifact Signing (Trusted Signing).
  `-DryRun` needs no certificate. `signtool` and the Artifact Signing client come
  from NuGet, pinned by SHA-512.
- Inner binaries are signed before packaging; the installer after. FFmpeg is signed
  only after its pinned digest is verified; the digest excludes the signature, so
  the runtime check still matches afterwards.
- `build/Verify-Signatures.ps1` fails the build on any shipped file that is
  unsigned, untimestamped, timestamped the legacy way, or signed by anyone other
  than `CAPTR_SIGN_PUBLISHER` (Captr's files) or a listed trusted publisher (the
  rest). `release.json` records `signed: true` only when verification passed.
- Tested: the dry run with no certificate; a real signing run with a throwaway
  self-signed PFX and a real timestamp authority, verified; every verification
  failure mode; a release refusing to start without signing. Not tested: a real
  code-signing certificate, the certificate store method with a hardware token,
  and Artifact Signing (all need credentials — manual checks).
- Signing removes "Unknown publisher" from UAC and file properties. SmartScreen
  reputation is separate and builds over time with downloads; a new OV
  certificate may still show a SmartScreen warning at first.

## UX changes (before → after)

| Area | Before | After |
|---|---|---|
| Recording failure | A recording stopped by repeated faults or a full disk went to "Idle — nothing is recording", exactly like a normal stop | Home, the side panel and the tray show "Recording stopped" with the reason and a one-time balloon, until dismissed; `captr status` names it too |
| Crashes | The window, recorder or CLI vanished with nothing written | A scrubbed crash report in the logs folder (newest 10 kept); the window says so once and keeps running |
| First run | Working folder shown nowhere; first Start silent for several seconds | Home says where recordings go (Open folder) and offers a 10-second test recording that reports duration, encoder and size, or the failure with its reason |
| Naming | Checked only on Save; what a pattern produced was never shown | Live preview under the naming pattern and the destination's file-name and folder boxes, or the problem in the validator's words |
| Transfers outside the Transfers page | A transfer stuck on a credential was invisible | Home shows "2 transfers on their way · 1 needs attention — see Transfers" |
| Transfers page | Retry/Stop errors vanished at once; focus and screen-reader place lost every 3 s; a bad queue stopped the window opening | Errors stay in a banner; rows update in place; a bad queue shows a message |
| Hotkeys | Saved changes did nothing until relaunch; Shift+A accepted; a self-clash blamed "another application"; Tab cleared the box | Applied on Save with any real conflict shown beside the boxes; Ctrl/Alt/Win required unless F1–F24; clashes named; Tab moves on |
| Reset to defaults | Immediate, no confirmation, past the recording lock | Asks, naming the destinations it removes; saves like everything else |
| States | Starting, suspended (PC asleep) and completed showed as Idle; clock wrapped after 24 h | Named in the side panel, tray and Home; elapsed counts total hours |
| Delete | Offered on the recording in progress | Refused with "Stop it first" |
| Errors | "No recording host is running", "Protocol version mismatch… Update the older side", "The host closed the connection mid-request" | "Nothing is recording right now"; "A Captr recorder from a different version is still running… quit from the tray, wait a minute, reopen"; "Captr lost contact with its recorder… try again; recording unaffected" |
| Support bundle | "It contains no video and no secrets." | Says what it holds, that identities are replaced, and to check it before sending; README inside |
| Accessibility | Icon and row buttons read as "button" or the same word on every row; high contrast ignored | Each names its row ("Remove destination Archive"); live regions on changing text; Windows high contrast switches the app's theme |
| Encoder label | libx264 shown as "Hardware accelerated" | "Software" |
| Display previews | A display that failed once stayed blank; ~33 MB kept per 4K display | Retried each refresh; only the thumbnail kept |

## Decisions for you

Each was decided in favour of current behaviour, or the smallest safe change, and
recorded here instead of stopping.

1. **Target platforms.** The brief left this open. Assumed Windows 10 22H2+ and
   Windows 11, x64 only — what the code targets and what was tested. ARM64 is
   neither built nor tested.
2. **Version.** Still 0.1.1. The upgrade test installs this build over the real
   0.1.1 release installer. Recommend releasing as 0.2.0
   (`pwsh build/new-release.ps1 -Bump minor`): behaviour changed in many places.
3. **Settings from a newer version.** A `settings.json` with a newer schema used to
   kill the host; now it is kept beside as `settings.json.from-schema-N` and
   defaults are used. Say if you would rather refuse to start.
4. **New validation rules do not block recording.** Destination folder placement
   (full path, no stream colon, no `..`) and hotkey rules are enforced when
   settings are edited, reported by Diagnostics, and the affected transfer fails
   with the reason — but an existing settings file that breaks them still records.
5. **Session folders are private** to the recording user, SYSTEM and
   Administrators. If other accounts are meant to read the working folder
   directly, this needs an option; transfers to destinations are unaffected.
6. **Support bundles pseudonymise** user, PC, profile path, tenant/client ids and
   SharePoint host by default. Support loses those names unless the person
   provides them.
7. **Releases must be signed.** `new-release.ps1` refuses without signing unless
   `-AllowUnsigned`.
8. **Pre-release versions** (`0.2.0-beta.1`) are refused. They never built; the
   alternative is a numeric assembly/file version derived from the semantic one.
9. **SharePoint verification by size only (XFR-10), deferred.** Graph returns a
   QuickXorHash; implementing it cannot be validated without a tenant, and a wrong
   implementation would park every upload. Meanwhile retention deletes a local
   copy on a size match. Options: implement and validate against a tenant, or keep
   local copies of SharePoint-only recordings out of retention until then.
10. **Certificate credentials for SharePoint (SEC-10/XFR-14), deferred.** Client
    secret only. Recommend a certificate credential before wide rollout.
11. **Overlay (REC-16).** The drawtext overlay path is correct now but switched
    off everywhere. Keep it for a future "timestamp on video" option, or delete it.
12. **Request time limits.** CLI requests to the recorder now give up (exit 3)
    after 2 minutes; start and stop 10 minutes; recover 1 hour. Tell me if a
    scheduled workflow needs longer.
13. **Hotkey "digit" keys.** `Ctrl+Alt+5` now means the 5 key; before, it
    registered the keypad's Clear key.
14. **Per-user PATH.** SPEC asks for PATH; the installer adds the user PATH, not
    the machine PATH, even for per-machine installs (existing design decision). I
    agree with it — it needs no elevation and no machine-wide change — but it
    departs from SPEC.
15. **UNC shares.** Supported for destinations and the working folder. Recommend
    SMB signing on those shares; Captr cannot enforce it.
16. **Test installs.** The installer suite installs per-user into a temp folder
    (a transient HKCU uninstall entry) and refuses to run over a real per-user
    install. It never installs machine-wide.
17. **Your machine's settings.** Before this work, the old `SettingsLockTests`
    wrote the real `%LOCALAPPDATA%\Captr\settings.json`; on this machine its
    working folder points at a deleted temp folder. I did not change it (outside
    the repository). `captr settings set workingFolder <folder>` or deleting the
    file fixes it.
18. **FFmpeg source offer contact.** `docs/ffmpeg-source-offer.md` refers people
    to a contact address that does not exist yet. It needs a real one before
    distribution outside the organisation.

## Manual verification needed

These need things this machine does not have. Exact steps and expected results
are in `docs/developer-guide/release-verification.md`.

- A real code-signing certificate: sign, verify, install, check the publisher
  in UAC and Properties; SmartScreen behaviour on a fresh machine.
- Azure Artifact Signing and certificate-store (hardware token) signing.
- A real SharePoint tenant: Sites.Selected grant, upload, large-file resume, wrong
  secret, missing grant.
- Real multi-monitor hardware: hot-plug and topology change mid-recording,
  mixed DPI.
- Session 0 on a real machine (a scheduled task set to run whether the user is
  logged on or not).
- Per-machine elevated install, upgrade and uninstall with another account
  running Captr, with and without `/FORCESTOP=yes`.
- The window's behaviour: the unexpected-error message, Dismiss on a failed
  recording, "Record a test", hotkey conflicts with another application, Reset
  confirmation, Transfers banner, second-launch focus, Tab through Settings,
  Narrator walkthrough, high-contrast theme.
- Sleep and resume during a recording.
- Review of a real support bundle before sending it.

## Final run
