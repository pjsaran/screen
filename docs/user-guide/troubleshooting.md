# Troubleshooting

## Start here: the Diagnostics page

**Diagnostics** runs a set of checks every time you open it and tells you, in plain
English, what is true right now:

| Check | Catches |
|---|---|
| Settings | A settings file that will not load, or will not validate — and what Captr did about it (restored the previous version, or kept a file from a newer Captr aside). |
| FFmpeg | A missing encoder, or one that is not exactly the build Captr ships — Captr cannot record without it. |
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

### A scheduled recording never started, or older recordings are black

A task set to "Run whether user is logged on or not" runs in session 0, where there
is no desktop to capture. Captr now refuses to record there — the task's Last Run
Result shows `(0x1)` and `captr start` says "Captr cannot record here: it is
running in session 0 …". Set the task to "Run only when user is logged on". See
[Scheduled recording](scheduling.md). Black recordings made that way by an older
Captr are that same mistake; nothing can recover the picture.

### Recording will not start

Diagnostics names the reason. The usual ones:

- **Every display is excluded.** Tick at least one in Settings → Displays.
- **Less than 30 minutes of disk.** Captr refuses to start a recording it can
  predict will run out. Free space, or pick a working folder on a bigger drive.
- **Settings are invalid.** The Settings page shows which field and why.
- **FFmpeg is missing or has been changed.** Captr runs only the FFmpeg it was
  installed with, from its own `ffmpeg` folder, and checks it before every use.
  "ffmpeg.exe was not found" usually means antivirus software quarantined it; "is
  not the FFmpeg build Captr ships" means the file was altered or damaged. Either
  way, reinstall Captr (and, for antivirus, ask for Captr's folder to be allowed).
- **Captr is running in session 0.** See above.

A refused start never leaves an empty recording behind.

### Messages about "the recorder"

The window and `captr` are views of a separate background recorder. When they
cannot talk to it, they say what that means and what to do:

| Message begins | What it means | What to do |
|---|---|---|
| "Nothing is recording right now." | No recorder is running, so there is nothing to pause or stop. | Nothing. |
| "A Captr recorder from a different version is still running…" | Usually a recorder from before an update, still finishing its work. | Quit Captr from the tray icon, wait a minute, and open it again. |
| "Captr lost contact with its recorder part-way through…" | The connection dropped mid-request. A recording in progress is not affected. | Try again. If it keeps happening, open Diagnostics and export a support bundle. |
| "Captr's recorder (Captr.App.exe) is missing…" | The installation is damaged. | Reinstall Captr. |
| "Captr's recorder was started but did not answer within 60 seconds…" / "…did not answer '…' within … minutes" | The recorder is stuck or very busy. It may still be working. | Check `captr status`; if it stays stuck, open Diagnostics (or run `captr doctor`) and export a support bundle. The message names the log folder. |
| "Captr's command pipe exists but does not belong to you…" | Another account or program has taken the name Captr uses to reach its recorder, and Captr refused to use it. | Sign out and back in; if it persists, ask your administrator to find the process that owns it. |

`captr` keeps its [exit codes](command-line.md#exit-codes) for these: `3` when the
recorder cannot be reached or does not answer in time, `1` for the rest.

### Windows says it blocked part of Captr (Smart App Control)

Smart App Control, and application control policies like it, block program files
that carry no valid signature. Every file in a signed Captr release is signed —
Captr's own, the bundled FFmpeg, and the third-party libraries — so a release is not
blocked. A **development build** (built from source without a certificate) is
unsigned, and on a PC with Smart App Control on, Windows may refuse to load it.
Install a signed release instead.

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

### The recording stopped on its own, and nobody stopped it

Captr never stops quietly. Home shows "Recording stopped" with the reason until you
press Dismiss, the tray icon shows the error icon, and `captr status` says "The last
recording stopped on its own: …" (see
[Recording](recording.md#when-a-recording-stops-on-its-own)). Whatever ended it was
also written down, in this order of likelihood:

1. **The disk ran low.** Captr stops cleanly with about five minutes of space left,
   because a clean stop beats a disk-full crash. The footage up to that point is
   finalised and complete. Look in the session's `journal.ndjson` for a note saying
   "Disk critically low". If there was not even room to assemble the finished file,
   Captr says so, keeps every segment as it is, and finishes the file once space is
   freed — the next time it starts, or at once with `captr recover`.
2. **Windows shut down, restarted, or logged you off.** Captr holds the shutdown
   until the recording is closed properly, then lets it continue. On an **AWS
   WorkSpace set to AutoStop**, disconnecting your client eventually stops the whole
   machine — that looks exactly like this, and the recording will have been finalised
   on the way down.
3. **The encoder failed repeatedly.** Only this one loses anything, and only the tail.
   The journal carries the reason and FFmpeg's own error lines.

A locked screen, a UAC prompt, or a closed remote-desktop window is **not** a reason
to stop. Capture pauses while the desktop is away and picks up by itself when it
comes back; the time in between shows as a gap, and the recording keeps running for
as long as you asked it to.

To see which of the three happened, open the recording's folder (**Recordings → Open
folder**) and read the last few lines of `journal.ndjson`, or send a support bundle —
it contains the same answer.

### The screen locked and my recording has a hole in it

Expected, and reported rather than hidden. While the machine is locked, Windows
shows the secure logon desktop and there is genuinely nothing for Captr to capture.
Recording keeps running, the locked time is recorded as a gap, and capture resumes
by itself the moment you unlock.

Captr already stops the machine sleeping and stops the display powering off for the
whole recording. It does **not** stop the lock. The lock runs off a different timer —
how long since a real keystroke or mouse move — which nothing Captr can ask Windows
for affects, and the only way to defeat it is to fake keyboard activity, which
overrides a security setting your organisation may have deliberately set.

**Diagnostics tells you in advance.** The **Unattended recording** check reads this
machine's actual settings and says what will happen and after how long, so you find
out before a three-hour recording rather than after. The same sentence is written
into the recording's journal when it starts.

To change it: **Settings > Personalisation > Lock screen > Screen saver**. If the
timeout is set by policy, it is greyed out and only whoever manages the machine can
change it.

One case is worth knowing about because it is *silent*: a screen saver that does
**not** lock the machine stays on the ordinary desktop, so capture keeps working
perfectly and records the screen saver. No gap, no warning — just the screen saver
instead of your screen. Diagnostics calls this one out separately.

### Coverage says less than 100%

Something interrupted the capture, and Captr is telling you rather than hiding it.
The recording's `integrity.json` records exactly where and for how long. Common
causes: the machine slept, the screen was locked or the remote session was
disconnected so there was nothing to capture, a display was unplugged, the encoder
stalled and was restarted, or you paused and forgot. Each gap in `integrity.json`
carries its own reason.

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
reaches for that automatically if the settings file goes missing or cannot be used —
not valid JSON, or JSON of the wrong shape (a word where a number belongs, a
destination kind Captr does not know). Diagnostics says so when it happens, and an
unusable file is kept as `settings.json.corrupt` so it can be looked at. A setting
written as `null` simply takes its default.

A settings file written by a **newer** Captr — after going back to an older version
— is not thrown away either. It is kept, untouched, as
`settings.json.from-schema-<N>` (a name no save ever writes), Captr uses defaults,
and Diagnostics says where the file is. Install the newer Captr again and copy that
file back over `settings.json` to get everything back.

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

If only Captr's background recorder died, FFmpeg keeps recording until the next
recorder starts; that one asks FFmpeg to finish cleanly, as Stop would, and kills it
only if it does not. The report counts only footage that actually survived: time the
journal says was recorded but that no segment holds is shown as a "footage lost"
gap, never as recorded.

### "Captr's window hit an unexpected problem"

The window met an error nothing else handled. It tells you (at most once a minute),
saves the details, and carries on; any recording continues, because it runs
separately. If something on screen looks wrong afterwards, quit Captr from the tray
and open it again.

Every Captr program — the window, the recorder, and `captr` — writes such a **crash
report** to the logs folder instead of just disappearing:
`crash-<ui|host|cli>-<UTC time>.txt`, with the version, the machine, and the error,
secrets masked. Only the ten newest are kept, and nothing is sent anywhere. The
support bundle includes them. `captr` also keeps its last unexpected error in
`cli-last-error.txt` in the same folder.

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

**Diagnostics → Export support bundle** writes `captr-support-<date>-<time>.zip` to
your Desktop. It contains:

- the logs — the window's and the recorder's, **including the one being written
  right now**, crash reports, and `cli-last-error.txt` if there is one;
- for every recording in the working folder: its journal, integrity record,
  heartbeat, FFmpeg's own report and progress files;
- basic system information (Windows version, processor count, Captr and .NET
  versions);
- a `README.txt` listing every file in the bundle, and any that could not be read.

**It contains no video and no secrets.** That is enforced in code and proven by a
test that plants a secret and then searches the bundle for it. Lines naming a
password, secret, token, or key are masked after the name, and sign-in tokens are
masked wherever they appear.

**It leaves out who and where.** Your Windows user name, PC name, and profile
folder, and — from your settings — each SharePoint destination's tenant id, client
id, and site host, and the server name of any network folder, are replaced with
placeholders such as `<user>`, `<pc>`, `%USERPROFILE%`, `<tenant-id>`,
`<client-id>`, `<sharepoint-host>`, and `<server>`. The window says what the bundle
holds when it is made; open it and check before you send it.

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
| Crash reports | `%LOCALAPPDATA%\Captr\logs\crash-*.txt` |
| Stored secrets | Windows Credential Manager, under `Captr/` |

Everything is under one folder on purpose: a backup, a support bundle, or a
clean-up is one place rather than several. (If the `CAPTR_DATA_ROOT` environment
variable is set — Captr's own tests use it — everything in this table except the
stored secrets is under that folder instead, and that copy of Captr has a recorder
of its own. It is not set in a normal installation.)
