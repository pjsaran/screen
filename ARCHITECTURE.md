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
 │ captr.exe (CLI)│◄─────────────┘      journal, supervision, delivery
 └────────────────┘
```

- **The host owns the recording.** It starts on demand (the UI or CLI spawns it),
  and exits a couple of minutes after the last activity — nothing runs when
  nothing is being recorded.
- **The UI and CLI are views.** They hold no recording state; killing or
  relaunching them never affects capture.
- **FFmpeg outlives the host.** It is deliberately NOT in a job object; if the
  host crashes, the encoder keeps writing segments and is re-adopted (matched by
  PID + process start time + image path + an in-file session marker) when the
  host restarts.
- **Never a Windows Service.** DXGI Desktop Duplication cannot capture from
  session 0; a service would record black. This is stated in code at
  `HostEntryPoint` so nobody "fixes" it.

## One recording, end to end

1. `captr start` (or the UI button) connects to the host's named pipe — spawning a
   host if none answers (`Captr.Core.Ipc`).
2. `SessionPlanner` refuses early with a specific message if anything is wrong:
   invalid settings, every display deselected, no encoder passes its trial encode,
   or insufficient disk for the measured rate (`Captr.Core.Sessions`).
3. Displays are resolved from **stable EDID identities** to today's DXGI indices —
   indices reorder when cables move, so they are never persisted
   (`Captr.Core.Displays`).
4. `EncoderSelector` proves an encoder by trial-encoding the real canvas
   (advertised support means nothing), caches the winner against GPU + driver +
   canvas, and `SizeEstimator` measures the real GB/hour (`Captr.Core.Encoders`).
5. `FfmpegArgumentBuilder` produces the exact argument vector (golden-file
   tested): `ddagrab` capture per display → pad/stack into one canvas →
   clock-aligned 5-minute Matroska segments with forced keyframes so they later
   join by stream copy.
6. `EncoderSupervisor` runs FFmpeg and watches its machine-readable progress
   (a FILE, not a pipe — files survive host death and cannot deadlock):
   stall → kill and restart into a new segment with the gap journaled honestly;
   repeated faults → ONE fallback to the software encoder; more faults → stop
   loudly. External kills restart but never count toward fallback
   (`Captr.Core.Supervision`).
7. Throughout, `SessionJournal` appends events with every write flushed to
   physical disk, and a 1 Hz atomic heartbeat marks liveness
   (`Captr.Core.Sessions`).
8. Stop (or crash recovery — the SAME code path) runs `FinalizationPipeline`:
   probe every segment, repair truncated ones (originals kept), reconcile
   durations against the journal, hash everything into `integrity.json`, join by
   stream copy, name the output by the user's pattern.
9. `DeliveryQueue` (SQLite, crash-proof) copies the output to every enabled
   destination — a verified folder copy, or a resumable SharePoint upload that
   continues from the last confirmed 320 KiB-multiple chunk
   (`Captr.Core.Delivery`). A delivery failure can never endanger the local file.
10. Much later, `RetentionCleaner` reclaims the disk — but only for sessions that
    are finalised, delivered *and* verified somewhere else, and past the
    retention period. A recording that exists nowhere but here is never deleted
    automatically.

## What survives what

| Something dies | What happens |
|---|---|
| The encoder (crash, stall, someone ends the task) | Supervisor restarts it into a new segment within seconds; the gap is journaled with its real duration. Only faults count toward the single hardware→software fallback. |
| The host (crash, kill) | FFmpeg keeps writing — it is deliberately not in a job object. The next host re-adopts it (PID + start time + image path + in-file marker), stops it, and finalises. Loss is the encoder's unflushed output buffer, not the segment. |
| The UI (crash, kill, closed) | Nothing. It holds no recording state; relaunching reattaches on its next status poll. |
| The machine (power loss) | Segments already written are playable; the truncated last one is repaired by tolerant remux; the journal survives because every append is flushed to physical disk. |
| The network / a destination / a credential | Only the transfer. The local file is untouched, and the queue resumes from the last confirmed chunk. |
| The disk filling | The session stops CLEANLY before it fills, warning in minutes-remaining first, with a ballast file reserved so finalisation always has room. |

## Where things live at runtime

| What | Where |
|---|---|
| Settings | `%APPDATA%\Captr\settings.json` |
| Sessions (segments, journal, integrity record) | working folder, default `%LOCALAPPDATA%\Captr\Sessions\<timestamp>-<id>` |
| Logs | `%LOCALAPPDATA%\Captr\logs` |
| Delivery queue | `%LOCALAPPDATA%\Captr\delivery.db` |
| Encoder cache | `%LOCALAPPDATA%\Captr\encoder-cache.json` |
| Credentials | Windows Credential Manager (DPAPI-wrapped), never in files |

## The map

Every folder below has a README explaining, in plain English, what it owns and
where to start reading.

| Folder | Owns |
|---|---|
| `Captr.Core/Common` | Torn-read-free file writing, build identity |
| `Captr.Core/Displays` | Which monitors exist, and stable identity for them |
| `Captr.Core/Encoders` | Arrangement, the FFmpeg argument vector, encoder proving, quality |
| `Captr.Core/Supervision` | Keeping the encoder alive and honest; re-adoption |
| `Captr.Core/Sessions` | The recording engine, the journal, finalisation and recovery |
| `Captr.Core/WindowsEvents` | Sleep, lock, RDP, display change, shutdown |
| `Captr.Core/Ipc` | The named-pipe protocol between UI/CLI and host |
| `Captr.Core/Hosting` | The headless host: operations and lifecycle |
| `Captr.Core/Delivery` | The persisted queue, destinations, retention |
| `Captr.Core/Secrets` | The only place secrets exist; enforced redaction |
| `Captr.Core/Settings` | The small settings model, validation, migration, the recording lock |
| `Captr.Core/Naming` | Output names: tokens, sanitisation, collisions |
| `Captr.Core/Diagnostics` | The support bundle (no video, no secrets) |
| `Captr.Core/Cli` | Every command and its documented exit code |
| `Captr.App` | The WPF UI, and the `--host` role switch |

## Reading order for a new contributor

1. `SPEC.md` — the contract everything maps back to.
2. This page.
3. `src/Captr.Core/Sessions/README.md` — the integrity model.
4. `src/Captr.Core/Supervision/README.md` — how the encoder is kept honest.
5. The folder README nearest whatever you are changing.
