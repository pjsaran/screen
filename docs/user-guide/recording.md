# Recording

## The three quality choices

Most recorders offer one "quality" slider. Captr offers three, because they answer
three different questions and bundling them makes the useful combinations
impossible to ask for — "cheap on CPU but a sharp picture" is exactly what a laptop
recording a terminal wants, and one slider cannot express it.

**Settings → Recording quality.**

### Framerate — how often the screen is sampled

`5`, `10`, `15` (default), `20`, `24`, `30`, `45`, `60`.

Higher costs more CPU, more GPU, and more disk, in roughly a straight line. 15 is
comfortable for anything that is mostly text and windows. Go to 30 or above for
video playback or animation.

### Preset — how much work the encoder may spend on each frame

`ultrafast`, `superfast`, `veryfast` (default), `faster`, `fast`.

This one reads backwards at first: a **faster** preset does **less** work, so it
costs less CPU and produces a **bigger** file at the same quality. A slower preset
spends more CPU to make the file smaller. It does not change how the picture looks.

### Quality — how good the picture has to be

`lossless`, `maximum`, `high`, `balanced` (default), `compact`.

This is the one that changes what you see. `balanced` keeps small text legible.
`compact` is for long recordings where the file size matters more than the last of
the sharpness. `lossless` is exactly what it says and produces very large files.

Every option in each list says what it costs, so you do not need to know what CRF
means to choose sensibly.

## Choosing displays

**Settings → Displays.** Every attached display is ticked by default; untick one to
leave it out. Multiple displays are recorded into a single file, side by side.

Displays are remembered by their actual identity, not by number, so unplugging a
monitor and plugging it into a different port does not silently change what gets
recorded. A display you plug in later is recorded by default — a new monitor is
never quietly missed.

If you untick every display, recording refuses to start and says why.

## Changing settings while recording

Captr will accept a change mid-recording **only if it asks the machine for less
work**: a lower frame rate, a lower quality, a faster preset, or removing a
display. Each one is applied to the live recording immediately, starts a new
segment, and is written into the recording's journal so the change is part of its
history.

Anything that asks for *more* — a higher frame rate, better quality, a slower
preset, adding a display back, or moving the working folder — is refused with a
message telling you to stop the recording first.

Settings that have nothing to do with the encoder — naming, retention,
destinations, hotkeys, window behaviour — change freely at any time.

This exists so that "the disk is filling up" has an answer that does not involve
losing the recording.

## Knowing it is recording
The tray icon **blinks** the whole time a recording is running — that is the signal,
and it is deliberately hard to miss. A still icon means Captr is installed and idle,
not recording.

Two things do NOT blink, on purpose:

- **Stopping and finalising** hold the bright icon steady. Captr is still working —
  joining segments and hashing them — and the recording is not finished until it
  goes idle. "Still busy" should never look like "still recording".
- **Paused** has its own icon, plus a reminder every five minutes.

If you cannot see the icon at all, Windows has probably tucked it into the
notification-area overflow: click the `^` chevron by the clock and drag the Captr
icon onto the taskbar to keep it visible.

## Pausing

Pause with the button, the tray menu, or Ctrl+Alt+F10.

A pause is recorded as a **gap**, not as missing time — the recording's coverage
report says exactly where and for how long. Captr reminds you every five minutes
while paused, because a forgotten pause is an invisible hole in a recording and the
reminder is much cheaper than discovering it later.

## Where recordings go

Each recording gets its own folder under the working folder
(`%LOCALAPPDATA%\Captr\Sessions` by default; change it in **Settings → Files**).

Inside, alongside the finished `.mkv`, you will find:

| File | What it is |
|---|---|
| `seg-*.mkv` | The segments the recording was written in, five minutes each. Kept after the finished file is assembled — they are the safety net, not rubbish. |
| `journal.ndjson` | Every event in the recording's life: started, segment rolled, display changed, paused, resumed, stopped. One line each, flushed to disk as it happens. |
| `heartbeat.json` | Rewritten once a second while recording. Its absence is how a later run knows the recording was interrupted. |
| `integrity.json` | Written at the end: a SHA-256 of every file, plus the honest coverage report. |
| `progress.txt` | FFmpeg's own progress output, written to a file rather than a pipe so it survives Captr being killed. |

Nothing here is deleted automatically unless [retention](#retention) is configured
and the recording has already been transferred somewhere.

## What happens when something goes wrong

This is the part Captr is actually built around.

| If | Then |
|---|---|
| The encoder stalls or dies | It is restarted into a new segment within seconds. The gap is recorded and shown, never hidden. |
| Captr's own background process is killed | The encoder keeps writing. The next start adopts the orphan, stops it cleanly, and finalises everything that reached disk. |
| The machine loses power | The next start finds the unfinalised recording, repairs any truncated segment, and assembles the rest. You lose the encoder's last output buffer — seconds. |
| A segment is corrupt at its tail | It is repaired by re-wrapping it; the original is kept until the repair is verified. |
| The disk gets low | You are warned in minutes-remaining. Below five minutes, Captr stops cleanly and finalises rather than being cut off. A reserved half-gigabyte is released so there is always room to finish. |
| A display is unplugged, added, or rearranged | The recording rolls to a new layout and carries on. |
| The machine sleeps | The segment is closed and flushed before suspend, and a new one starts on resume. The sleep is a recorded gap. |
| You lock the screen | Recording continues. Locking is not an interruption. |
| Windows shuts down | Shutdown is blocked briefly, with a reason on screen, long enough to finalise. |
| The GPU encoder fails repeatedly | It falls back to the CPU encoder once, and says so. It never falls back to a different capture method. |

Captr never reports a recording as continuous when it is not. If there is a gap,
you will see it on Home while it is happening and in the recording's integrity
record afterwards.

## Retention

**Settings → Files → Keep local copies for (days).**

Local files are deleted only when **all** of these are true:

1. The retention period has passed.
2. Every enabled destination has confirmed the transfer.
3. Disk space is actually short.

A recording that exists nowhere else is never deleted automatically, whatever the
retention setting says — including when you have no destinations at all. Deleting
those is your decision, from the Recordings page, with the typed confirmation.
