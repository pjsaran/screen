using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Captr.App.Services;
using Captr.App.ViewModels;
using Captr.App.Views;
using Captr.Core.Ipc;
using Captr.Core.Settings;

namespace Captr.App;

/// <summary>
/// The application shell: navigation between the five pages, the tray icon with its
/// unmistakable state (SPEC §9), close-to-tray, the quit-while-recording guard
/// (defaulting to continue recording), and the paused-state reminders. Holds no
/// recording state — everything it shows came from <see cref="HostConnection"/>.
/// </summary>
public partial class MainWindow
{
    private readonly HostConnection _host;
    private readonly StatusViewModel _statusViewModel;
    private readonly Page[] _pages;
    private readonly DispatcherTimer _pausedReminder;
    private DateTimeOffset _pausedSinceUtc;
    private bool _quitConfirmed;

    /// <summary>The status view model, shared with the tray menu and hotkeys so
    /// every entry point drives the same commands.</summary>
    public StatusViewModel Status => _statusViewModel;

    public MainWindow(HostConnection host)
    {
        _host = host;
        _statusViewModel = new StatusViewModel(host);
        InitializeComponent();

        _pages =
        [
            new StatusPage(_statusViewModel),
            new RecordingsPage(new RecordingsViewModel(host)),
            new DeliveryPage(new DeliveryViewModel(host)),
            new SettingsPage(new SettingsViewModel(host)),
            new DiagnosticsPage(new DiagnosticsViewModel()),
        ];
        PageHost.Navigate(_pages[0]);

        _host.StatusChanged += status => Dispatcher.BeginInvoke(() => UpdateTray(status));

        // A forgotten pause is an invisible hole in a recording (SPEC §6) — remind
        // every five minutes with elapsed paused time.
        _pausedReminder = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _pausedReminder.Tick += (_, _) => ShowPausedReminder();
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageHost is not null && NavigationList.SelectedIndex >= 0)
        {
            PageHost.Navigate(_pages[NavigationList.SelectedIndex]);
            while (PageHost.CanGoBack)
            {
                PageHost.RemoveBackEntry(); // No back-stack: the nav list is the navigation.
            }
        }
    }

    // ---- Tray state (SPEC §9: unmistakable at a glance) -------------------------

    private void UpdateTray(StatusResponse status)
    {
        (string glyph, string state) = status.State switch
        {
            "recording" => ("🔴", $"recording {status.Elapsed:hh\\:mm\\:ss}"),
            "paused" => ("⏸️", $"PAUSED — remember to resume"),
            "stopping" or "finalizing" => ("🟡", "finalising"),
            "failed" => ("❌", "stopped after repeated failures"),
            _ => ("⚪", "idle"),
        };

        string disk = status.DiskMinutesRemaining is { } minutes ? $" · {minutes:F0} min disk left" : "";
        TrayIcon.ToolTipText = $"Captr — {state}{disk}";

        // A glyph-rendered icon keeps the states visually distinct without shipping
        // an icon font: idle ⚪, recording 🔴, paused ⏸, error ❌.
        if (TrayIcon.IconSource is not H.NotifyIcon.GeneratedIconSource generated || generated.Text != glyph)
        {
            TrayIcon.IconSource = new H.NotifyIcon.GeneratedIconSource { Text = glyph, FontSize = 44 };
        }

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

    private void ShowPausedReminder()
    {
        TimeSpan paused = DateTimeOffset.UtcNow - _pausedSinceUtc;
        TrayIcon.ShowNotification(
            "Captr is still paused",
            $"Recording has been paused for {paused:hh\\:mm\\:ss}. Resume it if that was not intended.");
    }

    // ---- Close/quit behaviour (SPEC §9) -----------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        bool closeToTray = new SettingsStore().Load().CloseToTray;
        if (closeToTray)
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
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    // ---- Tray menu --------------------------------------------------------------

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayOpen(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayStart(object sender, RoutedEventArgs e) => _statusViewModel.StartCommand.Execute(null);

    private void OnTrayPause(object sender, RoutedEventArgs e) => _statusViewModel.PauseCommand.Execute(null);

    private void OnTrayResume(object sender, RoutedEventArgs e) => _statusViewModel.ResumeCommand.Execute(null);

    private void OnTrayStop(object sender, RoutedEventArgs e) => _statusViewModel.StopCommand.Execute(null);

    private void OnTrayQuit(object sender, RoutedEventArgs e) => QuitApplication();

    /// <summary>Brings the window back (also used by the single-instance focus signal).</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
