using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Captr.App.Services;
using Captr.App.Theme;
using Captr.App.ViewModels;
using Captr.App.Views;
using Captr.Core.Ipc;
using Captr.Core.Settings;

namespace Captr.App;

/// <summary>
/// The application shell: navigation between the five pages, the always-visible
/// state readout on the rail, the tray icon (SPEC §9), close-to-tray,
/// minimise-while-recording, the quit-while-recording guard (defaulting to continue
/// recording), and the paused-state reminders. Holds no recording state — everything
/// it shows came from <see cref="HostConnection"/>.
/// </summary>
public partial class MainWindow : IDisposable
{
    private readonly HostConnection _host;
    private readonly HomeViewModel _home;
    private readonly Page[] _pages;
    private readonly TrayPresenter _tray;
    private readonly DispatcherTimer _pausedReminder;

    /// <summary>The status subscription, kept so it can be detached on dispose. An
    /// event handler that outlives its subscriber is the classic WPF leak; the
    /// window unsubscribes explicitly rather than relying on process exit.</summary>
    private readonly Action<StatusResponse> _statusHandler;

    private DateTimeOffset _pausedSinceUtc;
    private bool _quitConfirmed;
    private bool _hiddenForRecording;

    /// <summary>Whether the last status said a recording was under way — so the shell
    /// can react to start and stop rather than to every one-second tick.</summary>
    private bool _wasRecording;

    private bool _disposed;

    /// <summary>The status view model, shared with the tray menu and hotkeys so
    /// every entry point drives the same commands.</summary>
    public HomeViewModel Home => _home;

    public MainWindow(HostConnection host)
    {
        _host = host;
        _home = new HomeViewModel(host);
        InitializeComponent();

        _pages =
        [
            new HomePage(_home),
            new RecordingsPage(new RecordingsViewModel(host)),
            new TransfersPage(new TransfersViewModel(host)),
            new SettingsPage(new SettingsViewModel(host)),
            new DiagnosticsPage(new DiagnosticsViewModel()),
            new HelpPage(),
        ];
        PageHost.Navigate(_pages[0]);

        _tray = new TrayPresenter(TrayIcon);

        _statusHandler = status => Dispatcher.BeginInvoke(() => ApplyStatus(status));
        _host.StatusChanged += _statusHandler;

        // A forgotten pause is an invisible hole in a recording (SPEC §6) — remind
        // every five minutes with elapsed paused time.
        _pausedReminder = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _pausedReminder.Tick += (_, _) => ShowPausedReminder();

        SizeToScreen();
    }

    /// <summary>
    /// Picks the window's one fixed size from the screen it will open on.
    /// </summary>
    /// <remarks>
    /// The window is deliberately not resizable (<c>ResizeMode="CanMinimize"</c>), so
    /// the size has to be right on every machine rather than left to the user to drag.
    /// A fraction of the work area handles that: roomy on a large monitor, and still
    /// fully visible on a small laptop screen — the work area excludes the taskbar, so
    /// the window can never open partly underneath it. The bounds keep it from
    /// becoming unusably narrow or absurdly wide on an ultrawide.
    /// </remarks>
    private void SizeToScreen()
    {
        const double widthFraction = 0.62;
        const double heightFraction = 0.72;

        double availableWidth = SystemParameters.WorkArea.Width;
        double availableHeight = SystemParameters.WorkArea.Height;

        // The lower bound is what the Settings page needs before its three-column
        // quality row starts to crowd; the upper bound stops an ultrawide monitor
        // producing a window with a metre of empty space in the middle.
        Width = Math.Clamp(availableWidth * widthFraction, 900, 1200);
        Height = Math.Clamp(availableHeight * heightFraction, 600, 820);

        // On a genuinely small screen, fitting wins over the preferred proportions.
        Width = Math.Min(Width, availableWidth - 40);
        Height = Math.Min(Height, availableHeight - 40);
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageHost is null || NavigationList.SelectedIndex < 0)
        {
            return;
        }

