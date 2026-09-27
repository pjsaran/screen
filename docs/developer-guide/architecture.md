# Captr architecture

Captr records one or more displays into crash-safe segmented video, driven from a
small desktop UI or entirely from the command line. This page is the ten-minute
tour; every folder under `src/Captr.Core` has its own README with the next level of
detail.

## The processes

```
 ┌────────────────┐         ┌──────────────────────────┐        ┌──────────────┐
 │ Captr.App.exe  │  IPC    │ Captr.App.exe --host     │ child  │ ffmpeg.exe   │
 │ (WPF UI)       │◄───────►│ (headless recording host)│───────►│ (encoder)    │
 └────────────────┘  named  └──────────────────────────┘        └──────────────┘
 ┌────────────────┐  pipe        ▲      owns everything: session state,
 │ captr.exe (CLI)│◄─────────────┘      journal, supervision, transfer
 └────────────────┘
```

- **The host owns the recording.** It starts on demand (the UI or CLI spawns it),
  and exits two minutes after the last activity — nothing runs when nothing is
  being recorded. One host per user per Windows session (the `Local\CaptrHost`
  mutex); a host that finds it taken waits for a predecessor's idle exit rather
  than leaving at once.
- **The host owns its pipe.** The pipe name is derived from the user's SID, which
  anyone can compute, and a named pipe's access list is fixed by whoever creates
  its first instance. So the host claims the name as the first instance with an
  explicit owner and a current-user-only ACL, refuses to run if someone else
  already holds it, and keeps a listening instance alive so the name never lapses.
  The UI and CLI connect with `CurrentUserOnly` (the server must be owned by this
  user) and allow only Identification-level impersonation. The pipe is claimed
  **before** the startup recovery scan, so a second host cannot mistake the first
  one's live recording for a crash, and status answers while a long recovery runs.
- **The UI and CLI are views.** They hold no recording state; killing or
  relaunching them never affects capture. Every request has a time limit
  (`IpcClient.TimeoutFor`: 2 minutes; `start`/`stop` 10 minutes; `recover` 1 hour),
  so a stuck host cannot hang a script or a scheduled task.
- **Nothing vanishes silently.** Each of the three processes installs
  `CrashReporter` at start: an unhandled exception is written, scrubbed, to
  `logs/crash-<ui|host|cli>-<UTC time>.txt` (newest ten kept), which the support
  bundle collects. The window reports it once and carries on.
- **FFmpeg outlives the host.** It is deliberately NOT in a job object; if the
  host crashes, the encoder keeps writing segments and is re-adopted (matched by
  PID + process start time + image path + an in-file session marker) when the
  host restarts.
- **Never a Windows Service.** DXGI Desktop Duplication cannot capture from
  session 0; a service would record black. This is stated in code at
  `HostEntryPoint` so nobody "fixes" it.

## One recording, end to end

1. `captr start` (or the UI button) connects to the host's named pipe — spawning a
   host if none answers (`Captr.Core.Ipc`). Starts are single-flight: concurrent
   starts (a schedule, a hotkey, the tray) produce one session, and a start while
   the previous session is still finalising starts a new one while the old one
   finishes in the background.
2. `SessionPlanner` refuses early with a specific message if anything is wrong:
   Captr is in session 0 or on a non-interactive window station
   (`WindowsEvents/InteractiveSession` — Desktop Duplication would record black),
   invalid settings (only rules that predate the release block; later ones such as
   destination-folder placement and hotkeys do not), every display deselected, the
   bundled FFmpeg missing or not the pinned build, no encoder passes its trial
   encode, or insufficient disk for the measured rate (`Captr.Core.Sessions`). A
   refused start removes the session folder the trial created, unless it holds a
   journal or footage.
3. Displays are resolved from **stable EDID identities** to today's DXGI indices —
   indices reorder when cables move, so they are never persisted
   (`Captr.Core.Displays`).
4. `EncoderSelector` proves an encoder by trial-encoding the real canvas
   (advertised support means nothing). That one trial also measures the real
   GB/hour, and both the winner and the rate are cached against GPU + driver +
   canvas + encoding settings — so only the FIRST recording on a machine pays for
   the trial, and every later start begins immediately (`Captr.Core.Encoders`).
5. `FfmpegArgumentBuilder` produces the exact argument vector (golden-file
   tested): `ddagrab` capture per display → pad/stack into one canvas →
   clock-aligned 5-minute Matroska segments with forced keyframes so they later
   join by stream copy.
