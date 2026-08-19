# Common

Small building blocks shared by every other folder. Nothing in here knows anything
about recording — if a type needs to know what a "session" or "segment" is, it does
not belong in this folder.

Start reading with:

- **AtomicFile** — writes a file so that a reader (or a crash) can never observe a
  half-written state: write to a temporary name, flush to physical disk, then rename
  over the destination. Used by the heartbeat and the settings store. The atomicity
  of the rename is pinned by a dedicated stress test
  (`AtomicFileTests.Concurrent_reader_never_observes_a_torn_file`) rather than
  assumed from framework documentation, as SPEC §6 requires.
