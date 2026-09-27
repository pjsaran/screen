using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Captr.App.Services;
using Captr.Core.Common;
using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Ipc;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Transfers;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The status page's state: what the host reports, rendered for humans, plus the
/// start/pause/resume/stop commands. Owns no recording state — every property is a
/// projection of the latest host status (SPEC §4: the UI is a view).
/// </summary>
/// <remarks>
/// Each fact on the page is a PAIR of properties — a value and a hint. The value is
/// the number; the hint is the sentence that says what the number means. Splitting
/// them here keeps the XAML free of logic and means the wording lives in one place.
/// </remarks>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    /// <summary>Below this, the disk warning appears. Half an hour is long enough to
    /// react (lower the frame rate, free space, move the working folder) and short
    /// enough not to nag through a normal session.</summary>
    private const double LowDiskWarningMinutes = 30;

    private readonly HostConnection _host;
    private readonly Action<StatusResponse> _statusHandler;
    private readonly DisplayPreviewService _previews = new();

    [ObservableProperty]
    private string _stateText = "Idle";

    /// <summary>
    /// True until the first recording exists: Home then says where recordings go and
    /// offers a short test. There was no first-run guidance at all - the working
    /// folder was shown nowhere while idle, and the first Start spent several silent
    /// seconds proving an encoder.
    /// </summary>
    [ObservableProperty]
    private bool _showFirstRun;

    [ObservableProperty]
    private string _firstRunFolderText = "";

    /// <summary>Progress and outcome of the test recording, in words.</summary>
    [ObservableProperty]
    private string _testRecordingText = "";

    [ObservableProperty]
    private bool _isTestRunning;

    /// <summary>One line about transfers still moving or stuck; empty when there is
    /// nothing to say. See <see cref="TransferDigest"/>.</summary>
    [ObservableProperty]
    private string _transferLine = "";

    private string _workingFolder = "";

    private TransferQueue? _transferQueue;

    /// <summary>The last recording ended on its own and nobody has acknowledged it yet.</summary>
    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private string _elapsedText = "";

    [ObservableProperty]
    private string _detailText = "Press Start to begin recording every included display.";

    [ObservableProperty]
    private Brush _stateBrush = Brushes.Gray;

    [ObservableProperty]
    private string _coverageText = "—";

    [ObservableProperty]
    private string _coverageHint = "";

    [ObservableProperty]
    private string _encoderText = "—";

    [ObservableProperty]
    private string _encoderHint = "";

    [ObservableProperty]
    private string _captureText = "—";

    [ObservableProperty]
    private string _captureHint = "";

    [ObservableProperty]
    private string _diskText = "—";

    [ObservableProperty]
    private string _diskHint = "";

    [ObservableProperty]
    private string _warningText = "";

    [ObservableProperty]
    private string _displaysHint = "";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Pause is offered only while actually recording — not while paused
    /// (Resume is shown instead) and not while finalising.</summary>
    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private string _lastError = "";

    /// <summary>One tile per attached display.</summary>
    public ObservableCollection<DisplayTile> Displays { get; } = [];

    public HomeViewModel(HostConnection host)
    {
        _host = host;
        _statusHandler = status =>
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(status));
        _host.StatusChanged += _statusHandler;
        RefreshDisplays();
    }

    private void Apply(StatusResponse status)
    {
        IsRecording = StatusPresentation.IsActive(status.State);
        IsPaused = status.State == "paused";
        CanPause = status.State == "recording";
        IsFailed = status.State == StatusPresentation.Failed;

        (StateText, string brushKey) = status.State switch
        {
            "idle" => ("Idle", "TextFillColorPrimaryBrush"),
            "starting" => ("Starting", "SystemFillColorCautionBrush"),
            "recording" => ("Recording", "SystemFillColorCriticalBrush"),
            "paused" => ("Paused", "SystemFillColorCautionBrush"),
            "suspended" => ("Suspended", "SystemFillColorCautionBrush"),
            "stopping" or "finalizing" or "completed" => ("Finalising", "SystemFillColorCautionBrush"),
            "failed" => ("Recording stopped", "SystemFillColorCriticalBrush"),
            _ => (status.State, "TextFillColorPrimaryBrush"),
        };
        StateBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? StateBrush;

        ElapsedText = StatusPresentation.FormatElapsed(status.Elapsed);

        DetailText = status.State switch
        {
            "idle" => "Press Start to begin recording every included display.",
            "starting" => "Getting ready — the first recording on this PC checks which encoder works, which takes a few seconds.",
            "paused" => "The pause is recorded as a gap. Resume when you are ready.",
            "suspended" => "The PC went to sleep. The time asleep is recorded as a gap, and recording carries on when it wakes.",
            "stopping" or "finalizing" or "completed" => "Writing the final file — this is safe to leave running.",
            "failed" => StatusPresentation.DescribeFailure(status),
            _ => status.WorkingFolder is null ? "" : "Writing to " + status.WorkingFolder,
        };

        ApplyFacts(status);

        WarningText = status.DiskMinutesRemaining is { } minutes and < LowDiskWarningMinutes
            ? $"Only about {minutes:F0} minutes of recording space is left. Free some space, or lower the frame rate " +
              "or quality in Settings — both are allowed to be lowered while recording."
            : "";
    }

    /// <summary>The four facts in the strip: coverage, encoder, capture, disk.</summary>
    private void ApplyFacts(StatusResponse status)
    {
        // Coverage is stated honestly and always — "continuous" only when it truly
        // is (SPEC §6/§9: never present a recording as continuous when it isn't).
        if (status.GapCount == 0)
        {
            CoverageText = "Continuous";
            CoverageHint = "No gaps so far";
        }
        else
        {
            CoverageText = string.Create(CultureInfo.CurrentCulture, $"{status.Coverage:P1}");
            CoverageHint = $"{status.GapCount} gap(s) recorded";
        }

        EncoderText = status.Encoder ?? "—";
        EncoderHint = status.Encoder is null
            ? ""
            // The catalogue decides, not the name: libx264 leads the software tier and
            // was labelled "Hardware accelerated" on every CPU-only machine.
            : EncoderCatalog.IsSoftware(status.Encoder)
                ? "Software — no GPU encoder worked"
                : "Hardware accelerated";

        CaptureText = status.FrameRate is { } fps ? CaptureRates.Describe(fps) : "—";
        CaptureHint = status.Quality is { } quality
            ? QualityLevels.FindOrDefault(quality).DisplayName +
              (status.SpeedPreset is { } speed ? " · " + SpeedPresets.FindOrDefault(speed).DisplayName : "")
            : "";

        if (status.DiskMinutesRemaining is { } remaining)
        {
            DiskText = remaining >= 120
                ? string.Create(CultureInfo.CurrentCulture, $"{remaining / 60:F1} h")
                : string.Create(CultureInfo.CurrentCulture, $"{remaining:F0} min");
            DiskHint = "of recording space left";
        }
        else
        {
            DiskText = "—";
            DiskHint = "";
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        await RunHostCommandAsync(async () =>
        {
            StartResponse response = await _host.RequestAsync<StartResponse>(
                IpcKinds.Start, new StartRequest(null, null, null, null),
                startHostIfNeeded: true, CancellationToken.None);
            return response.Message;
        });
    }

    /// <summary>Acknowledges a failed recording; the page returns to idle.</summary>
    [RelayCommand]
    private void DismissFailure() => _host.DismissFailure();

    /// <summary>
    /// Re-reads the two things Home shows that are not in the recorder's status:
    /// whether any recording exists yet, and the transfer queue's one line. Called
    /// with the preview refresh; cheap, and every failure just leaves the old text.
    /// </summary>
    public async Task RefreshSurroundingsAsync()
    {
        try
        {
            string folder = new SettingsStore().Load().WorkingFolder;
            bool anyRecording = await Task.Run(() =>
                Directory.Exists(folder)
                && Directory.EnumerateFiles(folder, SessionJournal.FileName, SearchOption.AllDirectories).Any()).ConfigureAwait(true);
            _workingFolder = folder;
            FirstRunFolderText = "Recordings are saved to " + folder + ".";
            ShowFirstRun = !anyRecording && !IsRecording;

            string line = await Task.Run(() =>
                TransferDigest.Describe((_transferQueue ??= new TransferQueue()).ListRecent(DateTimeOffset.UtcNow))).ConfigureAwait(true);
            TransferLine = line;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or Microsoft.Data.Sqlite.SqliteException)
        {
            // Home is a status page; a folder or queue that cannot be read right now
            // keeps what it showed last rather than showing an error of its own.
        }
    }

    [RelayCommand]
    private void OpenWorkingFolder()
    {
        try
        {
            Directory.CreateDirectory(_workingFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _workingFolder) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or System.ComponentModel.Win32Exception or ArgumentException)
        {
            TestRecordingText = "The folder could not be opened: " + exception.Message;
        }
    }

    /// <summary>
    /// Records for ten seconds with the label "test" and says how it went: proof, on
    /// a new PC, that capture, the encoder and finalising all work - before the first
    /// recording that matters.
    /// </summary>
    [RelayCommand]
    private async Task RecordTestAsync()
    {
        IsTestRunning = true;
        LastError = "";
        TestRecordingText = "Starting… the first recording on this PC checks which encoder works, which takes a few seconds.";
        try
        {
            StartResponse started = await _host.RequestAsync<StartResponse>(
                IpcKinds.Start, new StartRequest(null, null, null, "test"), startHostIfNeeded: true, CancellationToken.None);
            if (started.AlreadyRecording)
            {
                TestRecordingText = "A recording is already running, so no test was made.";
                return;
            }

            TestRecordingText = "Recording a 10-second test…";
            await Task.Delay(TimeSpan.FromSeconds(10));
            string encoder = _host.LatestStatus.Encoder ?? "the encoder";
            await _host.RequestAsync<StopResponse>(IpcKinds.Stop, null, startHostIfNeeded: false, CancellationToken.None);

            TestRecordingText = "Saving the test recording…";
            DateTime giveUp = DateTime.UtcNow.AddMinutes(2);
            while (StatusPresentation.IsActive(_host.LatestStatus.State) && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(500);
            }

            if (_host.LatestStatus.State == StatusPresentation.Failed)
            {
                TestRecordingText = "The test recording failed: " + StatusPresentation.DescribeFailure(_host.LatestStatus);
                return;
            }

            RecordingSummary? test = RecordingCatalog.TryDescribe(
                Directory.EnumerateDirectories(_workingFolder).Where(folder => folder.EndsWith(started.SessionId.ToString("N")[..8], StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault() ?? "");
            TestRecordingText = test is { Finalized: true }
                ? $"Test recording OK: {test.RecordedSpan.TotalSeconds:F0} s with {encoder}, {ByteSize.Format(test.TotalBytes)}. " +
                  "It is on the Recordings page; delete it there when you have looked."
                : "The test recording was made but is still being saved. Check the Recordings page in a moment.";
            await RefreshSurroundingsAsync();
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException
                                               or ProtocolMismatchException or IOException)
        {
            TestRecordingText = "The test recording could not be made: " + exception.Message;
        }
        finally
        {
            IsTestRunning = false;
        }
    }

    [RelayCommand]
    private Task PauseAsync() => RunSimpleAsync(IpcKinds.Pause);

    [RelayCommand]
    private Task ResumeAsync() => RunSimpleAsync(IpcKinds.Resume);

    [RelayCommand]
    private async Task StopAsync()
    {
        await RunHostCommandAsync(async () =>
        {
            StopResponse response = await _host.RequestAsync<StopResponse>(
                IpcKinds.Stop, null, startHostIfNeeded: false, CancellationToken.None);
            return response.State;
        });
    }

    /// <summary>
    /// The single "record" toggle behind the hotkey and the tray: start when idle,
    /// stop when recording or paused. One key that always does the obvious thing
    /// beats two keys that each do the wrong thing half the time.
    /// </summary>
    public Task ToggleRecordingAsync() =>
        _host.LatestStatus.State is "recording" or "paused" ? StopAsync() : StartAsync();

    /// <summary>The "pause" toggle: pause when recording, resume when paused, and do
    /// nothing at all when idle — there is nothing to pause.</summary>
    public Task TogglePauseAsync() => _host.LatestStatus.State switch
    {
        "recording" => PauseAsync(),
        "paused" => ResumeAsync(),
        _ => Task.CompletedTask,
    };

    private async Task RunSimpleAsync(string kind)
    {
        await RunHostCommandAsync(async () =>
        {
            StateResponse response = await _host.RequestAsync<StateResponse>(
                kind, null, startHostIfNeeded: false, CancellationToken.None);
            return response.Message;
        });
    }

    private async Task RunHostCommandAsync(Func<Task<string>> command)
    {
        try
        {
            LastError = "";
            await command();
        }
        catch (Exception exception) when (
            exception is HostUnreachableException or IpcRequestException or ProtocolMismatchException)
        {
            LastError = exception.Message;
        }
    }

    /// <summary>Refreshes the display tiles (called on load and by the page's timer
    /// while it is visible). Previews are best-effort; a failure leaves the previous
    /// image in place.</summary>
    public void RefreshDisplays()
    {
        try
        {
            IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Displays.Count != displays.Count)
                {
                    foreach (DisplayTile stale in Displays)
                    {
                        stale.Dispose();
                    }

                    Displays.Clear();
                    foreach (DisplayInfo display in displays)
                    {
                        Displays.Add(new DisplayTile(display, _previews));
                    }

                    DisplaysHint = displays.Count == 1 ? "1 display" : $"{displays.Count} displays";
                }

                foreach (DisplayTile tile in Displays)
                {
                    tile.RefreshThumbnail();
                }
            });
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            // Preview only — never let it disturb the page.
        }
    }

    /// <summary>Releases the preview capture devices. Called when the status page
    /// goes off screen, so an unwatched page holds no GPU resources.</summary>
    public void ReleasePreviews() => _previews.Release();

    public void Dispose()
    {
        _host.StatusChanged -= _statusHandler;
        _previews.Dispose();
    }
}

/// <summary>One display tile: a name, its resolution, and a live thumbnail.</summary>
public sealed partial class DisplayTile : ObservableObject, IDisposable
{
    private readonly DisplayInfo _display;
    private readonly DisplayPreviewService _previews;

    /// <summary>Guards against stacking up capture tasks if one refresh takes longer
    /// than the timer interval — the shape of leak that turns a slow preview into a
    /// growing queue of background work.</summary>
    private int _refreshInFlight;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    public DisplayTile(DisplayInfo display, DisplayPreviewService previews)
    {
        _display = display;
        _previews = previews;
    }

    /// <summary>"Display 2 — DELL U2720Q".</summary>
    public string Title => $"Display {_display.WindowsDisplayNumber} — {_display.FriendlyName}";

    /// <summary>"3840 × 2160".</summary>
    public string Subtitle => $"{_display.Width} × {_display.Height}";

    public void RefreshThumbnail()
    {
        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                BitmapSource? bitmap = _previews.Capture(
                    _display.DxgiAdapterIndex, _display.DxgiOutputIndexOnAdapter);
                if (bitmap is not null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => Thumbnail = bitmap);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        });
    }

    public void Dispose() => Thumbnail = null;
}
