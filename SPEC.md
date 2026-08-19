# Captr — Build Specification

> Place at the repo root as `SPEC.md`. Build in one pass — design, implement, test, deliver. No staged milestones; §14 must be green before the work is complete.
>
> **This document specifies behaviour, not structure.** Project layout, namespaces, file and folder naming, command names, settings keys, and output naming conventions are all yours to choose. Where a requirement says "the user can configure X" or "a command exists to do Y," design the surface yourself following the conventions of the platform and the language. If any requirement here conflicts with something you would normally do, follow this file and flag the conflict.

## 1. Overview

Captr is a lightweight Windows screen recorder. It captures one or more displays into a single video file, runs unattended for long periods, and is driven either from a small desktop UI or entirely from the command line.

Design priorities, in order:

1. **The recording survives.** A crash, a forced kill, or a power loss must never leave an unplayable file or lose more than the segment in progress.
2. **No licensing ambiguity.** Every dependency must be unambiguously free for commercial use inside a company. See §2 — this constrains real technical choices.
3. **Light.** No feature that isn't needed. No background agent running when nothing is recording. No scheduler, no update service, no telemetry.
4. **Scriptable.** Anything the UI can do, the command line can do, so recording can be driven by Windows Task Scheduler or any other automation.

### Capture requirements

The recorder must capture **whatever is composited to the screen**, regardless of how an application draws. A significant intended use is recording browser-based applications with dense, small text — WebICE is the specific case to test against. Two consequences follow, and both are load-bearing:

- **Use DXGI Desktop Duplication**, not GDI capture. GDI misses hardware-composited content: GPU-accelerated browser rendering, Direct3D, DirectComposition, and video overlays. A GDI-based recorder produces a file that looks complete while silently omitting exactly the content being recorded. This failure is silent, which makes it the worst kind.
- **Small text must stay legible.** Screen content is high-contrast and mostly static, which compresses well, but aggressive quantisation destroys thin glyphs first. Default quality must keep a dense grid of small numbers readable at 100% zoom in the encoded output. Verify this against real WebICE content, not a synthetic test pattern.

### Not in scope

No audio, microphone, or webcam capture. No video editing. No in-app scheduling of any kind — scheduled recording is achieved by invoking the command line from Windows Task Scheduler. No auto-update mechanism; updates are delivered by running a newer installer.

## 2. Licensing constraints

The application will be used commercially inside a company. Every component must be free for that use with no fee, no revenue threshold, no seat count, and no reciprocal obligation on the application's own source. Treat this as a hard requirement, not a preference.

**Acceptable licences for dependencies:** MIT, Apache-2.0, BSD-2/3-Clause, MS-PL, ISC, Unlicense, or public domain.

**Not acceptable:** GPL or AGPL in anything linked into the application; LGPL where it would be statically linked; MS-RL; anything "free for non-commercial use"; anything with a community-edition revenue or headcount cap; anything requiring a maintenance or sponsorship fee for commercial use.

Three specific decisions follow, each of which costs something:

**FFmpeg must be an LGPL build, not a GPL build.** It is invoked as a separate process, which keeps it clear of the application's own code either way, but a GPL binary redistributed in an installer still carries GPL obligations, and an LGPL build removes the question entirely. The cost is real: **`libx264` is unavailable**, so the software encoder fallback is weaker. Verify at fetch time whether the chosen build includes `libopenh264` (Cisco, BSD-licensed) — if so it is the preferred software fallback; if not, fall back to FFmpeg's native encoders. Hardware encoders (NVENC, Quick Sync, AMF) are all present in LGPL builds and will handle the overwhelming majority of machines, so this fallback is rare in practice.

Ship the FFmpeg licence text and a written offer for the corresponding source alongside the binary, and record the exact upstream build identifier. **Prefer to ship the binary bit-for-bit unmodified** — if DPI awareness or anything else appears to require patching it, first try an external manifest or a configuration-side workaround, and only modify the binary as a last resort, documenting precisely what changed. Applying an Authenticode signature is acceptable and should be documented as such.