        // Tell the outgoing page it is no longer visible so it can stop timers and
        // release the resources behind them (the display previews, above all).
        if (PageHost.Content is IPageLifecycle leaving)
        {
            leaving.OnLeaving();
        }

        Page target = _pages[NavigationList.SelectedIndex];
        PageHost.Navigate(target);
        while (PageHost.CanGoBack)
        {
            PageHost.RemoveBackEntry(); // No back-stack: the nav list is the navigation.
        }

        if (target is IPageLifecycle entering)
        {
            entering.OnEntering();
        }
    }

    // ---- Shell state (SPEC §9: unmistakable at a glance) ------------------------

    private void ApplyStatus(StatusResponse status)
    {
        _tray.Update(status);
        UpdateTrayMenu(status);
        UpdateRail(status);
        UpdatePausedReminder(status);
        HideForRecordingIfRequested(status);
    }

    /// <summary>
    /// Greys out the tray commands that make no sense right now. Without this every
    /// item is always clickable: "Start recording" during a recording, "Stop" when
    /// nothing is running. Clicking them was harmless - the host refuses - but an
    /// enabled menu item that does nothing is a bug report waiting to happen.
    /// </summary>
    private void UpdateTrayMenu(StatusResponse status)
    {
        bool busy = status.State is "recording" or "paused" or "stopping" or "finalizing";

        TrayStartItem.IsEnabled = !busy;

        // Not while stopping or finalising: the stop has already been asked for.
        TrayStopItem.IsEnabled = status.State is "recording" or "paused";

        TrayPauseItem.IsEnabled = status.State == "recording";
        TrayResumeItem.IsEnabled = status.State == "paused";
    }

    /// <summary>The rail's state dot and two lines of text — the same information the
    /// tray tooltip carries, for when the window IS open.</summary>
    private void UpdateRail(StatusResponse status)
    {
        (string label, string detail, string brushKey) = status.State switch
        {
            "recording" => ("Recording", $"Elapsed {status.Elapsed:hh\\:mm\\:ss}", "SystemFillColorCriticalBrush"),
            "paused" => ("Paused", "Resume when ready", "SystemFillColorCautionBrush"),
            "stopping" or "finalizing" => ("Finalising", "Writing the final file", "SystemFillColorCautionBrush"),
            "failed" => ("Failed", "Stopped after repeated errors", "SystemFillColorCriticalBrush"),
            _ => ("Idle", "Nothing is recording", "TextFillColorTertiaryBrush"),
        };

        // Typo.Text rather than Text: the rail label is uppercase and letter-spaced,
        // and setting Text directly would bypass that and print cramped capitals.
        Theme.Typo.SetText(RailStateText, label);
        RailDetailText.Text = detail;

        Brush? brush = TryFindResource(brushKey) as Brush;
        RailStateDot.Fill = brush ?? RailStateDot.Fill;
        RailStateText.Foreground = brush ?? RailStateText.Foreground;
    }

    private void UpdatePausedReminder(StatusResponse status)
    {
        bool isPaused = status.State == "paused";
        if (isPaused && !_pausedReminder.IsEnabled)
        {
            _pausedSinceUtc = DateTimeOffset.UtcNow;
            _pausedReminder.Start();
        }
        else if (!isPaused && _pausedReminder.IsEnabled)
        {
            _pausedReminder.Stop();
        }
    }

    /// <summary>
    /// "Minimise while recording" (a setting): the moment recording starts, the
    /// window disappears and Captr lives ONLY in the tray — which is usually what
    /// you want, since the recorder's own window is the last thing worth recording.
    /// The window comes back by itself when the recording ends, but only if Captr
    /// was the one that hid it.
    /// </summary>
    private void HideForRecordingIfRequested(StatusResponse status)
    {
        bool recording = status.State is "recording" or "paused";

        // Act only on the TRANSITION, not on every status tick. Status arrives once a
        // second, and reading settings means a file read, a JSON parse, and a
        // migration pass — none of which should happen sixty times a minute for the
        // whole length of a recording.
        if (recording == _wasRecording)
        {
            return;
        }

        _wasRecording = recording;

        if (recording)
        {
            if (IsVisible && LoadSettings().MinimiseWhileRecording)
            {
                _hiddenForRecording = true;
                Hide();
                _tray.Notify("Captr is recording", "The window is hidden. Open it again from the tray icon.");
            }
        }
        else if (_hiddenForRecording)
        {
            _hiddenForRecording = false;
            RestoreFromTray();
        }
    }

    private void ShowPausedReminder()
    {
        TimeSpan paused = DateTimeOffset.UtcNow - _pausedSinceUtc;
        _tray.Notify(
            "Captr is still paused",
            $"Recording has been paused for {paused:hh\\:mm\\:ss}. Resume it if that was not intended.");
    }

    // ---- Close/quit behaviour (SPEC §9) -----------------------------------------

    /// <inheritdoc />
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // Minimising while recording sends Captr to the tray rather than the taskbar,
        // so there is exactly one place it lives while a recording is in progress.
        if (WindowState == WindowState.Minimized
            && _host.LatestStatus.State is "recording" or "paused"
            && LoadSettings().MinimiseWhileRecording)
        {
            _hiddenForRecording = true;
            Hide();
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        if (LoadSettings().CloseToTray)
        {
            // Closing hides to tray; recording (in the host process) is unaffected.
            e.Cancel = true;
            Hide();
            return;
        }

        if (!ConfirmQuitWhileRecording())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>True when quitting may proceed. Defaults to CONTINUING to record —
    /// the destructive-feeling choice needs the explicit click (SPEC §9). Quitting
    /// the UI never stops the recording; the host runs on.</summary>
    private bool ConfirmQuitWhileRecording()
    {
        if (_host.LatestStatus.State is not ("recording" or "paused"))
        {
            return true;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "A recording is in progress. It will CONTINUE in the background even if you close Captr — " +
            "the window is only a viewer.\n\nClose the Captr window?",
            "Recording in progress",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>Actually exits the application (tray Quit).</summary>
    public void QuitApplication()
    {
        if (!ConfirmQuitWhileRecording())
        {
            return;
        }

        _quitConfirmed = true;
        Dispose();
        Application.Current.Shutdown();
    }

    // ---- Tray menu --------------------------------------------------------------

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayOpen(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayStart(object sender, RoutedEventArgs e) => _home.StartCommand.Execute(null);

    private void OnTrayPause(object sender, RoutedEventArgs e) => _home.PauseCommand.Execute(null);

    private void OnTrayResume(object sender, RoutedEventArgs e) => _home.ResumeCommand.Execute(null);

    private void OnTrayStop(object sender, RoutedEventArgs e) => _home.StopCommand.Execute(null);

    private void OnTrayQuit(object sender, RoutedEventArgs e) => QuitApplication();

    /// <summary>Brings the window back (also used by the single-instance focus
    /// signal and by the tray). Opening the window by hand cancels the
    /// hide-while-recording state, so Captr does not immediately hide it again.</summary>
    public void RestoreFromTray()
    {
        _hiddenForRecording = false;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private static CaptrSettings LoadSettings() => new SettingsStore().Load();

    /// <summary>Detaches the status subscription and releases the tray icon and the
    /// pages' timers. Called both by the tray's Quit and by application exit, so it
    /// must be safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.StatusChanged -= _statusHandler;
        _pausedReminder.Stop();

        foreach (Page page in _pages)
        {
            (page as IDisposable)?.Dispose();
        }

        _tray.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Implemented by pages that hold something worth releasing when they are not on
/// screen — a refresh timer, a capture device, a file watcher. The shell calls these
/// as the user navigates, so an unseen page costs nothing. Without it, every page's
/// timer would run for the life of the application (the display previews alone would
/// keep a GPU duplication alive forever).
/// </summary>
public interface IPageLifecycle
{
    /// <summary>The page has become the visible one.</summary>
    void OnEntering();

    /// <summary>The page is no longer visible.</summary>
    void OnLeaving();
}
