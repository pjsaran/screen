# Testing

## Running the tests

```powershell
pwsh build/build.ps1                    # unit + every integration category a plain machine can run
pwsh build/build.ps1 -Publish           # ... and the Published tests against publish/
pwsh build/build.ps1 -Full              # ... and Display, Gpu, and Soak (implies a publish)
```

To run one project, or one class, directly:

```powershell
dotnet test tests/Captr.Core.Tests
dotnet test tests/Captr.Integration.Tests --filter "Category=Chaos"

# The test projects are also runnable executables, which is the quickest loop:
./tests/Captr.Core.Tests/bin/Debug/net10.0-windows10.0.19041.0/Captr.Core.Tests.exe
./tests/Captr.Core.Tests/bin/Debug/net10.0-windows10.0.19041.0/Captr.Core.Tests.exe `
    -class Captr.Core.Tests.Transfers.TransferQueueTests
```

## The two projects

**`tests/Captr.Core.Tests`** — unit tests. No FFmpeg, no hardware, no network, no
clock dependence. Every one of these runs anywhere and runs fast; if a test here
needs a real anything, it belongs in the other project.

**`tests/Captr.Integration.Tests`** — real FFmpeg, real named pipes, real files, the
real PowerShell build scripts, and — in the end-to-end suites — the published
`captr.exe` and the real installer. Categorised by trait, because many cannot run
everywhere:

| Trait | Needs | Where it runs |
|---|---|---|
| `Os` | Windows itself — named pipes, ACLs, power requests, window messages, Credential Manager, PowerShell — but no FFmpeg, display, or GPU | `build.ps1` step 5 and CI |
| `Ffmpeg` | The bundled FFmpeg, driven with synthetic `lavfi` inputs | `build.ps1` step 5 and CI |
| `Chaos` | The same, plus randomised kills and deliberate corruption | `build.ps1` step 5 and CI |
| `Published` | `publish/captr.exe`, so it runs only AFTER a publish — see the note below | after publishing, locally and in CI |
| `Display` | A real interactive desktop (`ddagrab` capture) | `-Full` only |
| `Gpu` | A real hardware encoder (and, for several, the published payload) | `-Full` only |
| `Soak` | Both, plus patience. `CAPTR_SOAK_MINUTES` sets the length (default 6) | `-Full` only |
| `Installer` | The built installer; installs and uninstalls Captr | only on purpose — see [the installer suite](#the-installer-suite) |

The build selects by **excluding** what a step cannot run, so a test with no
category would still run — and `TestCategoryTests` fails the build if any class
declares no category, or one the build does not know. An include-list once silently
skipped fourteen uncategorised tests, including one this page cited as coverage.

A category is a promise about what a test **needs**, and needs are not only
hardware. `Published` exists because the CLI contract tests drive the published
`captr.exe`, which no amount of `dotnet build` produces. They were once `Ffmpeg` —
right that they need no GPU, wrong that they could run before packaging — and so
they passed only on machines carrying a stale `publish/` from an earlier run, while
failing on every clean clone. If a test needs an artefact from a later build stage,
give it its own trait and run it at that stage. Every test that uses `publish/` goes
through `PublishedPayload`, which fails, naming the fix, when any source file is
newer than the payload.

## Environment variables the tests use

| Variable | Effect |
|---|---|
| `CAPTR_DATA_ROOT` | Moves everything Captr keeps — settings, logs, transfer queue, encoder and display caches, token cache, default working folder — into that folder, and gives the recorder's pipe, its single-instance lock, and the window's single-instance names a suffix, so that copy of Captr has a recorder of its own. **The whole integration suite runs under its own**: `SuiteDataRoot` (a module initializer) sets it to a temp folder before any test runs, and every process a test starts inherits it, so no test ever reads or writes the developer's real `%LOCALAPPDATA%\Captr` or talks to the developer's running recorder. Tests that need a root of their own set one per process. |
| `CAPTR_SIMULATE_SESSION0` | `1` makes Captr behave as if it were in session 0, so the end-to-end suite can prove the refusal. It can only ever cause a refusal. |
| `CAPTR_SIMULATE_FREE_BYTES_FILE` | Names a file holding a byte count that replaces the real free space on every check, read each time, so a test can "fill the disk" mid-recording without administrator rights. |
| `CAPTR_PREVIOUS_INSTALLER` | The previous release's `captr-setup-*.exe`, for the upgrade test. Without it that one test is skipped. |
| `CAPTR_ACCEPT_GOLDEN` | `1` regenerates the golden FFmpeg argument files instead of comparing against them. Review the diff before committing. |
| `CAPTR_SOAK_MINUTES` | Length of the soak test (default 6). |

None of these is set in a real installation.

## The installer suite

`EndToEnd/InstallerEndToEndTests` (category `Installer`) runs the real setup
program silently, exactly as a scripted rollout would — but **per-user**
(`/CURRENTUSER`), into a temp folder, with no shortcut and no PATH entry, and under
its own `CAPTR_DATA_ROOT`. Nothing is installed machine-wide. It refuses to run if
Captr is already installed for your account, because it installs and uninstalls.

```powershell
pwsh build/build.ps1 -Installer                                   # build the installer from this source
dotnet test tests/Captr.Integration.Tests -c Release --no-build --filter "Category=Installer"