**Do not use WiX for the installer.** From v6 it requires an Open Source Maintenance Fee for commercial use, earlier versions are out of community support, and the toolset is under MS-RL. Use **Inno Setup**, whose licence explicitly permits commercial use with no fee, or NSIS (zlib licence) if you prefer. Both produce a signed `.exe` installer, which Intune, SCCM, and PDQ all deploy without difficulty. Only revisit MSI if a specific deployment system genuinely refuses `.exe`, and flag the licensing question if so.

**Verify licences mechanically.** The build must generate a dependency licence report (a tool such as `dotnet-project-licenses` or `nuget-license`) and **fail if any dependency falls outside the acceptable list**, including transitive ones. Commit the generated report so drift is visible in review. A licence that was fine at version 3 is not guaranteed fine at version 4 — this check exists to catch that.

## 3. Technology

.NET 10 (LTS), targeting Windows, x64, published **self-contained** so the machine needs no .NET runtime pre-installed.

| Concern | Choice | Licence |
|---|---|---|
| UI | WPF + **WPF UI** (`wpf-ui`, lepoco) | MIT |
| MVVM | `CommunityToolkit.Mvvm` | MIT |
| Tray icon | `H.NotifyIcon.Wpf` | MIT |
| Win32 interop | `Microsoft.Windows.CsWin32` (source-generated P/Invoke) | MIT |
| DXGI enumeration | `Vortice.Windows` | MIT |
| Capture / encode | Bundled **FFmpeg** (LGPL build) as a child process | LGPL-2.1 |
| Container | Matroska (`.mkv`), segmented | — |
| Logging | `Serilog` + file sink | Apache-2.0 |
| Hosting / DI | `Microsoft.Extensions.Hosting` | MIT |
| Local persistence | `System.Text.Json`; SQLite via `Microsoft.Data.Sqlite` if a queue is needed | MIT |
| Cloud destination | `Microsoft.Identity.Client`, `Microsoft.Graph` | MIT |
| CLI parsing | `System.CommandLine` | MIT |
| Installer | **Inno Setup** | Free for commercial use |

**Do not** use a managed FFmpeg binding (`FFmpeg.AutoGen`, `Sdcb.FFmpeg`) or encode in-process. Linking libav into the application creates the licence entanglement §2 exists to avoid, and GPU encoder faults surface as access violations that .NET cannot reliably recover from — in-process, an encoder fault would take down the supervisor and the recovery logic with it.

**Do not** use a general-purpose FFmpeg process wrapper. The encoder arguments need to be constructed programmatically and asserted against golden files, and the process needs precise stdin, progress-stream, and re-adoption handling that general wrappers do not provide.

**Do** use in-process capture (`Windows.Graphics.Capture` or a DXGI duplication loop) for live UI thumbnails and preview only. That path is independent of recording and may fail without consequence.

## 4. Process model

**One executable, two roles**, selected by a command-line switch: a UI role and a headless recording host. Same assembly, one build, one signature, one install payload. A small launcher or the same executable under a second name provides the CLI entry point.

```
UI role  ──┐
           ├──►  recording host (headless)  ──►  FFmpeg child process
CLI      ──┘         (local IPC)
```

- **The host owns the recording.** All session state, integrity journaling, supervision, and delivery live there. It runs fully without a UI.
- **The host starts on demand and exits when idle.** Do not install a logon task, a Windows Service, or any resident agent. When a recording is requested and no host is running, start one; when recording stops and delivery drains, the host exits after a short idle period. Nothing runs on the machine when nothing is being recorded.
- **The host must run in the user's interactive session.** DXGI Desktop Duplication cannot capture from session 0, so a Windows Service will silently capture nothing. Never register the host as a Windows Service. Put this in a comment at the host bootstrap so it is not "fixed" later. See §10 for the corresponding Task Scheduler constraint.
- **The UI is a view.** It holds no recording state, renders what the host pushes, and can be closed, killed, or relaunched mid-recording with no effect on capture and no visible discontinuity on return.
- **IPC** over a local named pipe with a length-prefixed message framing, restricted to the current user's SID; reject any other identity. Requests, responses, and pushed state events. Version the protocol so a stale UI against a newer host fails clearly rather than misbehaving.
- **FFmpeg is not in a job object** that would kill it with its parent. If the host dies, FFmpeg keeps writing and is re-adopted when the host restarts. Match on process ID together with process start time and image path — start time defeats PID reuse without a WMI query — and cross-check an identifying marker written into the encoder's own metadata.
- Start FFmpeg with output redirected and **no console window**, or a console flashes on every segment boundary and probe call.

