# Captr.App

The single Windows executable, serving two roles (SPEC §4):

- `Captr.exe` — the WPF desktop UI described below.
- `Captr.exe --host` — the headless recording host (`HostEntryPoint` →
  `Captr.Core.Hosting`). See the warning in `HostEntryPoint.cs`; never a service.

The UI is a **view** (SPEC §4): it holds no recording state, and can be closed,
killed, or relaunched mid-recording with no effect on capture. Concretely:

- `Services/HostConnection` polls the host's status over IPC once a second and
  exposes it as observable state. If the host is gone, the state is "idle" —
  reattaching after a UI restart is just the next poll. (The spec phrases this as
  the host "pushing" state; a 1 Hz poll over the same pipe delivers the identical
  UI behaviour with fewer moving parts, and keeps the UI stateless by
  construction.)
- `Services/ThumbnailService` produces the live display previews with an
  in-process one-shot DXGI duplication — the capture path the spec permits for
  preview ONLY, allowed to fail without consequence. Never GDI.
- Pages (`Views/` + `ViewModels/`, MVVM via CommunityToolkit): Status,
  Recordings, Delivery, Settings, Diagnostics.
- `TrayIcon` keeps state unmistakable (idle/recording/paused/error icons +
  tooltip); closing the window hides here; quitting while recording asks first
  and defaults to keep recording (SPEC §9).
- `HotkeyManager` registers the global start/pause/stop combinations and reports
  conflicts in plain language.
