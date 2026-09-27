# Getting started

## Your first recording

Until you have made a recording, Home shows a short welcome: where recordings will
be saved (with **Open folder**), and a **Record a test** button. The test records
for ten seconds, labelled `test`, and then says how it went — its length, which
encoder did the work, and its size, or the reason it failed. It is the quickest proof
that capture, the encoder, and saving all work on this PC. The test recording is on
the **Recordings** page afterwards; delete it there once you have looked.

Then, for real:

1. On **Home**, press **Start recording**.
2. Do whatever you wanted to record.
3. Press **Stop**.

That is the whole thing. The recording is written to
`%LOCALAPPDATA%\Captr\Sessions` and appears on the **Recordings** page a moment
later, where **Play** opens it.

The very first start on a PC takes a few seconds longer: Captr is checking which
encoder works best on this hardware, and Home says so while it does.

You can also do all of that without touching the window — see
[hotkeys](#hotkeys) below and the [command line](command-line.md).

## The window

Six sections down the left. The strip at the bottom of the rail always shows what
the recorder is doing, whichever page you are on — starting, recording, paused,
suspended while the PC sleeps, finalising, idle, or stopped and needing attention.

### HOME

What is happening right now, and the buttons to change it.

- The state and, while recording, the elapsed clock.
- **Coverage** — "Continuous" while nothing has been missed. If anything ever
  interrupts the capture, this becomes a percentage and a gap count, immediately.
  Captr will never tell you a recording is continuous when it is not.
- **Encoder** — which encoder is actually running, and whether it is your graphics
  card or the CPU.
- **Capture** — the frame rate, preset, and quality in force.
- **Disk** — how much longer you could keep recording, measured at the rate this
  machine is actually writing.
- A live thumbnail of every display, so you can see what is being captured before
  you start rather than after you finish. A display that could not be captured for
  a moment (a screen mode change, a UAC prompt) is tried again on the next refresh.
- One line about transfers, when there is something to say — for example "2
  transfers on their way · 1 transfer needs attention — see Transfers" — so a
  transfer waiting for a new password or refused by SharePoint is visible without
  opening the Transfers page.
- If a recording stopped on its own, the reason and a **Dismiss** button. See
  [Recording](recording.md#when-a-recording-stops-on-its-own).

### RECORDINGS

Every past recording: when it was made, how long, how big. **Play** opens it,
**Open** shows it in File Explorer, and **…** offers "Send to destinations again"
and "Delete recording".

Deleting asks you to type the recording's name. That is deliberate: it is the one
action in Captr that destroys footage. A recording that is still being written
cannot be deleted, and a file that another program is holding open is reported on
the row rather than silently left behind.

### TRANSFERS

Only interesting if you have set up a [destination](destinations-and-transfers.md).
One line per copy, with where it is going, how far it has got, and — when something
failed — the destination's own error message next to a plain explanation of what to
do about it.

### SETTINGS

Everything you can change. See [Recording](recording.md) and
[Destinations and transfers](destinations-and-transfers.md).

### DIAGNOSTICS

Health checks, where Captr keeps its files, recent warnings, and the support bundle.
See [Troubleshooting](troubleshooting.md).

### HELP

The naming tokens with examples, and where to look first when something goes wrong.

## The tray icon

Captr sits in the notification area whenever it is open. The icon is the same shape
in every state — only the screen inside it changes — so you can read it without
looking directly at it:

| Icon | Meaning |
|---|---|
| Pale screen | Idle. Nothing is being recorded. |
| Red screen, **pulsing** bright and dim | Recording. |
| Red screen, steady | Starting, or finishing: stopping and finalising the file. |
| Amber screen with two bars | Paused, or suspended while the PC sleeps. |
| Red screen with an exclamation mark | The recording stopped on its own. Open Captr to see why. |

The icon pulses only while a recording is actually running. A 16-pixel icon that is
often tucked into the overflow area needs movement to answer "is it recording right
now?" at a glance; only the screen inside the icon dims, so it reads as a pulse
rather than a flicker, and it holds still the moment the recording is not running.
See [Knowing it is recording](recording.md#knowing-it-is-recording).

When a recording stops on its own, a notification appears once, and the exclamation
icon stays until you dismiss the failure on Home or start another recording.

Hover for elapsed time, coverage, and remaining disk. Right-click for start, pause,
resume, stop, and quit. Double-click to bring the window back.

## Hotkeys

Two, both working anywhere in Windows:

| Default | Does |
|---|---|
| **Ctrl + Alt + F9** | Starts a recording when idle, stops it when one is running. |
| **Ctrl + Alt + F10** | Pauses a running recording, resumes a paused one. Does nothing when idle. |

They are *toggles* on purpose. A hotkey is something you press without looking at
the screen, so "start" and "stop" being the same key means it always does the
obvious thing.

To change one: **Settings → Hotkeys**, click the box, and press the combination you
want — it is captured as you press it. Backspace or Delete clears it, which disables
that hotkey. Tab, Shift+Tab, and Escape move on to the next control as they do
everywhere else, so a keyboard user is never trapped in the box.

The rules:

- A combination needs **Ctrl, Alt, or Win**. Shift on its own is not enough —
  Shift+A is a capital A, and would stop you typing one in every other program. The
  exception is a function key: **F1 to F24** work alone.
- The two hotkeys must be different.

A combination that breaks a rule is not accepted; the box keeps the hotkey it had
and says why. Hotkeys take effect **as soon as you save** the Settings page — no
restart. If another application already owns a combination, the Settings page says
so beside the boxes, naming the combination, and Captr also tells you when it
starts. A hotkey setting saved by an older Captr that breaks these rules never
stops a recording; it is refused only when the settings are next saved.

## Closing, minimising, and quitting

- **Closing the window** hides Captr in the notification area. A recording carries
  on. (Turn this off in Settings → Window if you would rather it exit.)
- **Hide the window while recording** (Settings → Window) makes Captr disappear the
  moment recording starts and come back when it ends — useful, because the
  recorder's own window is usually the last thing you want in the recording.
- **Quit** from the tray menu exits properly. If a recording is in progress it
  asks first, and defaults to carrying on.

**Nothing you do to the window stops a recording.** The recorder is a separate
background process; the window is a view of it. Even ending the window in Task
Manager leaves the recording running, and reopening Captr reattaches to it.
