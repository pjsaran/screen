# Sessions

The integrity heart of Captr (SPEC §6). Everything needed to prove, after the worst
possible day — a crash, a forced kill, a power cut — exactly what was recorded, what
was missed, and that nothing on disk has been altered. Plus the engine that runs a
recording from start to finished file.

## While recording

```
RecordingSession   the command loop: start, pause/resume, stop, suspend/resume,
                   topology change, frame-rate degradation, disk pressure.
                   Everything that changes the recording goes through here, one
                   command at a time, so no two changes ever interleave.
  ├── SessionPlanner    the gate BEFORE a session exists: settings valid, at least
  │                     one display, an encoder proven by trial, disk preflighted.
  │                     Every "cannot start" message the user sees is born here.
  ├── SessionJournal    append-only NDJSON; every append flushed to physical disk
  ├── HeartbeatSnapshot 1 Hz "I am alive", written atomically
  ├── SegmentTracker    journals each segment opening and closing, with its SHA-256
  └── DiskGuard         measured-rate preflight, ballast reserve, minutes-remaining
                        warnings, and the clean stop before the disk actually fills
```

## When it ends — and it always ends the same way

```
FinalizationPipeline   ONE path for a clean stop AND for crash recovery:
                       probe every segment → repair truncated ones (originals kept
                       until the repair verifies) → reconcile durations against the
                       journal → hash into integrity.json → join by stream copy,
                       one output per arrangement group → hand to delivery.
                       It never deletes a working file.
  ├── FfprobeClient      asks ffprobe the questions finalisation needs
  ├── CoverageCalculator pure gap arithmetic: the honest coverage statement
  ├── IntegrityRecord    the proof — and VerifyAsync is the later pass that checks it
  ├── RecoveryScanner    at every host start: finds journals with no terminal event,
  │                      re-adopts and stops any encoder still writing to them, then
  │                      runs the pipeline above and reports what was recovered
  └── SegmentClipper     extracts part of a finished recording by stream copy at
                         segment boundaries — no re-encode, so it is instant and the
                         picture is untouched
```

Start reading with `JournalEvent.cs` — the whole folder is built around that one
event list. Then `SessionJournal` (how events reach disk safely), then
`CoverageCalculator` (how events become an honest number), then
`FinalizationPipeline` (the code that runs on the worst day).

## Design rules that matter here

- **Every append is flushed to physical disk.** An OS-buffered write survives a
  process crash but not a power cut; the journal must survive both.
- **Events are immutable records and timestamps are UTC.** The local timezone is
  captured once, in `SessionStarted`, for later human display.
- **Gaps are never rounded away.** Every restart, suspend, pause, and capture loss
  records its real wall-clock duration, and coverage can never read above 100 % —
  a recording must never look *more* complete than it is.
- **The reader must never crash on a truncated final line.** After power loss, a
  torn last line IS the expected state.
- **Recovery and clean stop are the same code.** If they diverge, the path that
  only runs on the worst day is the one that rots.