6. `EncoderSupervisor` runs FFmpeg — only the bundled one, from Captr's own
   `ffmpeg` folder (or `tools/ffmpeg/bin` inside a source checkout), after
   `FfmpegLocator` has checked its signature-independent digest against the one
   `fetch-ffmpeg.ps1` recorded — and watches its machine-readable progress
   (a FILE, not a pipe — files survive host death and cannot deadlock):
   stall → kill and restart into a new segment with the gap journaled honestly;
   repeated faults → ONE fallback to the software encoder; more faults → stop
   loudly. External kills restart but never count toward fallback
   (`Captr.Core.Supervision`).
7. Throughout, `SessionJournal` appends events with every write flushed to
   physical disk, and a 1 Hz atomic heartbeat marks liveness
   (`Captr.Core.Sessions`). The session folder is created by `Common/PrivateFolder`
   with a protected ACL — the user, SYSTEM, Administrators — so footage and the
   files the recorder later trusts cannot be read or planted by other accounts,
   even under a drive root. The working folder the user chose, and any folder that
   already exists, keep their own ACL; where the ACL cannot be applied (some
   shares) the folder is created as before. `ExecutionStateHolder` keeps the system
   and display awake with a **power request** (`PowerCreateRequest` /
   `PowerSetRequest`, visible in `powercfg /requests`), which belongs to a handle
   rather than to a thread — `SetThreadExecutionState` was dropped because an async
   method's pool thread changes under it. `SystemEventWindow`, a hidden
   **top-level** tool window on its own thread, turns Windows' broadcasts —
   suspend/resume, display changes, the clock, shutdown and logoff — into events; a
   message-only window receives no broadcasts, which is why it is not one. A
   display notification restarts the encoder only when the displays being
   recorded actually changed.
8. Stop (or crash recovery — the SAME code path) runs `FinalizationPipeline`:
   probe every segment, repair truncated ones (originals kept), reconcile
   durations against the journal — time the journal says was recorded but no
   surviving footage holds becomes a "footage lost" gap — hash everything into
   `integrity.json`, join by stream copy (a single-segment output is a hard link, no
   second copy; a join checks free space first and, without it, leaves every
   segment intact for recovery), and name the output by the user's pattern. A
   rename that fails falls back to the default pattern, then to the working name;
   it never stops the hand-off to transfers.
9. `TransferQueue` (SQLite, crash-proof) copies the output to every enabled
   destination — a verified folder copy, or a resumable SharePoint upload that
   continues from the last confirmed 320 KiB-multiple chunk
   (`Captr.Core.Transfers`). A transfer failure can never endanger the local file.
10. Much later, `RetentionCleaner` reclaims the disk — but only for sessions that
    are finalised, past the retention period, and transferred at least once with
    **every** one of their own transfers completed and verified (matched on the
    session folder plus a separator, so `S1` is never judged by `S10`'s
    transfers). A recording that exists nowhere but here is never deleted
    automatically.

## How a recording's state reaches people

The host reports a state (`starting`, `recording`, `paused`, `suspended`,
`stopping`, `finalizing`, `completed`, `idle`) and, since a session can end between
two polls, `StatusResponse.LastOutcome`: how the most recent session ended —
`completed`, `failed` (stopped itself: repeated encoder faults, a nearly full disk),
or `faulted` (an internal error) — with a reason, kept until the next start. It is
an additive field; no protocol bump.

`Ipc/StatusPresentation` turns that into what every view shows: an idle recorder
whose last session failed is presented as `failed` until the person dismisses it
or starts again — even after the host has exited and taken `LastOutcome` with it,
because the window's `Tracker` remembers it. `IsActive` and `CanStop` are the one
definition of which states count as a recording in progress, shared by Home, the
rail, and the tray. `captr status` uses the same words, keeping exit code `10`.

## What survives what

