# Sessions

The integrity heart of Captr (SPEC §6). Everything needed to prove, after the worst
possible day — a crash, a forced kill, a power cut — exactly what was recorded, what
was missed, and that nothing on disk has been altered.

How the pieces fit together:

```
 while recording                          on stop OR after a crash
 ───────────────                          ────────────────────────
 SessionJournal   append-only ndjson,     JournalReader    reads the journal back,
                  every append flushed                     tolerating a torn last line
                  to physical disk
 HeartbeatWriter  1 Hz "I am alive"       CoverageCalculator folds the events into a
                  snapshot, written                        CoverageReport: total span,
                  atomically                               every gap, coverage %
```

Start reading with `JournalEvent.cs` — the whole folder is built around that one
event list. Then `SessionJournal` (how events get to disk safely), then
`CoverageCalculator` (how events become an honest coverage statement).

Design rules that matter here:

- **Every append is flushed to physical disk** (`Flush(flushToDisk: true)`). An
  OS-buffered write survives a process crash but not a power cut; the journal must
  survive both (SPEC §6: "this is the difference between a journal that survives
  power loss and one that does not").
- **Events are immutable records, timestamps are UTC** (SPEC §12). The local
  timezone is captured once in `SessionStarted` for later human display.
- **Gaps are never rounded away** (SPEC §6: "account for gaps honestly"). The
  supervisor journals every wall-clock gap; `CoverageCalculator` reports each one
  with its timestamp and duration, plus paused time as its own category.
- The journal reader must never crash on a truncated final line — a torn last line
  IS the expected state after power loss.
