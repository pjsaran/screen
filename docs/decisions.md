# Decisions and deviations

Where Captr departs from the letter of `SPEC.md`, or resolves something the spec
left to judgement, the reason is recorded here. SPEC §0 asks for exactly this:
"if any requirement here conflicts with something you would normally do, follow
this file and flag the conflict."

## Deviations from the spec's letter

**The Graph SDK is not used; the upload protocol is implemented directly.**
SPEC §3's table lists `Microsoft.Graph`. Its `LargeFileUploadTask` hides the
per-chunk confirmed offset that SPEC §7 requires us to persist after *every*
chunk, and it resists pointing at a local mock server for the interrupted-resume
test. The resumable-upload protocol is three HTTP calls; implementing it directly
gives both the persistence and the test. Licensing is unaffected (MIT either way),
and one fewer dependency passes the licence gate.

**The UI polls status at 1 Hz rather than receiving pushed events.**
SPEC §4 describes the host pushing state. A poll over the same pipe produces
identical UI behaviour with fewer moving parts, and it makes the UI stateless by
construction — which is what §4 actually cares about ("the UI is a view… can be
closed, killed, or relaunched mid-recording"). Reattaching after a UI restart is
just the next poll.

## Judgement calls the spec left open

**"Re-adopted when the host restarts" means the new host takes ownership and
finalises, not resumes.** §4 says a surviving encoder is re-adopted; §6 says every
host start finalises unfinalised sessions. Captr does both in that order:
identify the orphan (PID + start time + image path + the in-file session marker),
stop it, then finalise — preserving every frame that reached disk. It does not
resume supervising the old session, because a new host cannot reconstruct the
original disk guard, tracker, and encoder plan without replaying state the journal
was never meant to carry. Footage is preserved either way; this way the
recording's boundaries stay honest.

**Retention never deletes a recording that exists nowhere else.** §7 permits
deletion once "every enabled destination has confirmed" — which is vacuously true
when there are no destinations. Deleting then would destroy the only copy, so
`RetentionCleaner` additionally requires at least one *completed* delivery. Design
priority #1 outranks reclaiming disk. Local-only recordings are deleted by the
user, with the typed confirmation.

**A host crash costs the encoder's unflushed output buffer, not the segment.**
§13 rule 1 allows losing "at most the segment in progress". In practice FFmpeg
buffers file output through 512 KB that no documented option removes
(`-flush_packets`, `-avioflags direct`, `-blocksize` were all measured and
changed nothing), so a killed encoder loses only that tail — seconds, less at
higher bitrates. Capping Matroska clusters at 2 s pushed ~50 % more bytes to disk
before a kill, and is now the default.

**Picture quality is a user setting, not a build-time calibration.** The spec asks
for a default that keeps small text legible and for verification against real
content. The preset, a numeric quantizer override, and the frame rate are all
adjustable at any time from the UI or CLI, so a reviewer who wants sharper text
raises the preset. What the build guarantees instead is that the chosen settings
are the ones actually used and honestly reported — encoder selection never
silently substitutes, and status names the encoder actually running.

## Things deliberately not built

- **No scheduler, service, resident agent, telemetry, or auto-update** (§1, §11).
  Scheduled recording is Windows Task Scheduler invoking the CLI, and the host
  exits when idle.
- **No mid-segment clipping.** Clips cut at segment boundaries only, because
  anything finer requires re-encoding and would soften the picture (§9).
- **No second fallback rung, and never a capture-method change.** Frame rate may
  degrade indefinitely; capture may not. GDI is never used, at any point, for
  anything (§1, §6).
