# Delivery

Getting finished recordings where they belong (SPEC §7), without ever endangering
the local file (SPEC §13 rule 4). Everything here assumes the transfer WILL be
interrupted — by a crash, a reboot, a dead network, an expired credential — and is
built to resume rather than restart.

```
finalised output ──► DeliveryQueue (SQLite, survives crash/reboot)
                         │  drained by DeliveryWorker in the host
             ┌───────────┴───────────┐
     FolderDestination        GraphUploader (SharePoint)
     copy → verify → rename   resumable upload session, sequential
     never overwrite          320 KiB-multiple chunks, offset persisted
                              after EVERY chunk, resume via
                              nextExpectedRanges
```

- **DeliveryQueue** — the persisted state. A row per (recording, destination) with
  state, attempt count, next-attempt time, the upload session URL and confirmed
  offset, and the server's last error verbatim.
- **RetryClassifier** — SPEC §7's failure taxonomy: transient (5xx/429/network)
  retries with exponential backoff + jitter honouring Retry-After; expired
  credentials pause until re-auth; permission/quota/policy failures park in
  manual-retry showing the server's own words.
- **FolderDestination** — copy to a temporary name, re-read and compare size and
  hash, then rename into place atomically; collisions suffix, never overwrite.
- **GraphUploader** — the Microsoft Graph resumable-upload protocol over plain
  HttpClient with an injectable base URL, which is what lets the interrupted-resume
  tests run against a local mock server. (Deliberate deviation from the SPEC §3
  table's Microsoft.Graph SDK: the SDK's LargeFileUploadTask hides the per-chunk
  offset persistence SPEC §7 requires and resists base-URL redirection for tests;
  the protocol itself is three HTTP calls. Flagged as the spec invites.)
- Credentials come from `Captr.Core.Secrets` by name; nothing here holds one.