## 5. Displays and capture

**Displays are always detected automatically.** Enumerate DXGI outputs for index, native resolution, DPI scale, virtual-desktop position, and refresh rate. Cross-reference the Windows display configuration APIs so each display is labelled with the same number the Windows Display Settings page shows — anything else confuses users immediately.

**All detected displays are recorded by default.** The user may deselect displays, and that choice must persist across restarts, reboots, and hardware changes.

Persisting the selection correctly is subtle and the obvious approach is wrong. DXGI output indices **reorder** when displays are added, removed, or re-cabled, so a stored index can silently start pointing at a different display. Persist selection against a **stable per-display identity** derived from the display configuration APIs (the monitor device path is EDID-derived and survives reconnection), and resolve identity to a current index at each start and after each topology change. Never persist an index. Store the selection as exclusions so that a newly attached display is recorded by default rather than silently ignored, and surface that in the UI when it happens. Block starting if every display has been deselected.

Canvas dimensions derive from the native resolutions of included displays; displays of unequal height are padded before being combined. Choose the arrangement automatically — side-by-side for a small number of displays, a grid when the combined width would become unreasonable.

### Encoder arguments

Construct encoder arguments programmatically as an argument vector, never by concatenating a command string. One capture source per display, combined into one output stream, written as **clock-aligned segments** by the encoder itself rather than by the application. Force keyframes at segment boundaries so that segments can later be joined without re-encoding.

Requirements:

- Set an identical frame rate on every capture source; sources are independently clocked with no cross-source synchronisation.
- Escape any text-overlay format string carefully — colons and backslashes are the usual cause of a filter graph that fails silently. Unit-test the escaping.
- **Golden-file tests** must assert the exact generated filter graph for one, two, and three displays, for equal and unequal display sizes, for each arrangement, and with the overlay on and off.
- Read the encoder's stdout **and** stderr concurrently on separate async paths. Reading them sequentially deadlocks when a pipe buffer fills, and a blocked supervisor is a blind supervisor. Test this deliberately by flooding stderr while consuming the progress stream.

### Encoder selection

At start, probe hardware encoders in preference order and **run a short trial encode of the actual canvas** for each candidate. Appearing in the encoder list proves nothing — a machine can advertise a vendor encoder with no matching GPU and silently produce an empty file. A candidate passes only if the trial output is non-empty and probes back with the expected dimensions and a non-zero packet count.

Prefer HEVC hardware encoders, then H.264 hardware encoders, then software. Cache the winner against GPU identity, driver version, and canvas dimensions, and invalidate the cache when any of those change. Always report the encoder actually in use; never claim hardware acceleration that was not selected. If the canvas exceeds an encoder's dimension limit, try the next encoder, then scale to fit, recording the decision.

Quality is exposed as a small set of named presets spanning roughly "visually lossless, large" to "small, layout legible but small text not." Choose names that describe intent rather than a number. Default to a preset that keeps small text legible, since that is the primary use case. Allow a numeric quality override for users who want it. Show a live size estimate — derived from a short trial encode of the real canvas, not a hardcoded table — expressed in GB per hour, total for a typical session length, and how many hours the free space allows.

## 6. Recording integrity

### Segments and journal

Record as **segmented Matroska**, not a single MP4. MP4 writes its index at finalisation, so an interrupted recording is unplayable; Matroska segments are playable as soon as they are written. Segment length around five minutes.

