# The `captr` command line

Everything the window can do is available here, so Captr can be driven by Task
Scheduler, a build server, or a script.

Results go to **stdout**, errors to **stderr**, and every command accepts `--json`
for machine-readable output.

If `captr` is not found, either the "Add Captr to my PATH" option was unticked at
install time, or the terminal was already open when Captr was installed. Open a new
one.

## Exit codes

These are a contract. Scripts may branch on them.

| Code | Meaning |
|---|---|
| `0` | Success — including `start` while already recording and `stop` while idle. |
| `1` | The command failed. Details on stderr. |
| `2` | Bad arguments or usage. |
| `3` | No recording host answered, and none could be started. |
| `10` | `status` only: idle, nothing is recording. |
| `11` | `status` only: a recording exists but is paused. |

## Recording

```
captr start [--fps N] [--quality LEVEL] [--preset SPEED] [--label TEXT] [--json]
captr stop   [--json]
captr pause  [--json]
captr resume [--json]
captr status [--json]
```

`start` launches the background recorder if it is not already running, validates the
settings, resolves the displays, proves an encoder, checks there is enough disk, and
begins. Starting while already recording is a **success** that reports the existing
session — schedulers fire twice more often than anyone expects, and neither
double-fire can harm a recording.

The overrides apply to this session only; your saved settings are untouched:

- `--fps` — `5`, `10`, `15`, `20`, `24`, `30`, `45`, `60`
- `--quality` — `lossless`, `maximum`, `high`, `balanced`, `compact`
- `--preset` — `ultrafast`, `superfast`, `veryfast`, `faster`, `fast`
- `--label` — free text, available to naming patterns as `{label}`

`stop` returns as soon as the recording has ended; finalising and transferring
continue in the background. Watch them with `captr status` and
`captr transfers list`.

> The first recording on a machine spends a few seconds proving which encoder works
> best on this hardware. Every later start with the same GPU, driver, layout, and
> settings reads the cached answer and begins immediately.

## Recordings

```
captr recordings list [--json]
captr recordings verify <session-folder> [--json]
captr recordings resend <session-folder> [--json]
```

- `list` reads the working folder directly. It answers on a machine where nothing
  is running, and never starts anything.
- `verify` recomputes the SHA-256 of every file against the recording's integrity
  record, proving nothing has been altered on disk. Exits `1` with a list of
  problems if anything has.
- `resend` queues this recording to every enabled destination that does not already
  have it.

## Recovery

```
captr recover [--json]
```

Finalises any recording interrupted by a crash or power cut, and reports what was
recovered and what was lost. The recorder does this automatically every time it
starts; this command is for doing it now, on demand.

## Transfers

```
captr transfers list [--json]
captr transfers retry <id> [--json]
captr transfers stop  <id> [--json]
```

`list` shows every transfer with its attempt count and the destination server's
verbatim error. `retry` puts a stopped one back in the queue and resets its attempt
count. `stop` halts one that is queued, running, or backing off — it stays in the
list and starts again only when you retry it. Nothing local is ever deleted.

The queue holds one row per (recording, destination), so `retry <id>` re-sends to
exactly one destination.

## Settings

```
captr settings get
captr settings set <key> <value>
captr settings init [--working-folder PATH]
captr settings export > captr-settings.json
captr settings import captr-settings.json
```

`get` prints exactly what is in `settings.json`.

`init` creates `settings.json` with defaults **only if it does not exist**. It never
overwrites, so it is safe to run repeatedly — the installer runs it on every install,
which is how a fresh machine gets working settings while an upgrade keeps yours.

Keys:

| Key | Example |
|---|---|
| `framerate` | `15` |
| `preset` | `veryfast` |
| `quality` | `balanced` |
| `workingFolder` | `D:\Recordings` |
| `outputPattern` | `{machine} {date:yyyy_MM_dd} {start:HH_mm_ss}` |
| `retentionDays` | `14` |
| `maxAttempts` | `5` |
| `firstRetrySeconds` | `30` |
| `maxRetrySeconds` | `1800` |
| `startMinimised` | `true` |
| `closeToTray` | `true` |
| `minimiseWhileRecording` | `false` |
| `recordToggleHotkey` | `Ctrl+Alt+F9` |
| `pauseToggleHotkey` | `Ctrl+Alt+F10` |
| `excludedDisplayIds` | semicolon-separated stable ids |

Every change is validated before anything is written; an invalid value is refused
with a message naming the field and the problem.

**Destinations are edited in the window**, not with `settings set` — a destination
is several fields that have to be consistent with each other. Use
`settings export` / `settings import` to move a configuration between machines.

An export **never contains credentials**, and says so in the file. Importing leaves
stored credentials alone.

### While a recording is in progress

Capture and quality settings accept only changes that ask for **less** work — a
lower frame rate, a lower quality, a faster preset, removing a display. Each is
applied to the live recording, rolls a new segment, and is journaled. Anything
asking for more is refused with a message telling you to stop first. Settings that
do not touch the encoder change freely.

## Credentials

```
captr auth set-secret <name>      reads stdin when piped, otherwise prompts
captr auth status <name>
captr auth delete <name>
```

For SharePoint destinations the window collects the secret for you and stores it
under a name derived from the destination — `auth set-secret` is only needed for
scripted or headless setup.

**A secret is never accepted as a command-line argument.** Argument lists are
visible to every process on the machine. Pipe it in:

```bat
type secret.txt | captr auth set-secret "Captr:sp-archive"
```

or let the masked prompt ask. Secrets live in Windows Credential Manager, bound by
DPAPI to your account **and** this machine, so a copied blob is useless elsewhere.
`status` only ever confirms that one is stored; nothing reads a secret back out.

## Checking the installation

```
captr doctor [--json]
```

Runs the same health checks the Diagnostics page shows — settings, FFmpeg, displays,
the proven encoder, disk measured in hours of recording, destinations and their
stored secrets, and anything stuck in the transfer queue — and prints each with what
to do about it.

Exits `1` if any check is a **problem**, so a deployment script can ask "is this
machine ready to record?" and branch on the answer:

```bat
captr doctor
if %ERRORLEVEL% NEQ 0 echo Captr is not ready on this machine
```

Attention-level findings do not fail the command: they are worth reading, not worth
stopping for.

## Build identity

```
captr version [--json]
```

The application version, the source commit, the exact bundled FFmpeg build, and the
.NET runtime — the four facts a support conversation starts from. The same block is
on the Diagnostics page.
