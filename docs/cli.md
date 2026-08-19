# captr command-line reference

Everything the UI can do is available here (SPEC §10). Every command supports
`--json` for machine-readable output; errors go to stderr, results to stdout.

## Exit codes (a contract — scripts may branch on them)

| Code | Meaning |
|---|---|
| 0 | Success. Includes the idempotent cases: `start` while already recording, `stop` while idle. |
| 1 | The command failed; details on stderr. |
| 2 | Bad arguments/usage. |
| 3 | No recording host answered and none could be started. |
| 10 | `status`: idle — nothing is recording. |
| 11 | `status`: a recording exists but is paused. |

## Commands

### Recording

```
captr start [--fps N] [--quality PRESET] [--label TEXT] [--json]
captr stop   [--json]
captr pause  [--json]
captr resume [--json]
captr status [--json]
```

- `start` summons a recording host if none is running, plans the session (settings
  validated, displays resolved, encoder proven by a trial encode, disk preflighted
  at the measured rate) and begins recording. Starting while recording is a
  SUCCESS that reports the existing session.
- Quality presets: `archival`, `sharp-text` (default — dense small text stays
  readable), `balanced`, `compact`.
- `stop` returns promptly; finalisation and delivery continue in the background —
  watch with `status` / `delivery list`.
- `status` exit code encodes the state (see table) so scheduled tasks can branch
  without parsing output.

### Recordings

```
captr recordings list [--json]
captr recordings verify <session-folder> [--json]
```

`verify` recomputes every SHA-256 against the session's integrity record, proving
the footage is unaltered on disk (exit 1 with a list of problems otherwise).

### Recovery

```
captr recover [--json]
```

Finalises any session interrupted by a crash or power cut (the host also does this
automatically at every start). Reports duration recovered, repairs, and time lost.

### Delivery

```
captr delivery list [--json]
captr delivery retry <id> [--json]
```

`list` shows every transfer with attempts and the destination server's verbatim
error message; `retry` re-queues a failed one.

### Settings

```
captr settings get
captr settings set <key> <value>
captr settings export > captr-settings.json
captr settings import captr-settings.json
```

Keys: `frameRate`, `qualityPreset`, `qualityOverride`, `workingFolder`,
`outputPattern`, `retentionDays`, `startMinimised`, `closeToTray`,
`excludedDisplayIds` (semicolon-separated stable ids). All changes are validated
before saving. Exports NEVER contain credentials, and say so in the file; import
leaves stored credentials untouched.

### Credentials

```
captr auth set-secret <name>     # reads stdin when piped, else masked prompt
captr auth status <name>
captr auth delete <name>
```

Secrets are **never accepted as command-line arguments** — argument lists are
visible to every process on the machine (SPEC §7). Pipe the secret in
(`type secret.txt | captr auth set-secret sp-archive`) or let the masked prompt
ask. Secrets live in Windows Credential Manager, DPAPI-bound to your account and
this machine; `status` only ever confirms that one is stored.