Maintain a **journal** in the working folder describing the session: identity, start time in UTC with the local timezone recorded separately, machine and user, application and FFmpeg versions, the display set with stable identities, canvas dimensions, encoder configuration, the full argument vector used, and an append-only list of segments and events. Every append must be **flushed to physical disk** — the default flush is not sufficient, and this is the difference between a journal that survives power loss and one that does not.

Maintain a **heartbeat** written roughly once a second, using write-to-temp-then-atomic-rename so a reader never sees a torn file. Pin the atomicity of that rename with a test rather than assuming the framework's file-move overload provides it.

**Account for gaps honestly.** Every encoder restart records its wall-clock gap. The final record states coverage plainly — total span, number of gaps, duration and timestamp of each. Never round a gap away or present a recording as continuous when it isn't.

Hash each segment as it closes and record the hashes, so a later verification pass can prove nothing has been altered on disk.

### Supervision

Consume the encoder's machine-readable progress stream rather than parsing its human-readable log output. Keep a bounded ring buffer of the most recent log output for diagnostics.

Detect and respond to: **no progress for several seconds** (kill, roll to a new segment, record the gap); **unexpected exit** (restart into a new segment, capture the log tail); **sustained encoding slower than real time** (warn, then reduce frame rate); **output file not growing while nominally recording** (treat as a stall); and **capture device lost** (wait briefly, re-enumerate displays, rebuild, restart).

If restarts become frequent, fall back **once** from the hardware encoder to software. If software also fails repeatedly, **stop and report loudly.** Do not add further fallback rungs, and in particular **never fall back to GDI capture** — it would produce a recording that appears complete while omitting hardware-composited content, which is worse than stopping. Frame rate may degrade indefinitely; capture method may not.

Distinguish an external termination — a user ending the task — from an encoder fault. Both restart, but only faults should count toward the fallback threshold.

### Disk

Before starting, measure the actual encoding rate with a trial encode, add generous headroom, and refuse to start if free space is clearly insufficient, stating what is needed. Reserve a ballast file at the start of a session and release it when space becomes critical, so there is always room to finalise cleanly.

Express warnings to the user in **minutes of recording remaining**, not bytes. Warn well ahead; offer a one-click reduction in frame rate and the ability to move the working folder to another volume mid-session (segments need not share a volume). When space becomes critical, close the current segment, finalise, and stop cleanly with a prominent message. **A clean stop beats a disk-full crash.**

### Windows events

The host needs a message-only window to receive these.

| Event | Required behaviour |
|---|---|
| Display sleep | Hold a system-and-display-required execution state for the whole session. **Mandatory** — Desktop Duplication returns black frames once the display powers off. Release it on stop. |
| Suspend | Close the current segment and flush the journal before suspending; resume into a new segment and record the gap. |
| Shutdown or logoff | Register a shutdown block reason, finalise quickly, then release it. |
| Workstation lock | Record the event and keep recording. Never stop. |
| Remote desktop connect, console disconnect | Desktop Duplication loses access. Retry periodically and resume when the console session returns; never end the session silently. |
| UAC secure desktop | Produces a brief access denial. Tolerate with backoff rather than restarting aggressively. |
| Display topology or mode change | Debounce, re-resolve display identities to indices, rebuild, and roll to a new segment. Segments either side are not join-compatible without re-encoding, so group them and produce one output file per arrangement. |
| DPI | Declare per-monitor DPI awareness in the application manifest. Verify empirically whether the bundled encoder binary needs the same under mixed scaling; if it does, prefer an external manifest over patching the binary (§2). |
| System clock change | Detect and record jumps. Store all timestamps in UTC; convert for display only. |

### Pause and finalisation

Pause closes the current segment and stops the encoder; resume starts a new one. Because a forgotten pause is an invisible hole in a recording, make the paused state impossible to miss — a distinct, animated tray state and periodic reminders showing elapsed paused time — and mark the resulting file as containing paused gaps.

Finalisation and crash recovery must be **the same code path**, recovery being that path applied to a session whose last segment is truncated:

