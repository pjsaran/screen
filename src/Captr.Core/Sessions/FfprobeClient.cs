using System.Diagnostics;
using System.Globalization;

namespace Captr.Core.Sessions;

/// <summary>
/// Thin async wrapper around the bundled ffprobe for the questions finalisation
/// asks: does this segment play, how long is it, what are its dimensions, and what
/// session marker does it carry. Owns nothing but the process call; if it lies,
/// repair decisions are wrong.
/// </summary>
public static class FfprobeClient
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Probes one media file. Returns a failed result rather than throwing
    /// for a truncated/corrupt file — that is a normal input for recovery.</summary>
    public static async Task<ProbeResult> ProbeAsync(string ffprobePath, string mediaPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height : format=duration : format_tags=CAPTR_SESSION",
            "-of", "default=noprint_wrappers=1",
            mediaPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return ProbeResult.Failed("probe timed out");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return ProbeResult.Failed($"ffprobe exit {process.ExitCode}: {stderr.Trim()}");
        }

        int width = 0;
        int height = 0;
        double durationSeconds = 0;
        string? sessionMarker = null;

        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator];
            string value = line[(separator + 1)..];
            switch (key)
            {
                case "width":
                    _ = int.TryParse(value, out width);
                    break;
                case "height":
                    _ = int.TryParse(value, out height);
                    break;
                case "duration":
                    _ = double.TryParse(value, CultureInfo.InvariantCulture, out durationSeconds);
                    break;
                case "TAG:CAPTR_SESSION":
                    sessionMarker = value;
                    break;
            }
        }

        // A file that probes but reports no duration is truncated mid-write — the
        // signature of the segment that was open when power was lost.
        if (durationSeconds <= 0)
        {
            return ProbeResult.Failed("no duration — truncated or still open", stderr.Trim());
        }

        return new ProbeResult(
            Success: true, FailureReason: null, ErrorDetail: stderr.Trim(),
            Duration: TimeSpan.FromSeconds(durationSeconds), Width: width, Height: height,
            SessionMarker: sessionMarker);
    }
}

/// <summary>What ffprobe found. A failed probe carries the reason — recovery treats
/// failure as "repair me", not as an exception.</summary>
public sealed record ProbeResult(
    bool Success,
    string? FailureReason,
    string? ErrorDetail,
    TimeSpan Duration,
    int Width,
    int Height,
    string? SessionMarker)
{
    public static ProbeResult Failed(string reason, string? detail = null) =>
        new(false, reason, detail, TimeSpan.Zero, 0, 0, null);
}
