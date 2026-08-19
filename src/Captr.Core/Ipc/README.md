# Ipc

How the UI and the command line talk to the recording host (SPEC §4): a local named
pipe, restricted to the current user, carrying length-prefixed JSON messages.

```
UI / CLI ──► IpcClient ──► \\.\pipe\captr-host-<user-sid-hash> ──► IpcServer ──► HostService
```

- **IpcProtocol** — the wire format: a 4-byte little-endian length prefix followed
  by one UTF-8 JSON envelope `{ "v": 1, "kind": "...", "payload": { … } }`, plus
  the typed request/response records for every operation.
- **IpcServer** — accepts connections (one task per client), enforces the
  handshake, dispatches requests to the host, answers unknown kinds with an error
  *response* (never a dropped connection — old clients must fail politely).
- **IpcClient** — connects, performs the handshake, sends requests. Its
  `EnsureHostRunningAsync` starts `Captr.exe --host` when no host answers — this
  is how "the host starts on demand" (SPEC §4) actually happens.

Rules that matter:

- **Identity**: the pipe's security descriptor admits only the current user's SID.
  A different user on the same machine cannot even connect.
- **Versioning**: the first exchange is `hello`. A protocol-version mismatch gets a
  typed refusal naming both versions, so a stale UI against a newer host fails
  clearly instead of misbehaving (SPEC §4).
- **Unknown kinds are ignored, not fatal** (SPEC §14): the server answers with an
  `error` response; the connection lives on.
- **Secrets**: credential provisioning is the ONLY message carrying secret bytes;
  they are wiped after use and never logged (SPEC §7 redaction applies here too).
