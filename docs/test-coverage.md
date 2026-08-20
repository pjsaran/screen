# SPEC §14 coverage map

Every testing requirement in the specification, and exactly what covers it. Items
marked **deferred** cannot run on the development machine (they need a second
monitor, clean VMs, a code-signing certificate, a Microsoft 365 tenant, or admin
rights to fabricate a full disk); each names its stand-in and lives in
`docs/release-verification.md` as a ready-to-run checklist item.

Run everything runnable with `pwsh build/build.ps1 -Full`
(unit + Ffmpeg + Chaos + Display + Gpu + Soak).

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

Also covered beyond the spec's list: atomic-file torn-read stress
(`Common/AtomicFileTests`), journal durability and torn-line tolerance, supervision
policy decision table (`Supervision/SupervisorPolicyTests`), disk-guard thresholds,
delivery queue state machine and backoff, support-bundle secret scrubbing.

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
| Killing the UI mid-recording does not interrupt capture; relaunch reattaches | `Ui/UiIndependenceTests` | Gpu |
| Clip extraction at segment boundaries by stream copy (SPEC §9) | `Sessions/SegmentClipperTests` (duration maths, byte-level proof of no re-encode, range validation) | Ffmpeg |
| Local delivery renames, copies, verifies, never overwrites | `Delivery/FolderDestinationTests` (unit) + `verify-install.ps1` | — |
| Cloud delivery resumes from the correct offset after interruption | `Delivery/GraphUploaderTests` against `MockGraphServer` | — |
| Permission failure lands in manual retry with the server's message intact | `Delivery/GraphUploaderTests`, `Delivery/DeliveryQueueTests` | — |
| Stored credential survives a restart, appears in no file/log/bundle | `Secrets/CredentialVaultTests`, `SecretRedactionTests`, `SupportBundleTests` | — |
| Flooding the error stream while consuming progress: no deadlock | `Supervision/EncoderSupervisorTests` (debug-level flood > 100 KB) | Ffmpeg |
| **Two-display recording with mismatched resolutions stitches correctly** | **deferred** — needs a second monitor. Argument generation for that exact case is golden-filed (`d2_unequal_hstack_pad`), and `ArrangementPlannerTests` covers the padding maths. | — |
| **Deselected display stays deselected across restart *and reboot*** | partial — identity resolution is unit-tested across simulated topology changes; the reboot leg is **deferred** (manual, in the runbook). | — |
| **Filling the disk mid-recording stops cleanly** | **deferred** — needs a small mounted volume (admin). `Sessions/DiskGuardTests` covers the thresholds, ballast, and refusal messages that drive the clean stop. | — |
| Every setting persists and takes effect | partial — persistence and validation unit-tested; "takes effect" proven for working folder, frame rate, quality preset, and destinations through `verify-install.ps1` and the session tests. | — |
| Capture/quality settings locked while recording, degrading changes applied and journaled (SPEC §8) | `Settings/SettingsChangePolicyTests` (unit, all cases) + `Cli/SettingsLockTests` (end to end: refusal, live application, new arrangement group) | Gpu |

## Command line and scheduling

| Requirement | Covered by |
|---|---|
| Every command produces correct exit codes and valid JSON | `Cli/CliContractTests` (incl. usage errors exiting 2, not 1) |
| A user can determine the exact build from a running installation (SPEC §11) | `captr version` / UI Diagnostics page, from `Common/BuildInfo` |
| Starting twice and stopping when idle both succeed | `Cli/CliContractTests`, `Cli/CliRedirectionTests`, `Ipc/CrossProcessHostTests` |
| A recording started by one invocation is stopped by another, in a separate process | `Ipc/CrossProcessHostTests` |
| CLI usable from a scheduler (output captured, returns promptly) | `Cli/CliRedirectionTests` — regression guard for a real hang |
| Task Scheduler task (run only when logged on) starts and stops a recording | **verified on this machine**: `schtasks /Create … /IT` for start and stop, run via `schtasks /Run`, producing a finalised, correctly-named output. The script that did it is reproducible from the worked example in `docs/task-scheduler.md`. |
| **The session-0 half of that check** (configuring "whether logged on or not" and observing that it records nothing usable) | **deferred** — it needs stored credentials for a non-interactive task and produces a deliberately broken recording; the warning is documented prominently in `docs/task-scheduler.md`. |

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
| UAC secure desktop / session switch tolerated with backoff, never counted as an encoder fault | `Supervision/SupervisorPolicyTests` (classification + capped backoff + reset) | — |
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
