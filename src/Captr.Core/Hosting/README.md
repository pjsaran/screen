# Hosting

The recording host's brain, UI-free (SPEC §4: "the host owns the recording…
it runs fully without a UI"). `Captr.exe --host` is a thin shell around this folder.

- **HostService** — implements the IPC operations: start (idempotent — starting
  while recording reports the existing session and succeeds, because schedulers
  fire twice, SPEC §10), stop/pause/resume, status, list/verify recordings,
  recover. Owns the single active `RecordingSession` and the recovery scan that
  runs before anything else on host start.
- **HostRuntime** — the lifecycle: single-instance mutex (a second host exits
  immediately), Serilog file logging, the system-event window, the IPC server,
  and the idle timer — when nothing has been recording and nothing is pending for
  a few minutes, the host exits, because nothing may run when nothing records
  (SPEC §1/§4).

Never register any of this as a Windows Service or scheduled agent — see the
warning at `HostEntryPoint` in Captr.App: session 0 cannot capture the desktop.