| Something dies | What happens |
|---|---|
| The encoder (crash, stall, someone ends the task) | Supervisor restarts it into a new segment within seconds; the gap is journaled with its real duration. Only faults count toward the single hardware→software fallback. |
| The host (crash, kill) | FFmpeg keeps writing — it is deliberately not in a job object. The next host re-adopts it (PID + start time + image path + in-file marker; only the bundled FFmpeg is ever adopted), stops it by attaching to its console and sending Ctrl+C — which FFmpeg treats like `q`, closing the segment properly — killing it only if that is impossible or ignored, and finalises. Recovery never touches a folder whose host is still heartbeating, and explicit `captr recover` runs behind the startup scan, never beside it. Loss is the encoder's unflushed output buffer, not the segment. |
| The UI (crash, kill, closed) | Nothing. It holds no recording state; relaunching reattaches on its next status poll. |
| The machine (power loss) | Segments already written are playable; the truncated last one is repaired by tolerant remux; the journal survives because every append is flushed to physical disk. |
| The network / a destination / a credential | Only the transfer. The local file is untouched, and the queue resumes from the last confirmed chunk. |
| The disk filling | The session stops CLEANLY before it fills, warning in minutes-remaining first, with a ballast file reserved and the join's own space counted, so finalisation has room. The stop is reported as a failure with its reason. |

## Where things live at runtime

Everything lives under one data root, `%LOCALAPPDATA%\Captr` — local, not roaming
(see [design decisions](design-decisions.md)). `Common/CaptrPaths` is the one place
it is derived.

| What | Where |
|---|---|
| Settings | `%LOCALAPPDATA%\Captr\settings.json` (+ `.bak`, `.corrupt`, `.from-schema-N`) |
| Sessions (segments, journal, integrity record) | working folder, default `%LOCALAPPDATA%\Captr\Sessions\<timestamp>-<id>` |
| Logs and crash reports | `%LOCALAPPDATA%\Captr\logs`: the host's `host-<date>.log` (a new file each day or every 20 MB, newest 14 kept), `crash-*.txt`, `cli-last-error.txt` |
| Transfer queue | `%LOCALAPPDATA%\Captr\transfers.db` |
| Encoder cache | `%LOCALAPPDATA%\Captr\encoder-cache.json` |
| Displays seen before | `%LOCALAPPDATA%\Captr\seen-displays.json` |
| SharePoint token cache (DPAPI-protected) | `%LOCALAPPDATA%\Captr\captr-msal-cache.bin` |
| Credentials | Windows Credential Manager (DPAPI-wrapped), never in files |
| The host's pipe and lock | `\\.\pipe\captr-host-<hash of the user's SID>`, `Local\CaptrHost` |

**`CAPTR_DATA_ROOT`** moves every one of those files to another folder, and appends
a suffix derived from that folder to the pipe name, the host's mutex, and the
window's single-instance names, so a relocated Captr is wholly separate: its own
settings, its own recorder, and a window that never hands focus to the user's own
Captr. It grants nothing — it only moves where the user's own files go. The test
suites and `verify-install.ps1` rely on it; see [Testing](testing.md).

## The map

Every folder below has a README explaining, in plain English, what it owns and
where to start reading.

| Folder | Owns |
|---|---|
| `Captr.Core/Common` | Torn-read-free file writing, build identity, where state lives (`CaptrPaths`), free space for any folder including UNC (`FreeSpace`), private session folders (`PrivateFolder`) |
| `Captr.Core/Displays` | Which monitors exist, and stable identity for them |
| `Captr.Core/Encoders` | Arrangement, the FFmpeg argument vector, encoder proving, the frame-rate / speed / quality catalogues |
| `Captr.Core/Supervision` | Keeping the encoder alive and honest; re-adoption |
| `Captr.Core/Sessions` | The recording engine, the journal, finalisation and recovery |
| `Captr.Core/WindowsEvents` | Sleep, lock, RDP, display change, shutdown; the power request; the session 0 refusal |
| `Captr.Core/Ipc` | The named-pipe protocol between UI/CLI and host |
| `Captr.Core/Hosting` | The headless host: operations and lifecycle |
| `Captr.Core/Transfers` | The persisted queue, destinations, retention |
| `Captr.Core/Secrets` | The only place secrets exist; enforced redaction |
| `Captr.Core/Settings` | The small settings model, validation, migration, the recording lock |
| `Captr.Core/Naming` | Output names: tokens, sanitisation, collisions |
| `Captr.Core/Diagnostics` | The health checks behind the Diagnostics page, the support bundle (no video, no secrets, identities replaced), and crash reports |
| `Captr.Core/Cli` | Every command and its documented exit code |
| `Captr.App` | The WPF UI (design system in `Theme/`, six pages, the tray), and the `--host` role switch |

## Reading order for a new contributor

1. `SPEC.md` — the contract everything maps back to.
2. This page.
3. `src/Captr.Core/Sessions/README.md` — the integrity model.
4. `src/Captr.Core/Supervision/README.md` — how the encoder is kept honest.
5. The folder README nearest whatever you are changing.
