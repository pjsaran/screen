# Captr.App

The single Windows executable, serving two roles (SPEC §4):

- `Captr.App.exe` — the WPF desktop window described below.
- `Captr.App.exe --host` — the headless recording host (`HostEntryPoint` →
  `Captr.Core.Hosting`). See the warning in `HostEntryPoint.cs`; never a service.

The window is a **view** (SPEC §4): it holds no recording state, and can be closed,
killed, or relaunched mid-recording with no effect on capture.

## The pieces

- `Services/HostConnection` polls the host's status over IPC once a second and
  exposes it as observable state. If the host is gone, the state is "idle" —
  reattaching after a UI restart is just the next poll. (The spec phrases this as
  the host "pushing" state; a 1 Hz poll over the same pipe produces the identical UI
  behaviour with fewer moving parts, and keeps the UI stateless by construction.)
- `Services/DisplayPreviewService` produces the live display previews with
  in-process DXGI duplication — the capture path the spec permits for preview ONLY,
  allowed to fail without consequence. Never GDI. Devices are created once per
  output and reused, and released as soon as the Home page stops being watched.
- `Services/TrayPresenter` owns everything about the notification area: which of the
  four `.ico` files is showing, and the tooltip. It does **not** animate — see
  below.
- `Services/HotkeyManager` registers the two global toggle combinations and reports
  conflicts in plain language.
- `Theme/Styles.xaml` is the design system — the type scale, spacing, semantic
  colours, cards, table rows, status chips, the navigation rail. Every page draws
  from it; no page invents its own sizes, and no foreground colour is hard-coded
  (they are WPF-UI theme resources, so the whole UI follows the system light/dark
  theme and accent).
- `Theme/Typo.cs` provides letter spacing for uppercase micro-labels, which WPF has
  no property for. Bind `theme:Typo.Text` instead of `Text` on those labels.
- Pages (`Views/` + `ViewModels/`, MVVM via CommunityToolkit): Home, Recordings,
  Transfers, Settings, Diagnostics.

## Pages

| Page | What it is for |
|---|---|
| **Home** | What the recorder is doing, the transport buttons, four live facts (coverage, encoder, capture, disk), and a preview of each captured display. |
| **Recordings** | Every past session: play, open the folder, send to destinations again, delete with a typed confirmation. The list refreshes itself. |
| **Transfers** | Every transfer as a table row — file, destination, size, when it was queued, state, and one action — with a progress bar while sending and the server's verbatim error when it fails. |
| **Settings** | The three encoding choices, display selection, destinations, file naming and retention, retry policy, hotkeys, window behaviour. |
| **Diagnostics** | Health checks, where Captr keeps its files, recent warnings, the build identity, and the support-bundle export. |

## Things worth knowing before editing

- **Pages release what they hold.** A page that owns a timer, a capture device, or a
  file watcher implements `IPageLifecycle`; the shell calls `OnEntering`/`OnLeaving`
  as the user navigates. Without it every page's timer would run for the life of the
  process — the display previews alone would keep a GPU duplication alive forever.

- **Event subscriptions are unsubscribed.** `MainWindow` and `HomeViewModel` both
  attach to `HostConnection.StatusChanged` and both detach on dispose. A handler that
  outlives its subscriber is the classic WPF leak, and "the process exits anyway" is
  not a reason to leave one.

- **Reading does not start a host.** The recordings list scans the working folder
  directly (`RecordingCatalog`) and the transfers list reads the queue database
  directly. Opening a page must never spawn a recording host just to show a list.

- **A ContextMenu is in its own visual tree.** A
  `{Binding DataContext.X, RelativeSource={RelativeSource AncestorType=ItemsControl}}`
  inside one finds no ancestor, silently resolves to null, and produces a menu item
  that looks enabled and does nothing. Use a `Click` handler on the page instead.
  (This is what made "Send to destinations again" appear broken.)

