# Testing

## Running the tests

```powershell
pwsh build/build.ps1                    # unit + the CI-safe integration categories
pwsh build/build.ps1 -Full              # everything, including Display, Gpu, and Soak
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

**`tests/Captr.Integration.Tests`** — real FFmpeg, real named pipes, real files.
Categorised by trait, because most of them cannot run on a hosted CI runner:

| Trait | Needs | Runs in CI? |
|---|---|---|
| `Ffmpeg` | The bundled FFmpeg, driven with synthetic `lavfi` inputs | yes |
| `Chaos` | The same, plus randomised kills and deliberate corruption | yes |
| `Published` | `publish/captr.exe`, so it runs only AFTER a publish — see the note below | yes, after packaging |
| `Display` | A real interactive desktop (`ddagrab` capture) | no |
| `Gpu` | A real hardware encoder | no |
| `Soak` | Both, plus patience. `CAPTR_SOAK_MINUTES` sets the length (default 6) | no |

A category is a promise about what a test **needs**, and needs are not only
hardware. `Published` exists because the CLI contract tests drive the published
`captr.exe`, which no amount of `dotnet build` produces. They were once `Ffmpeg` —
right that they need no GPU, wrong that they could run before packaging — and so
they passed only on machines carrying a stale `publish/` from an earlier run, while
failing on every clean clone. If a test needs an artefact from a later build stage,
give it its own trait and run it at that stage.

## Writing a test here

- **Test names read as sentences** stating the behaviour:
  `A_transfer_that_runs_out_of_attempts_stops_retrying_and_asks_for_a_person`. If a
  name needs a comment to explain what it means, rename it.
- **Every integration test opens with a comment** describing the scenario in plain
  English, because reproducing one by hand is otherwise an archaeology exercise.
- **Never use `Progress<T>` to collect progress in a test.** It posts to the thread
  pool, so reports arrive after the operation has finished, out of order, and race
  on the collection. Use a synchronous `IProgress<T>` — which is also what
  production does.
- **Assert on behaviour, not on wording**, except where the wording *is* the
  behaviour (an error message a user is meant to act on).

## SPEC §14 coverage map

Every testing requirement in the specification, and exactly what covers it. Items
marked **deferred** cannot run on the development machine (they need a second
monitor, clean VMs, a code-signing certificate, a Microsoft 365 tenant, or admin
rights to fabricate a full disk); each names its stand-in and lives in
[release-verification.md](release-verification.md) as a ready-to-run checklist item.

## Unit — `tests/Captr.Core.Tests`

| Requirement | Covered by |
|---|---|
| Golden-file encoder arguments: display counts, mismatched sizes, arrangements, overlay on/off | `Encoders/FfmpegArgumentBuilderTests` + 9 files in `Golden/` |
| Text-escaping edge cases | `Encoders/DrawTextEscaperTests` (per-character escape depths, validated against the real binary) |
| Display identity across reorder, hot-plug, return of a deselected display | `Displays/DisplaySelectionTests` |
| Settings validation | `Settings/SettingsValidatorTests` |
| Schema migration from prior-version fixtures | `Settings/SettingsStoreTests` (migration chain, missing-step refusal, newer-file refusal) |
| Export omits credentials, import preserves them | `Settings/SettingsStoreTests` |
| Output naming: every token, collisions, illegal and reserved names | `Naming/OutputNamerTests` |
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
| Retry-policy validation, and destination folder patterns rejected before they are saved | `Settings/SettingsValidatorTests` |
| Support-bundle secret scrubbing | `Diagnostics/SupportBundleTests` |

## Integration — `tests/Captr.Integration.Tests`

| Requirement | Covered by | Trait |
|---|---|---|
| Status round-trips with no UI running | `Ipc/CrossProcessHostTests` | Gpu |
| Recording produces a playable file with expected duration and dimensions | `Sessions/RecordingSessionTests`, `build/verify-install.ps1` | Gpu |
| Killing the encoder restarts it into a new segment, gap journaled | `Supervision/EncoderSupervisorTests` | Ffmpeg |
| Killing the host leaves the encoder writing; re-adopted on restart | `Ipc/HostCrashReadoptionTests` | Gpu |
| Killing everything recovers playable footage automatically | `Ipc/HostCrashReadoptionTests`, `Sessions/FinalizationPipelineTests` | Gpu / Ffmpeg |
| Truncated segment repaired, durations reconcile | `Sessions/FinalizationPipelineTests` | Ffmpeg |
| Forcing an unavailable encoder falls back, reports what was used | `Encoders/EncoderSelectionTests` (QSV advertised, no Intel GPU → trial fails) | Gpu |
| Repeated software-encoder failure stops loudly | `Supervision/EncoderSupervisorTests` | Ffmpeg |
| External termination does not count toward fallback | `Supervision/EncoderSupervisorTests` | Ffmpeg |
| A lost desktop retries for ever and never ends the session | `Supervision/EncoderSupervisorTests` (real FFmpeg writes a real gdigrab capture-loss line) | Ffmpeg |
| Capture-loss signatures are really strings inside the shipped ffmpeg.exe | `Supervision/CaptureLossSignatureTests` | Ffmpeg |
| One encoder process's log never condemns the next one's exit | `Supervision/LogRingBufferTests` (unit) | — |
| Killing the UI mid-recording does not interrupt capture; relaunch reattaches | `Ui/UiIndependenceTests` | Gpu |
| Local transfer renames, copies, verifies, never overwrites | `Transfers/FolderDestinationTests` (unit) + `verify-install.ps1` | — |
| Cloud transfer resumes from the correct offset after interruption | `Transfers/GraphUploaderTests` against `MockGraphServer` | — |
| Uploads address the destination's configured Drive ID (Graph has no "default" alias) | `Transfers/GraphUploaderTests` (session path asserted against the mock) | — |
| GDI capture fallback declares one input per display with virtual-desktop offsets | golden files `d1_gdi`, `d2_gdi_offsets` (unit) | — |
| Test connection refuses an incomplete form naming the missing field | `Transfers/SharePointConnectionTestTests` (unit) | — |
| Permission failure lands in manual retry with the server's message intact | `Transfers/GraphUploaderTests`, `Transfers/TransferQueueTests` | — |
| Stored credential survives a restart, appears in no file/log/bundle | `Secrets/CredentialVaultTests`, `SecretRedactionTests`, `SupportBundleTests` | — |
| Replacing a secret really overwrites it; renaming a destination MOVES its secret and leaves no orphan | `Secrets/CredentialVaultTests` | — |
| Flooding the error stream while consuming progress: no deadlock | `Supervision/EncoderSupervisorTests` (debug-level flood > 100 KB) | Ffmpeg |
| **Two-display recording with mismatched resolutions stitches correctly** | **deferred** — needs a second monitor. Argument generation for that exact case is golden-filed (`d2_unequal_hstack_pad`), and `ArrangementPlannerTests` covers the padding maths. | — |
| **Deselected display stays deselected across restart *and reboot*** | partial — identity resolution is unit-tested across simulated topology changes; the reboot leg is **deferred** (manual, in the runbook). | — |
| **Filling the disk mid-recording stops cleanly** | **deferred** — needs a small mounted volume (admin). `Sessions/DiskGuardTests` covers the thresholds, ballast, and refusal messages that drive the clean stop. | — |
| Every setting persists and takes effect | partial — persistence and validation unit-tested; "takes effect" proven for working folder, frame rate, quality, speed preset, and destinations through `verify-install.ps1` and the session tests. | — |
| Frame rate, speed preset, and quality each map to the right arguments on every encoder | `Encoders/EncodingOptionsTests` (every encoder × every preset × every quality; H.264 offset; lossless mode; software bitrate ordering) | — |
| Naming-pattern format specifiers ({date:yyyy_MM_dd}) render, and bad ones are refused at save time | `Naming/TokenFormatTests` (formats, timezone, illegal characters, invalid format strings) | — |
| A transfer that stops making progress is abandoned and retried rather than hanging | `Transfers/TransferWorkerTests` (size-scaled attempt timeout) | — |
| A settings file from the previous schema upgrades without losing the user's choices | `Settings/SettingsStoreTests` (real v1 fixture: old preset → speed + quality, three hotkeys → two toggles) | — |
| A cache hit skips the trial so a repeat start is immediate | `Encoders/EncoderSelectionTests` | Gpu |
| Retrying a transfer re-sends only the destination that failed | `Transfers/TransferQueueTests` (one row per recording × destination) | — |
| A destination may rename the copy it receives | `Transfers/TransferQueueTests` | — |
| Capture/quality settings locked while recording, degrading changes applied and journaled (SPEC §8) | `Settings/SettingsChangePolicyTests` (unit, all cases) + `Cli/SettingsLockTests` (end to end: refusal, live application, new arrangement group) | Gpu |

## Command line and scheduling

| Requirement | Covered by |
|---|---|
| Every command produces correct exit codes and valid JSON | `Cli/CliContractTests` (incl. usage errors exiting 2, not 1) |
| A user can determine the exact build from a running installation (SPEC §11) | `captr version` / UI Diagnostics page, from `Common/BuildInfo` |
| Starting twice and stopping when idle both succeed | `Cli/CliContractTests`, `Cli/CliRedirectionTests`, `Ipc/CrossProcessHostTests` |
| A recording started by one invocation is stopped by another, in a separate process | `Ipc/CrossProcessHostTests` |
| CLI usable from a scheduler (output captured, returns promptly) | `Cli/CliRedirectionTests` — regression guard for a real hang |
| Task Scheduler task (run only when logged on) starts and stops a recording | **verified on this machine**: `schtasks /Create … /IT` for start and stop, run via `schtasks /Run`, producing a finalised, correctly-named output. The script that did it is reproducible from the worked example in `../user-guide/scheduling.md`. |
| **The session-0 half of that check** (configuring "whether logged on or not" and observing that it records nothing usable) | **deferred** — it needs stored credentials for a non-interactive task and produces a deliberately broken recording; the warning is documented prominently in `../user-guide/scheduling.md`. |

## Packaging and upgrade

Silent install, upgrade-preserves-everything, repair, downgrade refusal, uninstall
data retention, and signature validity are scripted (`make-installer.ps1`,
`verify-install.ps1`, the installer's `[Code]` interlocks) and **verified on this
machine**; the clean Windows 10 + Windows 11 VM matrix and real-certificate signing
are **deferred** to the runbook. The licence report gate runs on every build.

## Chaos — `tests/Captr.Integration.Tests/Chaos`

| Requirement | Covered by |
|---|---|
| Forced kills of each process at randomised points | `ChaosTests` (encoder, 3 randomised rounds) + `HostCrashReadoptionTests` (host) + `UiIndependenceTests` (UI) |
| Trailing bytes of the last segment corrupted, then recovery | `ChaosTests` |
| Working folder deleted mid-session | `ChaosTests` |
| **Power loss mid-write** | partial — the truncated-segment and corrupted-tail cases reproduce its on-disk artefact; a true power cut is **deferred** (manual). |
| UAC secure desktop / session lock / remote disconnect tolerated with backoff, never counted as an encoder fault | `Supervision/SupervisorPolicyTests` (classification of the real ddagrab AND gdigrab wording + capped backoff + reset), `Supervision/EncoderSupervisorTests` end to end | — |
| A gap still open when the session ends is journaled, so coverage cannot over-report | `Supervision/EncoderSupervisorTests` | Ffmpeg |
| Screen saver / auto-lock reported before a recording, never overridden | `WindowsEvents/IdleLockPolicyTests` (unit) + `captr doctor` against real registry states | — |
| **Disk filled to zero; display unplugged / resolution / DPI changed; sleep-resume; lock-unlock; RDP connect-disconnect; GPU driver reset** | **deferred** — each needs hardware or admin state this machine cannot script safely. The handling code paths exist and are reviewed (`WindowsEvents`, `DiskGuard`, topology rebuild, capture-loss backoff); the runbook lists them as manual checks. |

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