# Also the upgrade-from-previous-release test:
$env:CAPTR_PREVIOUS_INSTALLER = 'C:\Downloads\captr-setup-0.1.0.exe'
dotnet test tests/Captr.Integration.Tests -c Release --no-build --filter "Category=Installer"
```

It needs a desktop (it records), and it fails if the installer in `artifacts/` is
older than `publish/`. The previous release predates `CAPTR_DATA_ROOT`, so the
upgrade test saves your real `settings.json` and `settings.json.bak` first and puts
them back byte for byte.

## Writing a test here

- **Test names read as sentences** stating the behaviour:
  `A_transfer_that_runs_out_of_attempts_stops_retrying_and_asks_for_a_person`. If a
  name needs a comment to explain what it means, rename it.
- **Every integration test opens with a comment** describing the scenario in plain
  English, because reproducing one by hand is otherwise an archaeology exercise.
- **Every integration test class declares one category** from the table above.
- **Never touch the developer's own Captr.** The suite's `CAPTR_DATA_ROOT` covers
  this for anything that goes through `CaptrPaths`; a test that needs the real
  default must restore it.
- **Never use `Progress<T>` to collect progress in a test.** It posts to the thread
  pool, so reports arrive after the operation has finished, out of order, and race
  on the collection. Use a synchronous `IProgress<T>` — which is also what
  production does.
- **Assert on behaviour, not on wording**, except where the wording *is* the
  behaviour (an error message a user is meant to act on).

## Release brief coverage

The requirements the release had to prove end to end, and the tests that prove
them. The `EndToEnd` suites drive the real published `captr.exe`, the real recorder,
real capture, and (for the installer) the real setup program, each under a data root
of its own.

| Requirement | Covered by | Trait |
|---|---|---|
| Install → first run → record → transfer → uninstall | `EndToEnd/InstallerEndToEndTests.Install_first_run_record_transfer_and_uninstall` (version stamped on every binary and in Apps & features, no PDBs installed, `/WORKINGFOLDER` with a trailing backslash applied, the window opens, a recording verifies and reaches a folder destination, uninstall removes the program and keeps the recording, the transferred copy, and the settings) | Installer |
| Silent upgrade over a running recording | `InstallerEndToEndTests.A_silent_upgrade_over_a_running_recording_refuses_quickly_or_stops_it_cleanly_when_told` (refuses at once and the recording carries on; with `/FORCESTOP=yes` it is stopped, finalised, and verifies) | Installer |
| Downgrade refused unless allowed | `InstallerEndToEndTests.A_silent_downgrade_is_refused_unless_explicitly_allowed` | Installer |
| Upgrade from the previous release | `InstallerEndToEndTests.Upgrading_from_the_previous_release_replaces_it_in_place` (one Apps & features entry, records, uninstalls both versions' files; needs `CAPTR_PREVIOUS_INSTALLER`) | Installer |
| Installer script rules (runs the CLI only as the original user, never ends other accounts' recorders without `/FORCESTOP`, never waits in a silent install, announces PATH changes, uninstall refuses under a recording) | `Build/InstallerScriptTests` | Os |
| Every CLI command and every documented exit code (`0`, `1`, `2`, `3`, `10`) | `EndToEnd/CliEndToEndTests`: `Help_lists_every_command_and_exits_0`, `Bad_arguments_exit_2_with_the_problem_on_stderr`, `Status_with_nothing_recording_exits_10_and_reports_idle_as_json`, `Stop_pause_and_resume_with_nothing_recording_succeed`, `Recordings_list_answers_as_json_without_starting_anything`, `Verifying_or_resending_a_folder_that_is_not_a_recording_exits_1`, `Resend_takes_a_relative_path_relative_to_where_it_is_typed`, `A_piped_secret_longer_than_any_secret_is_refused_and_nothing_is_stored`, `Recover_with_nothing_to_recover_exits_0`, `Transfers_list_answers_and_retry_or_stop_of_an_unknown_id_exits_1`, `Settings_set_validates_and_get_shows_what_was_saved`, `Settings_init_creates_once_and_never_overwrites`, `Settings_init_restores_the_previous_version_instead_of_writing_defaults_over_it`, `Export_says_credentials_are_not_included_and_import_says_where_recordings_will_go`, `Importing_something_that_is_not_settings_exits_1`, `Auth_stores_confirms_and_deletes_a_secret_without_ever_printing_it`, `Doctor_exits_1_when_the_settings_are_broken_and_names_the_problem`, `Version_names_the_exact_build`, `Start_exits_3_when_no_recorder_can_be_started` (a hard-linked copy of the payload without the recorder), `A_relocated_data_root_keeps_the_real_settings_untouched` | Published |
| `status` `0` / `11` / `10` across a real recording's life | `EndToEnd/RecordingEndToEndTests.A_scheduled_style_recording_runs_pauses_resumes_stops_finalises_verifies_and_transfers` (start twice, pause, resume, the while-recording lock, stop, finalise, verify, transfer to a folder) | Gpu |
| Failure path: disk full | `RecordingEndToEndTests.Running_out_of_disk_mid_recording_stops_cleanly_keeps_the_footage_and_says_why` (`lastOutcome` = failed, plain `status` says so, the footage verifies) and `Starting_without_room_to_record_is_refused_in_plain_terms` (no empty recording left behind), both via `CAPTR_SIMULATE_FREE_BYTES_FILE`; `Sessions/FinalizationPipelineTests.Without_room_to_join_the_recording_waits_intact_for_space_rather_than_failing_mid_write` | Gpu / Ffmpeg |
| Failure path: missing FFmpeg | `RecordingEndToEndTests.A_missing_ffmpeg_refuses_to_start_and_says_to_reinstall` | Gpu |
| Failure path: tampered FFmpeg | `RecordingEndToEndTests.A_tampered_ffmpeg_is_never_run`; `Supervision/FfmpegIntegrityTests` (unit: one-bit change refused, no record refused, signing does not change the digest, only the bundled FFmpeg is ever adopted); `Supervision/PinnedFfmpegDigestTests.The_recorded_digest_is_what_the_application_computes` (PowerShell and C# digests agree on the real binaries) | Gpu / — / Ffmpeg |
| Failure path: FFmpeg crash mid-recording | `RecordingEndToEndTests.When_ffmpeg_dies_mid_recording_it_is_restarted_and_the_gap_is_recorded_honestly` | Gpu |
| Failure path: unreachable destination | `RecordingEndToEndTests.An_unreachable_destination_keeps_the_recording_and_the_transfer_waits_with_the_reason` | Gpu |
| Failure path: session 0 | `RecordingEndToEndTests.Starting_in_session_zero_is_refused_with_the_fix_named` (via `CAPTR_SIMULATE_SESSION0`); `Sessions/SessionZeroTests.Planning_a_recording_in_session_zero_is_refused_before_anything_starts`; `WindowsEvents/InteractiveSessionTests` (unit: session 0, a service window station, the user's desktop) | Gpu / Os / — |
| Failure path: bad credentials | `Transfers/TransferWorkerEndToEndTests.A_wrong_secret_pauses_the_transfer_for_new_credentials_and_sends_nothing`, `A_token_SharePoint_rejects_pauses_the_transfer_for_new_credentials`, `A_site_the_app_was_never_granted_parks_the_transfer_with_SharePoint_s_reason`, `A_missing_credential_pauses_the_transfer_for_sign_in_instead_of_killing_the_worker` (against `MockGraphServer`) | Os |
| Failure path: invalid naming pattern | `CliEndToEndTests.Settings_set_validates_and_get_shows_what_was_saved` (`outputPattern {nope}` refused, exit 1, nothing saved); `Naming/OutputNamerTests`, `Naming/TokenFormatTests`, `Naming/NamingPreviewTests`, `Settings/SettingsValidatorTests` (unit) | Published / — |
| Signing scripts | `Build/SigningScriptTests`: `A_dry_run_needs_no_certificate_and_changes_nothing`, `An_ffmpeg_that_is_not_the_pinned_build_is_never_signed_even_in_a_dry_run`, `An_unconfigured_build_warns_and_succeeds_but_a_release_that_requires_signing_fails`, `A_signing_method_with_a_missing_variable_is_named_rather_than_half_working`, `Verification_fails_on_an_unsigned_file`, `Verification_fails_on_a_signature_without_a_timestamp`, `Verification_fails_on_the_wrong_publisher`, `Verification_fails_on_a_legacy_timestamp`, `A_release_without_signing_refuses_before_building_and_tags_nothing`, `A_real_signing_run_signs_what_is_ours_keeps_what_is_trusted_and_passes_verification` (self-signed certificate; needs the network, skipped without it) | Os |
| Overlay text escaping on the real FFmpeg | `Encoders/DrawTextOnRealFfmpegTests.Overlay_text_renders_exactly_as_written` (each text drawn through the escaper and verbatim from a file, frames compared) and `A_time_expansion_still_works` | Ffmpeg |

## SPEC §14 coverage map

Every testing requirement in the specification, and exactly what covers it. Items
marked **deferred** cannot run on the development machine (they need a second
monitor, clean VMs, a code-signing certificate, a Microsoft 365 tenant, or a person
watching); each names its stand-in and lives in
[release-verification.md](release-verification.md) as a ready-to-run checklist item.

## Unit — `tests/Captr.Core.Tests`

| Requirement | Covered by |
|---|---|
| Golden-file encoder arguments: display counts, mismatched sizes, arrangements, overlay on/off | `Encoders/FfmpegArgumentBuilderTests` + 11 files in `Golden/` |
| Text-escaping edge cases | `Encoders/DrawTextEscaperTests` (per-character escape depths), proven against the real binary by `DrawTextOnRealFfmpegTests` (integration) |
| Display identity across reorder, hot-plug, return of a deselected display | `Displays/DisplaySelectionTests` |
| Settings validation | `Settings/SettingsValidatorTests` |
| Schema migration from prior-version fixtures | `Settings/SettingsStoreTests` (migration chain, missing-step refusal, a newer file kept intact as `settings.json.from-schema-N` with defaults used, wrong-shaped JSON treated as corrupt, nulls become defaults) |
| Export omits credentials, import preserves them | `Settings/SettingsStoreTests`; `Settings/ImportSummaryTests` (what an import changes about where recordings go) |
| Output naming: every token, collisions, illegal and reserved names | `Naming/OutputNamerTests` (every reserved device name, the 200-character cap, never splitting a character) |
| Log redaction | `Secrets/SecretRedactionTests` (populated credential object through the real pipeline) |
| Gap accounting from synthetic journals | `Sessions/CoverageCalculatorTests` |
| IPC round-trips, version mismatch fails clearly, unknown kinds ignored | `Ipc/IpcRoundTripTests`, `Ipc/IpcFramingTests` |

Also covered beyond the spec's list:

| | |
|---|---|
| Atomic-file torn-read stress under a concurrent writer | `Common/AtomicFileTests` |
| Journal durability and torn-line tolerance | `Sessions/SessionJournalTests` |
| Supervision policy decision table | `Supervision/SupervisorPolicyTests` |
| Disk-guard thresholds | `Sessions/DiskGuardTests` |
| Transfer queue state machine, bounded retries, cancellation, and backoff | `Transfers/TransferQueueTests` |
| Naming tokens inside a destination FOLDER, and the separators a token may not introduce | `Naming/FolderPathTokenTests` |
| Retry-policy validation, destination folder rules (full path, `..`, streams, device paths), and hotkey rules — the post-release ones never blocking a recording | `Settings/SettingsValidatorTests`, `Interop/HotkeyCombinationTests` |
| Support-bundle secret scrubbing, the live log included, identities replaced | `Diagnostics/SupportBundleTests` |
| Crash reports: content, secret masking, only the newest kept | `Diagnostics/CrashReporterTests` |
| A failed recording stays shown until dismissed, even after the recorder exits | `Ipc/StatusPresentationTests` |
| The recorder's pipe cannot be squatted by another account | `Ipc/PipeOwnershipTests` |
| Concurrent starts make one recording; a start during finalising starts a new one; outcomes reported | `Hosting/HostServiceStartTests` |
| A failed rename never costs the transfer | `Hosting/OutputHandOffTests` |
| Send again never duplicates, re-arms stuck transfers, and sends only the recording's own files | `Hosting/ResendTests` |
| Retention judges each session by its own transfers (the `S1`/`S10` prefix case) | `Transfers/RetentionCleanerTests` |
| A new session folder is private to the user; an existing folder keeps its permissions | `Sessions/WorkingFolderAclTests` |
| The Home transfer line | `Transfers/TransferDigestTests` |

## Integration — `tests/Captr.Integration.Tests`

| Requirement | Covered by | Trait |
|---|---|---|
| Status round-trips with no UI running | `Ipc/CrossProcessHostTests` | Gpu |
| Recording produces a playable file with expected duration and dimensions | `Sessions/RecordingSessionTests`, `EndToEnd/RecordingEndToEndTests`, `build/verify-install.ps1` | Gpu |
| Killing the encoder restarts it into a new segment, gap journaled | `Supervision/EncoderSupervisorTests`, `RecordingEndToEndTests` | Ffmpeg / Gpu |
| Killing the host leaves the encoder writing; re-adopted on restart | `Ipc/HostCrashReadoptionTests` | Gpu |
| Killing everything recovers playable footage automatically | `Ipc/HostCrashReadoptionTests`, `Sessions/FinalizationPipelineTests` | Gpu / Ffmpeg |
| Recovery never counts lost footage as recorded | `FinalizationPipelineTests.Time_the_journal_says_was_recorded_but_no_footage_holds_is_reported_as_lost` | Ffmpeg |
| Recovery leaves a live recording alone | `Sessions/RecoveryOfLiveSessionsTests` | Os |
| Truncated segment repaired, durations reconcile | `Sessions/FinalizationPipelineTests` | Ffmpeg |
| Forcing an unavailable encoder falls back, reports what was used | `Encoders/EncoderSelectionTests` (QSV advertised, no Intel GPU → trial fails) | Gpu |
| Repeated software-encoder failure stops loudly | `Supervision/EncoderSupervisorTests` | Ffmpeg |
| External termination does not count toward fallback | `Supervision/EncoderSupervisorTests` | Ffmpeg |
| A lost desktop retries for ever and never ends the session | `Supervision/EncoderSupervisorTests` (real FFmpeg writes a real gdigrab capture-loss line) | Ffmpeg |
| Capture-loss signatures are really strings inside the shipped ffmpeg.exe | `Supervision/CaptureLossSignatureTests` | Ffmpeg |
| One encoder process's log never condemns the next one's exit | `Supervision/LogRingBufferTests` (unit) | — |
| Mid-session settings changes honoured after pause; exclusions kept across topology changes; a notification that changes nothing does not restart the encoder; the software fallback follows the current plan; shutdown blocking released | `Sessions/RecordingSessionBehaviourTests` | Os |
| Windows broadcasts (sleep, display change, shutdown) reach the recorder | `WindowsEvents/SystemEventWindowTests.Broadcasts_reach_the_window` | Os |
| The display stays on for the whole recording, whichever thread asked | `WindowsEvents/ExecutionStateHolderTests` (reads the machine's real execution state) | Os |
| Killing the UI mid-recording does not interrupt capture; relaunch reattaches | `Ui/UiIndependenceTests` | Gpu |
| Local transfer renames, copies, verifies, never overwrites | `Transfers/FolderDestinationTests` (unit) + `TransferWorkerEndToEndTests.A_file_already_sitting_at_the_partial_name_is_never_overwritten` + `verify-install.ps1` | — / Os |
| Folder destinations on a UNC share | `TransferWorkerEndToEndTests.A_folder_destination_on_a_UNC_share_receives_the_recording` | Os |
| Cloud transfer resumes from the correct offset after interruption | `Transfers/GraphUploaderTests` against `MockGraphServer` | Os |
| An expired upload session is replaced without uploading twice | `TransferWorkerEndToEndTests.An_upload_whose_session_expired_starts_a_new_session_and_completes`, `An_upload_that_finished_but_lost_its_last_reply_is_recognised_rather_than_sent_twice` | Os |
| Uploads address the destination's configured Drive ID (Graph has no "default" alias) | `Transfers/GraphUploaderTests` (session path asserted against the mock) | Os |
| GDI capture fallback declares one input per display with virtual-desktop offsets | golden files `d1_gdi`, `d2_gdi_offsets` (unit) | — |
| Test connection refuses an incomplete form naming the missing field | `Transfers/SharePointConnectionTestTests` (unit) | — |
| Permission failure lands in manual retry with the server's message intact | `Transfers/GraphUploaderTests`, `Transfers/TransferQueueTests`, `TransferWorkerEndToEndTests` | Os / — |
| Stored credential survives a restart, appears in no file/log/bundle | `Secrets/CredentialVaultTests`, `SecretRedactionTests`, `SupportBundleTests` | Os / — |
| Replacing a secret really overwrites it; renaming a destination MOVES its secret and leaves no orphan | `Secrets/CredentialVaultTests` | Os |
| Flooding the error stream while consuming progress: no deadlock | `Supervision/EncoderSupervisorTests` (debug-level flood > 100 KB) | Ffmpeg |
| The data root relocates everything and gives a recorder of its own | `Settings/DataRootTests` | Os |
| **Two-display recording with mismatched resolutions stitches correctly** | **deferred** — needs a second monitor. Argument generation for that exact case is golden-filed (`d2_unequal_hstack_pad`), and `ArrangementPlannerTests` covers the padding maths. | — |
| **Deselected display stays deselected across restart *and reboot*** | partial — identity resolution is unit-tested across simulated topology changes, and `RecordingSessionBehaviourTests` keeps a mid-session exclusion; the reboot leg is **deferred** (manual, in the runbook). | — |
| Filling the disk mid-recording stops cleanly | `RecordingEndToEndTests.Running_out_of_disk_mid_recording_stops_cleanly_keeps_the_footage_and_says_why` (simulated free space); `Sessions/DiskGuardTests` for the thresholds, ballast, and refusal messages. A physically full volume is still a manual check. | Gpu |
| Every setting persists and takes effect | partial — persistence and validation unit-tested; "takes effect" proven for working folder, frame rate, quality, speed preset, and destinations through `verify-install.ps1`, the session tests, and the end-to-end suites. | — |
| Frame rate, speed preset, and quality each map to the right arguments on every encoder | `Encoders/EncodingOptionsTests` (every encoder × every preset × every quality; H.264 offset; lossless mode; software bitrate ordering) | — |
| Naming-pattern format specifiers ({date:yyyy_MM_dd}) render, and bad ones are refused at save time | `Naming/TokenFormatTests` (formats, timezone, illegal characters, invalid format strings) | — |
| A transfer that stops making progress is abandoned and retried rather than hanging | `Transfers/TransferWorkerTests` (size-scaled attempt timeout) | — |
| A settings file from the previous schema upgrades without losing the user's choices | `Settings/SettingsStoreTests` (real v1 fixture: old preset → speed + quality, three hotkeys → two toggles) | — |
| A cache hit skips the trial so a repeat start is immediate | `Encoders/EncoderSelectionTests` | Gpu |
| Retrying a transfer re-sends only the destination that failed | `Transfers/TransferQueueTests` (one row per recording × destination) | — |
| A destination may rename the copy it receives | `Transfers/TransferQueueTests` | — |
| Capture/quality settings locked while recording, degrading changes applied and journaled (SPEC §8) | `Settings/SettingsChangePolicyTests` (unit, all cases) + `Cli/SettingsLockTests` (end to end: refusal, live application, new arrangement group) + `RecordingEndToEndTests` | Gpu |

## Command line and scheduling

| Requirement | Covered by |
|---|---|
| Every command produces correct exit codes and valid JSON | `EndToEnd/CliEndToEndTests` (every command, exit codes 0, 1, 2, 3, 10) and `Cli/CliContractTests` (JSON on stdout, usage errors exiting 2, not 1) |
| A user can determine the exact build from a running installation (SPEC §11) | `captr version` / UI Diagnostics page, from `Common/BuildInfo`; `CliEndToEndTests.Version_names_the_exact_build` |
| Starting twice and stopping when idle both succeed | `Cli/CliContractTests`, `Cli/CliRedirectionTests`, `Ipc/CrossProcessHostTests`, `RecordingEndToEndTests` |
| A recording started by one invocation is stopped by another, in a separate process | `Ipc/CrossProcessHostTests` |
| CLI usable from a scheduler (output captured, returns promptly) | `Cli/CliRedirectionTests` — regression guard for a real hang |
| Task Scheduler task (run only when logged on) starts and stops a recording | **verified on this machine**: `schtasks /Create … /IT` for start and stop, run via `schtasks /Run`, producing a finalised, correctly-named output. The script that did it is reproducible from the worked example in `../user-guide/scheduling.md`. |
| A session-0 task is refused instead of recording black | `RecordingEndToEndTests.Starting_in_session_zero_is_refused_with_the_fix_named` (simulated) and `SessionZeroTests`; a real "whether logged on or not" task on a real machine is a manual check in the runbook. |

## Packaging and upgrade

Silent install, first run, record, transfer, uninstall with data kept, a silent
upgrade over a running recording, a refused downgrade, and an upgrade from the
previous release are covered end to end by `EndToEnd/InstallerEndToEndTests`
(per-user, see [the installer suite](#the-installer-suite)). The signing and
verification scripts are covered by `Build/SigningScriptTests`, and version
handling by `Build/VersionScriptTests`. Per-machine elevated installs with another
account running Captr, the clean Windows 10 + Windows 11 VM matrix, and signing with
a real certificate are **deferred** to the runbook. The licence report gate runs on
every build.

## Chaos — `tests/Captr.Integration.Tests/Chaos`

| Requirement | Covered by |
|---|---|
| Forced kills of each process at randomised points | `ChaosTests` (encoder, 3 randomised rounds) + `HostCrashReadoptionTests` (host) + `UiIndependenceTests` (UI) |
| Trailing bytes of the last segment corrupted, then recovery | `ChaosTests` |
| Working folder deleted mid-session | `ChaosTests.Deleting_the_working_folder_mid_session_ends_loudly_never_hangs` — supervision must fail loudly with a reason within a minute, or stay healthy and stop within a minute of being asked |
| **Power loss mid-write** | partial — the truncated-segment and corrupted-tail cases reproduce its on-disk artefact; a true power cut is **deferred** (manual). |
| UAC secure desktop / session lock / remote disconnect tolerated with backoff, never counted as an encoder fault | `Supervision/SupervisorPolicyTests` (classification of the real ddagrab AND gdigrab wording + capped backoff + reset), `Supervision/EncoderSupervisorTests` end to end |
| A gap still open when the session ends is journaled, so coverage cannot over-report | `Supervision/EncoderSupervisorTests` |
| Screen saver / auto-lock reported before a recording, never overridden | `WindowsEvents/IdleLockPolicyTests` (unit) + `captr doctor` against real registry states |
| Disk filled mid-recording | `RecordingEndToEndTests` with simulated free space (see above) |
| **A physically full disk; display unplugged / resolution / DPI changed on real hardware; sleep-resume; lock-unlock; RDP connect-disconnect; GPU driver reset** | **deferred** — each needs hardware or admin state this machine cannot script safely. The code paths are covered as far as software can reach them (`SystemEventWindowTests` for the broadcasts, `RecordingSessionBehaviourTests` for topology handling); the runbook lists the real-hardware checks. |

## Soak — `tests/Captr.Integration.Tests/Soak`

`SoakTests` runs the REAL capture path (ddagrab → the machine's proven hardware
encoder, through `RecordingSession`) and asserts wall-vs-encoded drift < 1 s, no
handle or memory growth trend, zero repairs, coverage > 99.9 %, a clean integrity
verify, and an internally consistent journal. Duration is `CAPTR_SOAK_MINUTES`;
the runbook sets 600 for the specified ten-hour run — same assertions, longer
clock.

It deliberately does NOT soak a synthetic `lavfi` source: one paced with `-re`
carries about a percent of its own pacing slop, so a drift assertion against it
measures FFmpeg's test-source timer rather than Captr's timeline. Real capture is
clocked by the compositor, which is what the spec's one-second bar is about.
