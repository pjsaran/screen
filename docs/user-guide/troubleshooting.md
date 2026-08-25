# Troubleshooting

## Start here: the Diagnostics page

**Diagnostics** runs a set of checks every time you open it and tells you, in plain
English, what is true right now:

| Check | Catches |
|---|---|
| Settings | A settings file that will not load, or will not validate. |
| FFmpeg | A missing or damaged encoder — Captr cannot record without it. |
| Displays | No displays, or every display excluded. |
| Encoder | Which encoder this machine proved, and whether it fell back to the CPU. |
| Disk space | How many **hours of recording** are left, not just free bytes. |
| Destinations | A SharePoint destination whose stored secret has gone missing. |
| Transfers | Anything stuck waiting for a person. |

Anything amber or red says what to do about it. The page also lists **where Captr
keeps everything** with an Open button beside each, and shows recent warnings from
the log without you having to find the log.

Most questions on this page are answered there first.

The same checks are available without opening the window:

```
captr doctor
```

It exits `1` when something would actually stop Captr working, so it can be used in
a deployment check.

## Common problems

### "captr is not recognised as a command"

Either the "Add Captr to my PATH" option was unticked during installation, or your
terminal was already open when Captr was installed. Open a new terminal. If it is
still missing, reinstall with the option ticked, or use the full path
`"C:\Program Files\Captr\captr.exe"`.

### The recording is black

Almost always a scheduled task set to "Run whether user is logged on or not". See
the warning in [Scheduled recording](scheduling.md) — it is a Windows limitation,
not something Captr can work around.

### Recording will not start

Diagnostics names the reason. The usual ones:

- **Every display is excluded.** Tick at least one in Settings → Displays.
- **Less than 30 minutes of disk.** Captr refuses to start a recording it can
  predict will run out. Free space, or pick a working folder on a bigger drive.
- **Settings are invalid.** The Settings page shows which field and why.

### The first recording takes a few seconds to start

Expected, once per machine. Captr proves which encoder actually works on your
hardware by running a short trial encode, and remembers the answer. Later starts
are immediate. It re-proves after a driver update, a change to the encoding
settings, or a change to your display layout — all of which can change the answer.

### The Encoder check says "software"

No GPU encoder passed its trial. Recording still works, but it costs noticeably
more CPU. Updating the graphics driver is the usual fix. If you have a laptop with
both an integrated and a discrete GPU, check the discrete one is enabled.

### The Encoder check mentions "compatibility (GDI) screen capture"

This machine's display driver cannot serve the GPU capture path — normal on a
virtual desktop such as **AWS WorkSpaces**, some VMs, and some remote-desktop
hosts. Captr detected that during the encoder trial and switched to reading the
screen through GDI, which works everywhere at the cost of more CPU. There is
nothing to fix; on a physical machine with a real graphics driver, the faster
path is chosen automatically.

### "No working encoder was found on this machine"

Every encoder failed its trial with *both* capture methods. Open **Diagnostics**:
the **Encoder support** check shows what the bundled FFmpeg ships, and the log
tail carries each candidate's own error. On a machine with no GPU at all the
software encoder should still pass — if it does not, the trial's error text is
what to send with a support bundle.

### Coverage says less than 100%

Something interrupted the capture, and Captr is telling you rather than hiding it.
The recording's `integrity.json` records exactly where and for how long. Common
causes: the machine slept, a display was unplugged, the encoder stalled and was
restarted, or you paused and forgot.

Everything outside the gaps is intact. Captr will never present a recording as
continuous when it is not.

### A transfer is stuck

Open **Transfers**. Every failed line shows the destination's own error message and
a plain explanation.

- **RETRYING** — it is handling itself; the line says when it will next try.
- **GAVE UP** — it ran out of automatic attempts. Fix the cause and press Retry.
  You can raise the limit in Settings → Transfers.
- **REFUSED** — permission, quota, or policy. The server's exact words are shown;
  that is the text to send to whoever administers the destination.
- **SIGN-IN NEEDED** — the SharePoint secret stopped working. Enter it again in
  Settings, then Retry.

If a destination is down and the retries are just noise, press **Stop**. It stays
in the list and starts again only when you press Retry.

### "Send to destinations again" is greyed out

It is offered only when it would do something. The tooltip says which case applies:
the recording is not finalised yet, there are no enabled destinations, or every
destination already has it.

### I reinstalled and lost my settings

A reinstall keeps them. If they went back to defaults, the uninstaller was told to
"Delete everything Captr has stored" — that choice removes `%LOCALAPPDATA%\Captr`,
which is where settings, the transfer queue, and any recordings still in the working
folder live.

`captr settings export` writes a copy you can keep anywhere; `captr settings import`
reads it back.

### My settings went back to defaults

Every save keeps the version it replaced as `settings.json.bak` beside it, and Captr
reaches for that automatically if the settings file goes missing or will not parse.
Diagnostics says so when it happens, and an unreadable file is kept as
`settings.json.corrupt` so it can be looked at.

If both are gone, the settings are gone — they are small and quick to re-enter, and
`captr settings export` makes a copy you can keep wherever you like.

### The Captr window disappeared when recording started

That is the "Hide the window while recording" setting. Captr is in the notification
area; double-click it to bring the window back. Turn it off in Settings → Window.

### Closing Captr did not stop the recording

Correct, and deliberate. The window is a viewer; the recorder is a separate
background process. Stop a recording with the Stop button, the hotkey, the tray
menu, or `captr stop`.

### A recording was interrupted by a crash or power cut

Nothing to do. The next time Captr runs, it finds the unfinalised recording,
repairs any truncated segment, assembles what reached disk, and reports what was
recovered. You can force it now with `captr recover`.

## Proving a recording has not been altered

```bat
captr recordings verify "<session folder>"
```

Every file's SHA-256 is recomputed and compared against the record written when the
recording finished. Exits `0` when everything matches, or `1` with a list of what
does not.

You do not normally need this. It exists for the case where a recording matters
enough that "is this the file that was made?" is a question someone will ask.

## Sending a support bundle

**Diagnostics → Export support bundle** writes a zip to your Desktop containing the
recordings' journals and integrity records, the application logs, the tail of the
encoder's own output, and system information.

**It contains no video and no secrets.** That is enforced in code and proven by a
test that plants a secret and then searches the bundle for it.

Include `captr version` (or the "This installation" block from Diagnostics) with
it — the version, source commit, and exact FFmpeg build are what make a problem
reproducible.

## Where everything lives

Every path is listed on the Diagnostics page with an Open button. For reference:

| | |
|---|---|
| Recordings | `%LOCALAPPDATA%\Captr\Sessions` (unless you moved it) |
| Settings | `%LOCALAPPDATA%\Captr\settings.json` (with `settings.json.bak`, the previous saved version) |
| Logs | `%LOCALAPPDATA%\Captr\logs` |
| Transfer queue | `%LOCALAPPDATA%\Captr\transfers.db` |
| Encoder cache | `%LOCALAPPDATA%\Captr\encoder-cache.json` |
| Stored secrets | Windows Credential Manager, under `Captr/` |

Everything is under one folder on purpose: a backup, a support bundle, or a
clean-up is one place rather than several.