1. Ask the encoder to stop cleanly, wait briefly, then terminate.
2. Probe every segment. Repair any truncated one by remuxing with error tolerance, keeping the original until the repair verifies.
3. Reconcile the summed segment durations against the wall-clock span and the journal's events; record any discrepancy.
4. Hash everything, close out the integrity record.
5. Join segments **by stream copy only** — never re-encode — producing one file per display arrangement, and verify the result's duration against the sum of its inputs.
6. Name the output and hand it to delivery. Do not delete the working files.

On every host start, scan for sessions that were never finalised and run this automatically without prompting, then report what was recovered: duration, segment count, repairs, and time lost.

## 7. Output and delivery

When recording stops and finalisation completes, the output is renamed according to a user-configurable pattern and copied to every enabled destination. Support a small set of substitution tokens covering date, start and end time, duration, machine, and user; sanitise against reserved Windows names and illegal characters; and on collision at a destination, suffix rather than overwrite. Never overwrite an existing recording.

Two destination kinds:

**Local or network folder.** Check free space, copy to a temporary name with progress and cancellation, verify by re-reading and comparing size and hash, then rename into place atomically.

**SharePoint via Microsoft Graph.** Use a resumable upload session for large files, with a chunk size that is a multiple of the size Graph requires, sequential chunks, and upload state persisted after every chunk so an interrupted upload resumes rather than restarting. Verify the returned size before marking complete. Support both an application-only credential (client secret or certificate, suitable for unattended machines) and an interactive delegated sign-in.

Delivery runs as a persisted queue so an interrupted transfer resumes after a crash or reboot. Classify failures: transient errors retry automatically with exponential backoff and jitter, honouring any server-supplied retry delay, up to a bounded number of attempts; expired credentials pause and resume after re-authentication; permission, quota, and policy failures go to manual retry showing the server's own message verbatim. Never let a delivery failure endanger the local file.

Delete local working files only after every enabled destination has confirmed and verified the transfer, a user-configured retention period has elapsed, and free space allows. Deleting recordings from the UI requires an explicit typed confirmation.

### Credentials

**No secret may be written to a settings file, a database, a log, or a diagnostics export.**

Store secrets in the Windows Credential Manager, DPAPI-wrapped under the current user with a fixed application entropy so that an exfiltrated blob is useless on another machine or account. Use the DPAPI-backed token cache for the identity library rather than its default in-memory cache, so tokens survive a host restart. Hold secrets in memory as `SecureString` or a byte array zeroed in a `finally` block, never as a plain string field or property. Confine all secret handling to a single module; everything else passes an opaque reference.

**Enforce redaction rather than relying on discipline.** Register a logging policy that scrubs any property whose name suggests a secret, and add a test that serialises a fully-populated credential-bearing object and asserts no secret material appears in the output. The UI is write-only: set a secret, see confirmation that one is stored and when, never read it back. The command line accepts secrets on stdin or via a masked prompt, never as an argument — command lines are visible to every process on the machine. Settings export omits credentials and says so.

## 8. Settings

Keep this small. A user should be able to configure: which displays to record, frame rate, quality preset (plus an optional numeric override), the working folder, the output naming pattern, retention, destinations, global hotkeys, and a handful of cosmetic behaviours such as starting minimised and closing to tray. Nothing else.

Everything else — segment length, keyframe interval, pixel format, supervision thresholds, ballast size, retry policy, log retention — is a constant in code with a sensible value, documented but not exposed.

Store settings in the user's roaming application data as JSON, versioned with forward migration, written atomically. Validate on change with inline errors, and block starting with a specific message when settings are invalid. Provide reset-to-default and export/import. Lock capture and quality settings while recording, permitting only degrading changes — a lower frame rate, a lower quality, or removing a display — each of which rolls a new segment and is journaled.

## 9. User interface

WPF with WPF UI, using its navigation shell, Mica backdrop, and system theme and accent. Keep it to a small number of pages covering: current status while recording, a list of past recordings, delivery status, settings, and diagnostics.