- **Every page opens with `PageHeader`, outside the ScrollViewer.** It draws the
  page's name at title size on the left and the page-level actions on the right, and
  it sits ABOVE the scrolling region so both stay put while the content moves. Before
  it, each page hand-rolled a header: different fonts, different vertical positions,
  some with a subtitle, and Settings with its Save/Reset buttons at the BOTTOM of a
  long scroll — so the layout visibly moved every time you changed page. Buttons that
  act on a single row belong in that row, never in the header.

- **The tray menu greys itself out from the status, in code.** `UpdateTrayMenu` sets
  `IsEnabled` on the four transport items on every status tick. It is not a binding
  for the same reason as the point above: a ContextMenu has its own visual tree, so a
  `RelativeSource` binding silently resolves to null and every item stays enabled —
  "Start recording" offered during a recording, "Stop" offered when nothing is running.

- **An implicit style applies to the EXACT type only.** `HotkeyBox` derives from
  `TextBox`, so the implicit `TextBox` style skipped it and it fell back to the plain
  WPF template — visibly shorter and squarer than every field beside it. `Styles.xaml`
  now keeps a keyed `AppTextBox` and derives both the implicit style and the
  `HotkeyBox` one from it. The same trap waits for any other TextBox subclass.

- **Number fields show no clear button and no spinners** (`NumberField` in
  `Styles.xaml`). The X sits on top of the digits as soon as the field has focus, which
  in a 130 px box showing "86400" hides the number entirely, and clearing a required
  number to nothing is not a useful gesture — every one of these fields has a minimum.
  The spinners want the same corner and nobody nudges a 1800-second ceiling one second
  at a time. `MaxDecimalPlaces="0"`, `AcceptsExpression="False"` and
  `ValidationMode="InvalidInputOverwritten"` keep the field genuinely numeric.

- **The tray icon blinks while recording, and that is a feature.** `TrayPresenter`
  runs an 800 ms `DispatcherTimer` that alternates `tray-recording.ico` with the
  dimmer `tray-recording-dim.ico`. A static icon was tried and rejected: the icon is
  small, often hidden in the notification-area overflow, and a still red dot is not
  something you notice — movement is the only reliable signal that recording is
  actually running. Stopping and finalising deliberately hold the BRIGHT icon, so
  "still busy" never looks like "still recording".

- **One surface rule, stated in `Theme/Styles.xaml`.** `Card` is the page-level
  container and everything lives in one; `TableCard` is a `Card` with no padding for
  an edge-to-edge table; `TableRow` is how EVERY list of things is drawn; `ListRow`
  is only ever a nested item inside a card. Before this rule Recordings drew its list
  as floating grey blocks on the window background, Transfers drew the same idea as a
  flat table, and Home used white cards — three looks for one concept, and the pages
  visibly jumped as you moved between them.

- **Button size follows PLACEMENT, not the word.** `ActionButton` (32 px) for a page
  header or hero control, `RowActionButton` (26 px) inside a table row, and
  `OverflowButton` matches the row buttons beside it. Heights are fixed rather than
  derived from padding plus content, because padding-derived heights drift apart as
  soon as one button has a different label — which is how the same word ended up two
  different sizes on two pages.

- **Coloured surfaces are TINTS, never solid semantic fills.** A status chip is a
  ~18 % alpha tint of the semantic hue with the theme's matching foreground on top.
  A solid `SystemFillColorSuccessBrush` background with primary text on it is
  near-black on dark green in light mode — unreadable. The tint composites over
  whatever is behind it, so one value works in both themes.

- **Both hotkeys are toggles**, not four separate keys. A hotkey is pressed without
  looking at the screen, so "stop while idle" must be impossible rather than merely
  harmless.

- **Icons are generated code, not binaries.** `build/make-icons.ps1` draws all five
  `.ico` files in `Assets/`; the design lives in reviewable PowerShell. Every tray
  icon is the same rounded display with a different screen colour and a cut-out
  glyph, drawn edge to edge with a dark rim so it reads on both a light and a dark
  taskbar at 16 px.

- **The tray icon does not blink.** It used to alternate a bright and a dimmed icon
  on a timer, which reads as an alert rather than a status and is tiring in
  peripheral vision for an eight-hour recording. Four distinct static icons say the
  same thing while standing still — and nothing runs a timer for the length of a
  recording.
