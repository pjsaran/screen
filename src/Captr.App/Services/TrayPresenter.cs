using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Captr.Core.Ipc;

using H.NotifyIcon;

namespace Captr.App.Services;

/// <summary>
/// Drives the notification-area icon: which state it shows and what its tooltip
/// says. Owns everything about how Captr looks in the tray (SPEC §9: "the tray icon
/// must make state unmistakable at a glance across idle, recording, paused, and
/// error, with elapsed time and remaining disk in its tooltip").
/// </summary>
/// <remarks>
/// <para>
/// Real multi-resolution .ico resources are used rather than rendered text glyphs.
/// Glyph-rendered icons looked tiny (glyph metrics leave large margins inside the
/// 16 px square) and vanished on a dark taskbar; the generated icons fill the
/// square and carry a dark rim so they read on both light and dark taskbars
/// (see build/make-icons.ps1).
/// </para>
/// <para>
/// <b>The icon blinks while recording, and only while recording.</b> "Am I actually
/// recording?" is the one question the tray exists to answer, and a still icon does
/// not answer it from the corner of your eye — movement does. Only the SCREEN inside
/// the icon dims; the silhouette stays put, so it reads as a pulse rather than as a
/// flicker. The timer runs only while a recording is in progress, so an idle Captr
/// costs nothing.
/// </para>
/// </remarks>
public sealed class TrayPresenter : IDisposable
{
    /// <summary>Blink period. Slow enough not to irritate in peripheral vision, fast
    /// enough to read as "live" rather than as "stuck".</summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(800);

    private readonly TaskbarIcon _icon;
    private readonly DispatcherTimer _blinkTimer;

    private readonly BitmapImage _idle = Load("tray-idle.ico");
    private readonly BitmapImage _recording = Load("tray-recording.ico");
    private readonly BitmapImage _recordingDim = Load("tray-recording-dim.ico");
    private readonly BitmapImage _paused = Load("tray-paused.ico");
    private readonly BitmapImage _error = Load("tray-error.ico");

    private bool _blinkOn = true;

    public TrayPresenter(TaskbarIcon icon)
    {
        _icon = icon;
        _icon.IconSource = _idle;

        _blinkTimer = new DispatcherTimer { Interval = BlinkInterval };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            _icon.IconSource = _blinkOn ? _recording : _recordingDim;
        };
    }

    /// <summary>Applies the latest host status to the icon and tooltip.</summary>
    public void Update(StatusResponse status)
    {
        switch (status.State)
        {
            case "recording":
                StartBlinking();
                break;

            // Finalising is not recording: the picture has stopped, so the icon
            // holds still while the file is written.
            case "stopping" or "finalizing":
                StopBlinking(_recording);
                break;

            case "paused":
                StopBlinking(_paused);
                break;

            case "failed":
                StopBlinking(_error);
                break;

            default:
                StopBlinking(_idle);
                break;
        }

        _icon.ToolTipText = BuildTooltip(status);
    }

    private void StartBlinking()
    {
        if (_blinkTimer.IsEnabled)
        {
            return;
        }

        _blinkOn = true;
        _icon.IconSource = _recording;
        _blinkTimer.Start();
    }

    private void StopBlinking(BitmapImage icon)
    {
        _blinkTimer.Stop();
        _icon.IconSource = icon;
    }

    /// <summary>Everything the user needs without opening the window: what it is
    /// doing, for how long, how honest the coverage is, and how much disk is left.</summary>
    private static string BuildTooltip(StatusResponse status)
    {
        if (status.SessionId is null)
        {
            return "Captr — idle";
        }

        string headline = status.State switch
        {
            "recording" => $"Recording {status.Elapsed:hh\\:mm\\:ss}",
            "paused" => $"PAUSED at {status.Elapsed:hh\\:mm\\:ss} — resume when ready",
            "stopping" or "finalizing" => "Finalising the recording",
            "failed" => "Stopped after repeated encoder failures",
            _ => status.State,
        };

        var lines = new List<string> { "Captr — " + headline };

        if (status.GapCount > 0)
        {
            lines.Add(string.Create(System.Globalization.CultureInfo.CurrentCulture,
                $"{status.GapCount} gap(s), {status.Coverage:P1} covered"));
        }

        if (status.DiskMinutesRemaining is { } minutes)
        {
            lines.Add($"About {minutes:F0} minutes of disk left");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Shows a balloon notification (used for the paused reminder).</summary>
    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    private static BitmapImage Load(string fileName) =>
        new(new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute));

    public void Dispose()
    {
        _blinkTimer.Stop();
        _icon.Dispose();
    }
}
