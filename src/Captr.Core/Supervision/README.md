# Supervision

Keeps the FFmpeg encoder process alive, honest, and observed (SPEC §4/§6). This
folder owns the child process: launching it, watching its progress, deciding when it
has stalled, restarting it into a new segment, falling back from hardware to
software encoding exactly once, and — after a host crash — finding and re-adopting
an encoder that kept writing while nobody watched.

```
EncoderSupervisor (the async loop)
 ├── FfmpegProcess       launch/stop/kill one encoder process
 │     ├── stdin pipe            — kept open ONLY to send 'q' for a graceful stop
 │     ├── -progress FILE        — machine-readable progress (never a pipe: a pipe's
 │     │                           reader dies with the host; a file survives)
 │     └── FFREPORT log FILE     — ffmpeg's own log for diagnostics (same reason)
 ├── FileTailReader       async tail of those growing files
 ├── ProgressReader       parses progress blocks into EncoderProgress values
 ├── LogRingBuffer        bounded tail of the log for the journal/diagnostics
 ├── SupervisorPolicy     PURE decision logic: stalled? restart? fall back? stop?
 └── ProcessAdoption      match {pid, start time, image path} + the CAPTR_SESSION
                          metadata marker to re-adopt an orphaned encoder (SPEC §4)
```

Why the unusual choices:

- **No pipes for output.** Both the progress stream and the log go to files that the
  supervisor *tails*. Two reasons, both load-bearing: reading two pipes needs
  carefully concurrent readers or a full pipe buffer deadlocks the child (SPEC §5
  demands a flood test); and a pipe's reader is the host process — if the host dies,
  the encoder eventually blocks on a full pipe and the recording stops. Files have
  neither failure mode, and they make re-adoption trivial: reopen and keep tailing.
- **No job object.** A job object would kill FFmpeg with its parent — the opposite
  of what SPEC §4 wants. The encoder is *supposed* to outlive a host crash.
- **PID alone never identifies a process.** Windows reuses PIDs aggressively.
  Adoption matches PID + process start time + image path, then cross-checks the
  `CAPTR_SESSION` metadata inside the newest segment (SPEC §4).
- **Policy is a pure class.** Every threshold decision (stall, fault vs external
  termination, the single fallback, stop-loudly) is unit-tested logic; the async
  loop just feeds it observations and executes its verdicts.
- **Only faults count toward fallback** (SPEC §6). A user killing ffmpeg in Task
  Manager restarts the encoder but must not push the session toward the software
  encoder. The distinction is a documented heuristic: an exit is a FAULT if the log
  tail around death contains encoder errors or the process died of a detected
  stall; an exit with a healthy progress stream and a clean log is external.

Thresholds live in `SupervisionConstants` with the consequence of changing each.
