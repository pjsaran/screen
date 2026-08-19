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

## Where things live at runtime

| What | Where |
|---|---|
| Settings | `%APPDATA%\Captr\settings.json` |
| Sessions (segments, journal, integrity record) | working folder, default `%LOCALAPPDATA%\Captr\Sessions\<timestamp>-<id>` |
| Logs | `%LOCALAPPDATA%\Captr\logs` |
| Delivery queue | `%LOCALAPPDATA%\Captr\delivery.db` |
| Encoder cache | `%LOCALAPPDATA%\Captr\encoder-cache.json` |
| Credentials | Windows Credential Manager (DPAPI-wrapped), never in files |

## Reading order for a new contributor

1. `SPEC.md` — the contract everything maps back to.
2. This page.
3. `src/Captr.Core/Sessions/README.md` — the integrity model.
4. `src/Captr.Core/Supervision/README.md` — how the encoder is kept honest.
5. The folder README nearest whatever you are changing.
