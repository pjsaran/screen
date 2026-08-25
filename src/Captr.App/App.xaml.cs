using System.Windows;

using Captr.App.Services;

namespace Captr.App;

/// <summary>
/// The WPF application object for the UI role. Owns composition (the host
/// connection, the main window, hotkeys), the single-instance rule (a second launch
/// focuses the first, SPEC §9), and start-minimised. Holds no recording state
/// whatsoever (SPEC §4: the UI is a view — it can be closed, killed, or relaunched
/// mid-recording with no effect on capture).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "A WPF Application's lifetime IS the process lifetime; the fields are released in OnExit.")]
public partial class App : Application
{
    /// <summary>Signalled by a second instance to ask the first to come forward.</summary>
    private const string FocusSignalName = @"Local\CaptrUiFocus";

    private Mutex? _singleInstance;
    private EventWaitHandle? _focusSignal;
    private RegisteredWaitHandle? _focusWait;
    private HostConnection? _host;
    private HotkeyManager? _hotkeys;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single UI instance (SPEC §9): the second launch signals the first and exits.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\CaptrUi", out bool isFirst);
        _focusSignal = new EventWaitHandle(false, EventResetMode.AutoReset, FocusSignalName);
        if (!isFirst)
        {
            _focusSignal.Set();
            Shutdown();
            return;
        }

        // Open the settings file and the transfer database on a background thread now,
        // while the window is still being built and nothing is clickable, rather than
        // on whichever page the user opens first. See Services/Warmup.
        Warmup.Begin();

        string version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0";
        _host = new HostConnection(version);
        _host.Start();

        var window = new MainWindow(_host);
        MainWindow = window;

        // The second-instance focus signal, serviced on the dispatcher.
        _focusWait = ThreadPool.RegisterWaitForSingleObject(
            _focusSignal,
            (_, _) => Dispatcher.BeginInvoke(window.RestoreFromTray),
            null, -1, executeOnlyOnce: false);

        RegisterHotkeys(window);

        Core.Settings.CaptrSettings settings = new Core.Settings.SettingsStore().Load();
        if (settings.StartMinimised)
        {
            // Start hidden; the tray icon is the presence.
            window.WindowState = WindowState.Minimized;
            window.Show();
            window.Hide();
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// Registers the two global hotkeys. Both are TOGGLES (SPEC §9 as refined for
    /// usability): one key starts or stops, the other pauses or resumes. A hotkey is
    /// pressed without looking at the screen, so it must never be possible to press
    /// "stop" while idle or "start" while already recording — with a toggle, it isn't.
    /// </summary>
    private void RegisterHotkeys(MainWindow window)
    {
        Core.Settings.CaptrSettings settings = new Core.Settings.SettingsStore().Load();
        _hotkeys = new HotkeyManager(window);
        _hotkeys.Register(settings.Hotkeys.RecordToggle, "start/stop recording",
            () => window.Dispatcher.BeginInvoke(() => _ = window.Home.ToggleRecordingAsync()));
        _hotkeys.Register(settings.Hotkeys.PauseToggle, "pause/resume recording",
            () => window.Dispatcher.BeginInvoke(() => _ = window.Home.TogglePauseAsync()));

        if (_hotkeys.Conflicts.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, _hotkeys.Conflicts),
                "Hotkey conflicts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _focusWait?.Unregister(null);

        // Release the window's tray icon, page timers, and status subscription on
        // EVERY exit path, not only the tray's Quit — a shell-initiated shutdown or
        // a log-off would otherwise leave a ghost icon in the notification area
        // until the user hovers over it.
        (base.MainWindow as Captr.App.MainWindow)?.Dispose();

        // Fire-and-forget: the process is exiting; the poll loop dies with it and
        // holds nothing that needs an orderly flush.
        _ = _host?.DisposeAsync().AsTask();
        _singleInstance?.Dispose();
        _focusSignal?.Dispose();
        base.OnExit(e);
    }
}
