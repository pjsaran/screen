# The `captr` command line

Everything the window can do is available here, so Captr can be driven by Task
Scheduler, a build server, or a script.

Results go to **stdout**, errors to **stderr**. Commands that report something
accept `--json` for machine-readable output — see [which ones](#machine-readable-output).

If `captr` is not found, either the "Add Captr to my PATH" option was unticked at
install time, or the terminal was already open when Captr was installed. Open a new
one.

## Exit codes

These are a contract. Scripts may branch on them.

| Code | Meaning |
|---|---|
| `0` | Success — including `start` while already recording and `stop` while idle. |
| `1` | The command failed. Details on stderr. |
| `2` | Bad arguments or usage: an unknown command, a missing argument, or an option value that is not allowed (`--fps 7`, `--quality sublime`, a transfer id that is not a number). The problem and the valid values are on stderr. |
| `3` | No recorder answered: none was running and none could be started, or the recorder did not answer a request in time (see [time limits](#time-limits)). |
| `10` | `status` only: idle, nothing is recording. |
| `11` | `status` only: a recording exists but is paused. |
| `130` | Interrupted with Ctrl+C (the conventional 128 + SIGINT). |

Ctrl+C ends the `captr` command, never the recorder: whatever the recorder was asked
to do, it carries on, so check with `captr status`. Treat any code not in this table
as a failure.

When something unexpected goes wrong, `captr` prints one line saying what failed and
exits `1` (or `3` if the recorder could not be reached). It never prints a stack
trace. The full details, with secrets masked, are kept in
`%LOCALAPPDATA%\Captr\logs\cli-last-error.txt`, and the message names that file.

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
double-fire can harm a recording. Starting while the previous recording is still
being written out is allowed: that one finishes in the background while the new one
records.

`start` is refused (exit `1`, with the reason) when nothing could be recorded: every
display excluded, no working encoder, not enough disk, a missing or altered FFmpeg,
or a Captr running in session 0 (see [Scheduled recording](scheduling.md)). A
refused start leaves no empty recording behind.

The overrides apply to this session only; your saved settings are untouched. A value
not in these lists is a usage error (exit `2`):

- `--fps` — `5`, `10`, `15`, `20`, `24`, `30`, `45`, `60`
- `--quality` — `lossless`, `maximum`, `high`, `balanced`, `compact`
- `--preset` — `ultrafast`, `superfast`, `veryfast`, `faster`, `fast`
- `--label` — free text, available to naming patterns as `{label}`

`stop` returns as soon as the recording has ended; finalising and transferring
continue in the background. Watch them with `captr status` and
`captr transfers list`. `stop`, `pause`, and `resume` with no recorder running say
so and exit `0`. `pause` and `resume` act only on a recording that is actually
running or paused.

`status` exits `0` while recording (or starting, finalising, or suspended while the
PC sleeps), `10` when idle, and `11` when paused. When the last recording **stopped
on its own** — repeated encoder failures, a nearly full disk — the idle message says
so and why:

```
Idle — nothing is recording. The last recording stopped on its own: <the reason>
```

The exit code is still `10`. With `--json`, the same information is in
`lastOutcome`: the session id, `result` (`completed` when it was stopped by a
person, a schedule, or Windows shutting down; `failed` when it stopped itself;
`faulted` for an internal error, whose footage is recovered on the next start), the
`reason`, when it ended, and its folder. The recorder exits a couple of minutes
after its last recording, and `lastOutcome` goes with it.

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
  problems if anything has, or if the folder is not a finished recording.
- `resend` sends this recording to every enabled destination that does not already
  have it. A destination it is already on its way to is left alone, and a transfer
  to it that was stopped, gave up, or is waiting for sign-in is put back in the queue
  rather than duplicated. Exits `1` when nothing was queued — every destination
  already has it, there are no enabled destinations, or the folder is not a finished
  recording.

A relative `<session-folder>` means relative to where you typed the command, for
`verify` and `resend` alike.

## Recovery

```
captr recover [--json]
```

Finalises any recording interrupted by a crash or power cut, and reports what was
recovered and what was lost. The recorder does this automatically every time it
starts; this command is for doing it now, on demand. If the recorder is still in its
own start-up recovery, `recover` waits for it and then finds nothing left to do. A
recording that is still being made is never touched. Exits `1` if any recording
could not be recovered.

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

`retry` and `stop` exit `1` when no transfer has that id.

The queue holds one row per (recording, destination), so `retry <id>` re-sends to
exactly one destination.

## Settings

```
captr settings get
captr settings set <key> <value>
captr settings init [--working-folder PATH] [--json]
captr settings export > captr-settings.json
captr settings import captr-settings.json
```

`get` prints exactly what is in `settings.json`, as JSON.

`init` creates `settings.json` with defaults **only if it does not exist**. It never
overwrites, so it is safe to run repeatedly — the installer runs it on every install,
which is how a fresh machine gets working settings while an upgrade keeps yours. It
loads the settings first, so an older Captr's settings are moved across and a missing
file is restored from its previous version (`settings.json.bak`) rather than
replaced by defaults.

Keys:

| Key | Example |
|---|---|
| `framerate` | `15` |
| `preset` | `veryfast` |
| `quality` | `balanced` |
| `workingFolder` | `D:\Recordings` |
| `outputPattern` | `{machine} {date:yyyy_MM_dd} {start:HH_mm_ss}` |
| `retentionDays` | `14` (at most `3650`) |
| `maxAttempts` | `5` |
| `firstRetrySeconds` | `30` |
| `maxRetrySeconds` | `1800` |
| `startMinimised` | `true` |
| `closeToTray` | `true` |
| `minimiseWhileRecording` | `false` |
| `recordToggleHotkey` | `Ctrl+Alt+F9` |
| `pauseToggleHotkey` | `Ctrl+Alt+F10` |
| `excludedDisplayIds` | semicolon-separated stable ids |

Every change is validated before anything is written; an invalid value, or a key
that does not exist, is refused with a message naming the field and the problem
(exit `1`).

**Destinations are edited in the window**, not with `settings set` — a destination
is several fields that have to be consistent with each other. Use
`settings export` / `settings import` to move a configuration between machines.

An export **never contains credentials**, and says so in the file. Importing leaves
stored credentials alone.

`import` first prints what the imported file changes about **where recordings go** —
one line each for a moved working folder and for every destination added, changed,
or removed — and then saves it exactly as `set` would, including the rules below
when a recording is in progress. A file that is not a Captr settings file is refused
(exit `1`).

### While a recording is in progress

`set` and `import` go through the running recorder. Capture and quality settings
accept only changes that ask for **less** work — a lower frame rate, a lower
quality, a faster preset, removing a display. Each is applied to the live recording,
rolls a new segment, and is journaled. Anything asking for more is refused with a
message telling you to stop first. Settings that do not touch the encoder change
freely.

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

or let the masked prompt ask. A piped secret is read up to 4096 characters, with its
trailing line break removed; anything longer is refused and nothing is stored (exit
`1`), because no client secret is that long and it means the wrong thing was piped.
With no console to type into and nothing piped — a scheduled task, for example —
`set-secret` says to pipe the secret instead and stores nothing (exit `1`).

Secrets live in Windows Credential Manager, bound by DPAPI to your account **and**
this machine, so a copied blob is useless elsewhere. `status` only ever confirms that
one is stored (exit `0`, or `1` when none is); nothing reads a secret back out.

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

## Time limits

No command waits for the recorder for ever. Each request has a generous limit:

| Request | Limit |
|---|---|
| `start`, `stop` | 10 minutes |
| `recover` | 1 hour |
| everything else | 2 minutes |

A command that started the recorder itself also gives it 60 seconds to answer at
all. When a limit passes, the command exits `3` and says what to do: the recorder may
still be working, so check with `captr status`, and if it stays stuck, open
Diagnostics (or run `captr doctor`) and export a support bundle. `settings set` and
`settings import` report the same problem with exit `1`, because nothing was saved.

This is what stops a stuck recorder from blocking a scheduled task for good.

## Machine-readable output

`--json` is accepted by `start`, `stop`, `pause`, `resume`, `status`,
`recordings list`, `recordings verify`, `recordings resend`, `recover`,
`transfers list`, `transfers retry`, `transfers stop`, `settings init`, `doctor`, and
`version`. With it, stdout carries one JSON document and nothing else.

`settings get` and `settings export` always print JSON. `settings set`,
`settings import`, and the `auth` commands print plain sentences and do not take
`--json`.
