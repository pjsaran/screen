using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Captr.App.Services;
using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Ipc;

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
        IsRecording = status.State is "recording" or "paused" or "stopping" or "finalizing";
        IsPaused = status.State == "paused";
        CanPause = status.State == "recording";

        (StateText, string brushKey) = status.State switch
        {
            "idle" => ("Idle", "TextFillColorPrimaryBrush"),
            "recording" => ("Recording", "SystemFillColorCriticalBrush"),
            "paused" => ("Paused", "SystemFillColorCautionBrush"),
            "stopping" or "finalizing" => ("Finalising", "SystemFillColorCautionBrush"),
            "failed" => ("Stopped after repeated failures", "SystemFillColorCriticalBrush"),
            _ => (status.State, "TextFillColorPrimaryBrush"),
        };
        StateBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? StateBrush;

        ElapsedText = status.Elapsed is { } elapsed ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "";

        DetailText = status.State switch
        {
            "idle" => "Press Start to begin recording every included display.",
            "paused" => "The pause is recorded as a gap. Resume when you are ready.",
            "stopping" or "finalizing" => "Writing the final file — this is safe to leave running.",
            "failed" => "Everything recorded before the failure was finalised and kept.",
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
            : status.Encoder.Contains("openh264", StringComparison.OrdinalIgnoreCase)
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
