# WindowsEvents

The host's ear to the operating system (SPEC §6's Windows-events table). Recording
runs for hours unattended; everything Windows does in that time — sleep, lock,
remote desktop, display changes, shutdown, clock jumps — arrives here as messages
to a hidden window, and the session engine reacts.

- **MessageOnlyWindow** — a `HWND_MESSAGE` window on its own thread. It receives:
  - `WM_POWERBROADCAST` — suspend (close the segment, flush the journal) and
    resume (new segment, record the gap);
  - `WM_WTSSESSION_CHANGE` — workstation lock/unlock (note it, keep recording —
    never stop), console disconnect/reconnect (Desktop Duplication loses access;
    retry until it returns, never end silently);
  - `WM_DISPLAYCHANGE` / `WM_DEVICECHANGE` — topology changed: debounce,
    re-resolve display identities, rebuild, roll a new arrangement group;
  - `WM_QUERYENDSESSION` / `WM_ENDSESSION` — shutdown/logoff: block briefly with a
    visible reason, finalise quickly, release;
  - `WM_TIMECHANGE` — record the clock jump (timestamps are UTC everywhere).
- **ExecutionStateHolder** — holds `ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED` for
  the whole session. **Mandatory**: once the display powers off, Desktop
  Duplication returns black frames — the recording would look alive while
  capturing nothing (SPEC §6 marks this row mandatory).
- **ShutdownBlock** — registers/unregisters the visible shutdown-block reason.
- **IdleLockPolicy** — reads (never writes) what this machine does to itself when
  left alone: screen saver, and workstation lock. Feeds the **Unattended recording**
  diagnostic and a note journaled at recording start.

Events are raised on the window's message thread; subscribers must hop threads
themselves (the session engine marshals onto its own loop).

## What Captr prevents, and what it only reports

Two pairs of behaviours that look alike and are not.

| Behaviour | Driven by | Captr |
|---|---|---|
| System sleep | Power idle timer | **Prevents** — `ES_SYSTEM_REQUIRED` |
| Display power-off | Power idle timer | **Prevents** — `ES_DISPLAY_REQUIRED`, mandatory: a dark display makes Desktop Duplication return black frames |
| Screen saver | **Input** idle timer | Reports only |
| Workstation lock | **Input** idle timer, or Group Policy inactivity limit | Reports only |

The split is not an oversight. `SetThreadExecutionState` acts on the power idle
timer; the screen saver and the lock watch how long since a real keystroke or mouse
move, which it does not touch. No Windows API suppresses the Group Policy machine
inactivity lock — the only thing that defeats it is synthesising input on a timer,
which overrides a security control the machine's owner set deliberately. A screen
recorder quietly defeating a lock policy is not a trade worth making, so
`IdleLockPolicy` reports and stops there.

Note which one is dangerous. A **locking** screen saver switches to the secure
desktop: capture access is lost, and the supervisor records an honest, self-healing
gap. A **non-locking** screen saver stays on the ordinary desktop, so capture keeps
working and records the screen saver — no gap, no warning, content silently gone.
That is why `IdleLockPolicy` reports the two cases separately.