The status view shows recording state, elapsed time, coverage including any gaps, the encoder actually in use, frame rate, the displays being recorded with live thumbnails, disk time remaining, and any degradation prominently. The recordings view lists sessions with duration, size, coverage, and delivery state, and offers opening, playing, verifying, extracting a clip at segment boundaries by stream copy, and re-sending to a destination. The delivery view shows every pending and failed transfer with attempt count and the server's verbatim error alongside a plain-English explanation, with retry and cancel. Diagnostics exports a support bundle containing the journal, logs, integrity record, encoder log tail, and system information — **containing no video content and no secrets**, verified by a test that plants a secret and searches the produced bundle.

The tray icon must make state unmistakable at a glance across idle, recording, paused, and error, with elapsed time and remaining disk in its tooltip, and offer the core actions from its menu. Closing the window hides to tray. Attempting to quit while recording must require confirmation, defaulting to continuing to record. Register global hotkeys for start, pause, and stop with conflict detection and a clear message when another application owns a combination. Enforce a single UI instance; a second launch focuses the first.

## 10. Command line

Everything the UI can do must be available from the command line, because scheduled and automated recording is a primary use case. The command line talks to the recording host over the same IPC, starting one if none is running, so it works with no UI present.

Provide commands covering: starting a recording with optional overrides for displays, frame rate, quality, and a session label; stopping, pausing, and resuming; querying current status; listing and verifying past recordings; joining a recording's segments on demand; listing and retrying deliveries; running recovery; reading and writing settings; and provisioning destination credentials. Name them as you see fit, following normal CLI conventions.

Requirements:

- Every command supports both human-readable and machine-readable (JSON) output.
- **Exit codes are meaningful and documented**, distinguishing at minimum success, idle, paused, and error, so a scheduled task can branch on them.
- Starting when already recording, or stopping when idle, is **not an error** — report the existing state and exit successfully. Schedulers fire twice more often than anyone expects.
- Errors go to stderr, results to stdout.
- Commands return promptly; long operations report progress and support cancellation.

### Task Scheduler

Scheduled recording is configured by the user in Windows Task Scheduler invoking these commands. **Document this prominently**, including one constraint that will otherwise cost someone a day:

> The task must be configured to **run only when the user is logged on**. Selecting "run whether user is logged on or not" places the task in session 0, where Desktop Duplication cannot capture the desktop — the recording will start, appear to succeed, and produce nothing usable.

Also document: run at the highest privileges only if genuinely needed; disable "stop the task if it runs longer than" for long recordings; disable "stop if the computer switches to battery power"; and give a worked example of a start task and a matching stop task.

## 11. Versioning, packaging, and updates

### Versioning

Semantic versioning, with the version defined in exactly one place in the build and flowed to assembly version, file version, informational version, the installer, and the UI's about display. The informational version should include the source commit. Never hand-edit a version in two files.

Every release publishes: the signed installer, a checksum file, and a machine-readable release record capturing version, commit, build timestamp, .NET version, the exact FFmpeg build identifier, and file hashes. A user must be able to determine from a running installation exactly which build they have.

### Installer

A single signed `.exe` installer built with Inno Setup, installing per-machine by default, containing the application, the bundled encoder binaries, the self-contained .NET runtime, and licence texts.

Requirements:

- **Silent and unattended installation** with documented switches, plus documented switches to suppress shortcuts or preset the working folder, so it deploys via Intune, SCCM, or PDQ.
- Add the command-line entry point to the machine `PATH`.
- **Do not install any scheduled task, service, or resident agent.** The application starts on demand.
- Preflight and refuse clearly on an unsupported architecture or Windows version; warn if no hardware encoder is detectable.
- The uninstaller **must not delete** recordings, working files, settings, the delivery queue, or stored credentials. Offer removal as a clearly-labelled, unticked-by-default option.

### Upgrades

Running a newer installer over an existing installation must upgrade in place, and this must be tested, not assumed:

- Detect the installed version and refuse to silently downgrade; permit it only with an explicit switch.
- Stop a running host and UI cleanly before replacing files, and **refuse to proceed if a recording is in progress** unless explicitly forced, in which case finalise the recording first.
- **Preserve** settings, credentials, the delivery queue, and all recordings. Migrate the settings schema forward on first run of the new version.
- Leave no orphaned entry in Programs and Features, and no duplicate `PATH` entry.
- Support repair and reinstall of the same version.
- After upgrading, a previously interrupted session must still be recoverable and a pending delivery must still complete.

### Signing

Sign the application executable, the bundled encoder binaries, and the installer with an Authenticode certificate using SHA-256 and an RFC 3161 timestamp, so signatures remain valid after the certificate expires.

Sign the encoder binaries too. An unsigned executable with a well-known name, spawned by a signed parent and capturing the screen, is close to a textbook endpoint-protection detection; in a managed corporate environment that is the difference between a clean deployment and a week of allowlisting requests. Note that signing modifies the binary, so record it among the documented changes required by §2.

Drive signing from environment variables; never commit a certificate, thumbprint, or password. The build must **succeed unsigned when those variables are absent**, logging a prominent warning, so a developer can build without access to the certificate.

### Build automation

Scripts must be idempotent and fail loudly. They should: fetch a pinned LGPL encoder build and verify it against a committed checksum, refusing to proceed on mismatch; confirm the build actually contains the required capture filter, since generic builds often omit it; generate and check the dependency licence report (§2); build, test, and publish self-contained; sign; produce the installer; sign the installer; and emit checksums and the release record.

Provide a post-install verification script for a clean machine that checks the binaries are present and signed, the command line responds, a short recording produces a playable file of the expected duration and dimensions, and that file reaches a configured destination correctly named. Provide a CI workflow running formatting checks, a warnings-as-errors build, the test suite, the licence check, and packaging, skipping signing when credentials are absent.

## 12. Engineering standards

- Nullable reference types enabled and **warnings treated as errors** across the solution. No warning suppression without a comment justifying it.
- Central package version management; a single shared properties file for common build settings. No version numbers duplicated across project files.
- **No unhandled exception may reach the host process.** Register handlers for unhandled and unobserved-task exceptions that record and continue. A top-level catch-record-retry inside the supervision loop is correct here, not a code smell.
- No synchronous blocking on asynchronous work outside the entry point; enforce with an analyzer. Every asynchronous method accepts and honours a cancellation token. Library code does not capture the synchronisation context.
- All platform invocation is source-generated, confined to one place. Any hand-written declaration needs a comment explaining why generation was insufficient.
- Structured logging with a correlation identifier per session, rolling files with bounded retention, and the redaction policy from §7 registered at logger construction rather than at each call site.
- Timestamps are stored as UTC with offset and converted only at the presentation layer.
- Immutable domain types; public mutable state on the session model is a defect.
- Dependency injection throughout; no static mutable state, no service locator.
- Every type carries a documentation comment stating what it owns and what breaks if it fails.
- Formatting is verified in CI, not left to reviewers.

## 13. Reliability requirements to verify

These are behavioural guarantees the implementation must satisfy, restated here because they are what the test suite exists to prove:

1. A forced kill of any process at any moment leaves playable footage covering everything up to the kill, minus at most the segment in progress.
2. Recordings are never deleted or overwritten without explicit typed confirmation.
3. Recording never stops silently, and never degrades to a capture method that omits content.
4. Loss of network, credentials, or a destination endangers only the delivery, never the local file.
5. The UI crashing, hanging, or being killed does not affect recording.
6. No secret is ever written in plaintext, logged, or included in a diagnostics export.
7. Nothing runs on the machine when nothing is being recorded.

## 14. Testing

No milestones — the application is delivered complete with all of the following passing.

**Unit.** Golden-file assertions on generated encoder arguments across display counts, mismatched sizes, arrangements, and overlay states, including text-escaping edge cases. Display-identity resolution across simulated topology changes: reordering, hot-plug, and the return of a previously deselected display. Settings validation, schema migration from prior-version fixtures, export omitting credentials, import preserving them. Output naming: every token, collision handling, illegal and reserved names. Log redaction. Gap accounting from synthetic journals. IPC round-trips, including a version mismatch failing clearly and unknown message kinds being ignored rather than fatal.

