using System.Windows.Media.Imaging;

using Captr.App.Services;
using Captr.Core.Displays;
using Captr.Core.Ipc;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The status page's state: what the host reports, rendered for humans, plus the
/// start/pause/resume/stop commands. Owns no recording state — every property is a
/// projection of the latest host status (SPEC §4: the UI is a view).
/// </summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly HostConnection _host;

    [ObservableProperty]
    private string _stateText = "Idle";

    [ObservableProperty]
    private string _detailText = "Nothing is recording.";

    [ObservableProperty]
    private string _diskText = "";

    [ObservableProperty]
    private string _coverageText = "";

    [ObservableProperty]
    private string _degradationText = "";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _lastError = "";

    /// <summary>One tile per attached display.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DisplayTile> Displays { get; } = [];

    public StatusViewModel(HostConnection host)
    {
        _host = host;
        _host.StatusChanged += status =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply(status));
        RefreshDisplays();
    }

    private void Apply(StatusResponse status)
    {
        IsRecording = status.State is "recording" or "paused" or "stopping" or "finalizing";
        IsPaused = status.State == "paused";

        StateText = status.State switch
        {
            "idle" => "Idle",
            "recording" => "● Recording",
            "paused" => "⏸ Paused — don't forget to resume",
            "stopping" or "finalizing" => "Finalising…",
            "failed" => "Recording stopped after repeated failures",
            _ => status.State,
        };

        DetailText = status.SessionId is null
            ? "Nothing is recording."
            : $"Elapsed {status.Elapsed:hh\\:mm\\:ss} · encoder {status.Encoder} · {status.FrameRate} fps";

        // Coverage is shown WHILE recording, and says "continuous" only when it
        // truly is (SPEC §6/§9).
        CoverageText = status.SessionId is null
            ? string.Empty
            : status.GapCount == 0
                ? "Coverage: continuous — no gaps so far"
                : string.Create(System.Globalization.CultureInfo.CurrentCulture,
                    $"Coverage: {status.Coverage:P1} — {status.GapCount} gap(s) recorded");

        DiskText = status.DiskMinutesRemaining is { } minutes
            ? $"Disk: about {minutes:F0} minutes of recording space left"
            : "";

        DegradationText = status.DiskMinutesRemaining is < 30
            ? "⚠ Disk space is getting low — consider lowering the frame rate or moving the working folder."
            : "";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        await RunHostCommandAsync(async () =>
        {
            StartResponse response = await _host.RequestAsync<StartResponse>(
                IpcKinds.Start, new StartRequest(null, null, null), startHostIfNeeded: true, CancellationToken.None);
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

    /// <summary>Refreshes the display tiles (called on load and by a slow timer).
    /// Thumbnails are best-effort; a failure leaves the previous image.</summary>
    public void RefreshDisplays()
    {
        try
        {
            IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Displays.Count != displays.Count)
                {
                    Displays.Clear();
                    foreach (DisplayInfo display in displays)
                    {
                        Displays.Add(new DisplayTile(display));
                    }
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
}

/// <summary>One display tile: label + live thumbnail.</summary>
public sealed partial class DisplayTile : ObservableObject
{
    private readonly DisplayInfo _display;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    public DisplayTile(DisplayInfo display) => _display = display;

    public string Label =>
        $"Display {_display.WindowsDisplayNumber} — {_display.FriendlyName} ({_display.Width}×{_display.Height})";

    public void RefreshThumbnail()
    {
        Task.Run(() =>
        {
            BitmapSource? bitmap = ThumbnailService.CaptureThumbnail(
                _display.DxgiAdapterIndex, _display.DxgiOutputIndexOnAdapter);
            if (bitmap is not null)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Thumbnail = bitmap);
            }
        });
    }
}
