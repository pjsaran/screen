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
    // Suffixed like the recorder's pipe when CAPTR_DATA_ROOT relocates Captr, so a
    // relocated copy (the end-to-end tests) has a window of its own instead of
    // handing focus to - and exiting in favour of - the user's own Captr.
    private static readonly string FocusSignalName = @"Local\CaptrUiFocus" + Core.Common.CaptrPaths.InstanceSuffix;

    private Mutex? _singleInstance;
    private EventWaitHandle? _focusSignal;
    private RegisteredWaitHandle? _focusWait;
    private HostConnection? _host;
    private HotkeyManager? _hotkeys;

    // When the last unexpected-error message was shown, so a failure repeating on
    // every tick produces one message, not a stack of them.
    private DateTimeOffset _lastErrorShown = DateTimeOffset.MinValue;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // There was no handler at all: an unexpected exception in any button, page,
        // or timer ended the window silently - no report, no message, sometimes a
        // ghost tray icon. Now it is written to a crash report and the person is told,
        // once, in words; the window carries on. A recording is never affected either
        // way: it belongs to the recorder process, not to this window.
        Core.Diagnostics.CrashReporter.Install("ui");
        DispatcherUnhandledException += OnUnexpectedError;

        // A Windows high-contrast theme is an accessibility setting, not a taste: the
        // app's own tinted cards and light/dark palette ignored it. Everything else
        // about the theme is left exactly as it was.
        if (SystemParameters.HighContrast)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.HighContrast);
        }

        // Single UI instance (SPEC §9): the second launch signals the first and exits.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\CaptrUi" + Core.Common.CaptrPaths.InstanceSuffix, out bool isFirst);
        _focusSignal = new EventWaitHandle(false, EventResetMode.AutoReset, FocusSignalName);
        if (!isFirst)
        {
            Core.Interop.ForegroundHandOff.AllowAnyProcess();
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
        _hotkeys = new HotkeyManager(
            window,
            () => window.Dispatcher.BeginInvoke(() => _ = window.Home.ToggleRecordingAsync()),
            () => window.Dispatcher.BeginInvoke(() => _ = window.Home.TogglePauseAsync()));

        // The Settings page re-applies them after every save, so a change takes effect
        // at once instead of at the next launch.
        window.ApplyHotkeys = _hotkeys.Apply;

        if (_hotkeys.Apply(settings.Hotkeys) is { Count: > 0 } conflicts)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, conflicts),
                "Hotkey conflicts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnUnexpectedError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        string? report = Core.Diagnostics.CrashReporter.Write("ui", e.Exception, "The window continued.");
        e.Handled = true;

        if (DateTimeOffset.UtcNow - _lastErrorShown < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastErrorShown = DateTimeOffset.UtcNow;
        MessageBox.Show(
            "Captr's window hit an unexpected problem: " + e.Exception.Message + Environment.NewLine + Environment.NewLine +
            "Any recording carries on - it runs separately from this window. If something on screen looks wrong, " +
            "close Captr from the tray and open it again." +
            (report is null ? "" : Environment.NewLine + Environment.NewLine + "Details were saved to " + report +
                " (Diagnostics > Export a support bundle includes it)."),
            "Captr", MessageBoxButton.OK, MessageBoxImage.Warning);
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
