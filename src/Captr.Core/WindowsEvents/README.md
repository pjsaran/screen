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

Events are raised on the window's message thread; subscribers must hop threads
themselves (the session engine marshals onto its own loop).
