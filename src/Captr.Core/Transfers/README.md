# Transfers

Getting finished recordings where they belong (SPEC §7), without ever endangering
the local file (SPEC §13 rule 4). Everything here assumes the transfer WILL be
interrupted — by a crash, a reboot, a dead network, an expired credential — and is
built to resume rather than restart.

```
finalised output ──► TransferQueue (SQLite, survives crash/reboot)
                         │  drained by TransferWorker in the host
             ┌───────────┴───────────┐
     FolderDestination        GraphUploader (SharePoint)
     copy → verify → rename   resumable upload session, sequential
     never overwrite          320 KiB-multiple chunks, offset persisted
                              after EVERY chunk, resume via
                              nextExpectedRanges
```

## The pieces

- **TransferQueue** — the persisted state, and every state transition. One row per
  (recording, destination) with its state, attempt count, next-attempt time, the
  upload session URL and confirmed offset, byte progress, and the server's last
  error verbatim. The states are constants on the class, so a typo in one SQL
  string cannot invent a state nothing else recognises.

- **TransferWorker** — the host's drain loop. Owns dispatch to the right
  destination kind, the failure classification on the way back, the per-attempt
  deadline, and noticing that somebody pressed Stop in another process.

- **FolderDestination** — copy to a temporary name, re-read and compare size and
  hash, then rename into place atomically; collisions suffix, never overwrite.

- **GraphUploader** — the Microsoft Graph resumable-upload protocol over plain
  HttpClient with an injectable base URL, which is what lets the interrupted-resume
  tests run against a local mock server. (Deliberate deviation from the SPEC §3
  table's Microsoft.Graph SDK: the SDK's `LargeFileUploadTask` hides the per-chunk
  offset persistence SPEC §7 requires and resists base-URL redirection for tests;
  the protocol itself is three HTTP calls. Flagged as the spec invites.) Uploads
  are addressed by the destination's explicit **Drive ID** (`drives/{id}/…`) —
  Graph has no "default" drive alias, and pretending it did was the
  "invalid drive id" failure every upload used to hit.

- **SharePointConnectionTest** — the editor's Test connection button: sign in
  with what is typed in the boxes and ask Graph for the drive the Drive ID
  names. Proves the details before a recording depends on them; stores nothing,
  touches no file in the library.

- **RetentionCleaner** — the one place Captr deletes recorded data on its own:
  only a session that is finalised, transferred *and* verified to at least one
  destination, and past the retention period. A recording that exists nowhere but
  the working folder is never removed automatically, because the recording
  surviving outranks reclaiming disk.

- Credentials come from `Captr.Core.Secrets` **by name**; nothing here ever holds
  one.

## Three things worth understanding before changing anything

**Retrying is bounded, and running out is not a failure mode.** SPEC §7's failure
taxonomy is transient (5xx / 429 / network — retried with exponential backoff and
jitter, honouring `Retry-After`), expired credentials (pause until re-auth), and
permission/quota/policy (park immediately, showing the server's own words).
Transient retries stop after `RetrySettings.MaxAttempts` and park for a person
rather than retrying forever: invisible endless retrying is what makes a queue feel
broken. Parking loses nothing — the row keeps its error, the local file is
untouched, and a person can Retry.

**Where a transfer lands is decided when it is QUEUED, not when it runs.** A
destination folder may contain naming tokens (`\\archive\{date:yyyy-MM}`), and a
transfer that fails at 23:59 and retries at 00:01 must land where it was meant to.
The expanded folder is stored on the row. `TransferWorker.ResolveFolder` still
expands as a fallback, because a row can arrive without one (queued by an older
version, or by a path with no naming context) and an unexpanded token reaching the
file system fails every single attempt.

**Progress is reported synchronously, on the copying thread.** `Progress<T>` posts
each report to the thread pool, so reports land out of order and can arrive after
the transfer has been marked complete. `ThrottledProgress` reports inline and
rate-limits itself to one database write a second — enough for a bar, and not
thousands of writes per transfer.
