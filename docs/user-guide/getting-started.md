# Getting started

## Your first recording

1. Open Captr from the Start Menu.
2. On **Home**, press **Start recording**.
3. Do whatever you wanted to record.
4. Press **Stop**.

That is the whole thing. The recording is written to
`%LOCALAPPDATA%\Captr\Sessions` and appears on the **Recordings** page a moment
later, where **Play** opens it.

You can also do all of that without touching the window — see
[hotkeys](#hotkeys) below and the [command line](command-line.md).

## The window

Five sections down the left. The strip at the bottom of the rail always shows what
the recorder is doing, whichever page you are on.

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
  you start rather than after you finish.

### RECORDINGS

Every past recording: when it was made, how long, how big. **Play** opens it,
**Open** shows it in File Explorer, and **…** offers "Send to destinations again"
and "Delete recording".

Deleting asks you to type the recording's name. That is deliberate: it is the one
action in Captr that destroys footage.

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

## The tray icon

Captr sits in the notification area whenever it is open. The icon is the same shape
in every state — only the screen inside it changes — so you can read it without
looking directly at it:

| Icon | Meaning |
|---|---|
| Pale screen | Idle. Nothing is being recorded. |
| Red screen | Recording. |
| Amber screen with two bars | Paused. |
| Red screen with an exclamation mark | Stopped after repeated failures. Open Captr. |

Hover for elapsed time, coverage, and remaining disk. Right-click for start, pause,
resume, stop, and quit. Double-click to bring the window back.

The icon does not flash. A flashing tray icon reads as an alarm, and there is
nothing alarming about a recording that is going fine.

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
want — it is captured as you press it. Backspace clears it, which disables that
hotkey. A combination needs at least one of Ctrl, Alt, Shift, or Win; a bare letter
would fire while you were typing.

If another application has already claimed a combination, Captr says so by name at
startup rather than quietly doing nothing.

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