**Integration**, against real FFmpeg, real IPC, and real disk. Status round-trips with no UI running. A recording produces a playable file whose probed duration and dimensions match expectations. Killing the encoder restarts it into a new segment within seconds with the gap journaled. Killing the host leaves the encoder writing and it is re-adopted on restart. Killing everything recovers playable footage automatically on next start. A deliberately truncated segment is repaired and durations reconcile. A two-display recording with mismatched resolutions stitches correctly. A deselected display stays deselected across restart and reboot, while a newly attached display is recorded. Forcing an unavailable encoder falls back and reports what was actually used. Repeated software-encoder failure stops loudly rather than degrading further. An external process termination does not count toward the fallback threshold. Filling the disk mid-recording stops cleanly and never crashes. Killing the UI mid-recording does not interrupt capture and relaunch reattaches with correct state. Every setting persists and takes effect. Local delivery renames, copies, verifies, and never overwrites. Cloud delivery against a mocked service resumes from the correct offset after an interrupted upload, and a permission failure lands in manual retry with the server's message intact. A stored credential survives a host restart and appears in no file, log, or diagnostics bundle. Flooding the encoder's error stream while consuming its progress stream produces no deadlock. **Small text remains legible** in the encoded output at the default preset, verified against real dense-text content.

**Command line and scheduling.** Every command produces correct exit codes and valid JSON. Starting twice and stopping when idle both succeed without error. A recording started by one invocation is stopped by another, minutes later, in a separate process. A Windows Task Scheduler task configured to run only when logged on starts and stops a recording successfully, and the documentation's warning about the logged-on setting is verified by observing that the alternative configuration fails.

**Packaging and upgrade.** Silent installation succeeds on clean Windows 10 and Windows 11 virtual machines. All shipped binaries and the installer are signed with valid timestamps. Installing a newer version over an older one preserves settings, credentials, pending deliveries, and recordings, migrates the settings schema, leaves no duplicate entries, and leaves an interrupted session still recoverable. Upgrade during an active recording is refused unless forced, and when forced finalises the recording first. Uninstall leaves recordings, settings, and credentials intact by default and removes them when the option is selected. Downgrade is refused without an explicit switch. The licence report contains no disallowed licence.

**Chaos**, against a short synthetic session — every case must yield playable footage with accurate gap accounting. Forced kills of each process at randomised points. Power loss mid-write. Disk filled to zero. Display unplugged, resolution changed, DPI scaling changed mid-session. Sleep and resume, lock and unlock, remote desktop connect and disconnect. A forced GPU driver reset under load. Network loss and credential expiry mid-upload; a destination returning throttling and server errors. Working folder deleted mid-session. Trailing bytes of the last segment corrupted, then recovery. **The recovery path must never regress** — it is the one that runs on the worst day.

**Soak.** A ten-hour continuous recording with no handle leak, no unbounded memory growth, no timestamp drift beyond a second, an internally consistent journal, every segment verified, and coverage above 99.9%.

## 15. Definition of done

Everything in §14 passes. The build is warnings-clean, formatting-verified, and produces a licence report containing no disallowed dependency. A signed installer, checksums, and a release record are produced by a single command. Clean installation, silent installation, upgrade from the prior version, repair, and uninstall all verified on clean Windows 10 and Windows 11 machines. A recording started from Task Scheduler on a clean install runs unattended, stops on schedule, and arrives correctly named at both a folder and a SharePoint destination with no user interaction.

Documentation covers: building from source; the encoder fetch step and its licence obligations, including the source offer and any documented modification; code-signing setup; configuring scheduled recording in Task Scheduler with the logged-on-session warning stated plainly; the cloud destination's application registration, required permissions, and consent steps for both credential modes; where credentials are stored and how to rotate them; silent-install and upgrade switches; command-line reference with exit codes; and the location of settings, logs, and recordings.