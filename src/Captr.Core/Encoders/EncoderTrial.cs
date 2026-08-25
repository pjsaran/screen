using System.Diagnostics;
using System.Globalization;

namespace Captr.Core.Encoders;

/// <summary>
/// Proves (or disproves) that one encoder candidate actually works on THIS machine
/// with THIS capture graph (SPEC §5: "appearing in the encoder list proves nothing…
/// run a short trial encode of the actual canvas"). A candidate passes only when the
/// trial output is non-empty and probes back with the expected dimensions and a
/// non-zero packet count. If this class lies, Captr claims hardware acceleration it
/// does not have — or silently records empty files.
/// </summary>
public static class EncoderTrial
{
    private static readonly TimeSpan TrialTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a short trial with the plan's REAL capture graph (ddagrab and all) and
    /// the candidate encoder, then verifies the output with ffprobe.
    /// </summary>
    /// <param name="seconds">Trial length — 2 s for pass/fail probing; the size
    /// estimator uses longer runs for a stable rate.</param>
    public static async Task<TrialResult> RunAsync(
        string ffmpegPath, string ffprobePath, RecordingPlan plan, int seconds, CancellationToken cancellationToken)
    {
        ArrangementPlan arrangement = ArrangementPlanner.Plan(plan.Sources);
        string outputPath = Path.Combine(plan.WorkingFolder, $"trial-{plan.Encoder.CodecName}.mkv");
        Directory.CreateDirectory(plan.WorkingFolder);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-y",
        };
        // The trial must exercise the plan's REAL capture path — GDI capture needs
        // its inputs declared here exactly as a real recording would declare them.
        arguments.AddRange(FilterGraphBuilder.BuildInputArguments(plan));
        arguments.AddRange(
        [
            "-filter_complex", FilterGraphBuilder.Build(plan, arrangement),
            "-map", "[v]",
            "-c:v", plan.Encoder.CodecName,
        ]);
        arguments.AddRange(plan.Encoder.QualityArguments);
        arguments.AddRange(["-t", seconds.ToString(CultureInfo.InvariantCulture), "-f", "matroska", outputPath]);

        try
        {
            return await RunAndProbeAsync(
                ffmpegPath, ffprobePath, arguments, outputPath, arrangement, seconds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // The trial file has served its purpose the moment the result is known.
            // Leaving it behind put a stray .mkv in every session folder, inflated the
            // reported size of the recording, and sat next to the real footage looking
            // like part of it. Deleted on every path, pass or fail.
            TryDelete(outputPath);
        }
    }

    private static async Task<TrialResult> RunAndProbeAsync(
        string ffmpegPath,
        string ffprobePath,
        IReadOnlyList<string> arguments,
        string outputPath,
        ArrangementPlan arrangement,
        int seconds,
        CancellationToken cancellationToken)
    {
        (int exitCode, string stderr) = await RunProcessAsync(ffmpegPath, arguments, TrialTimeout + TimeSpan.FromSeconds(seconds), cancellationToken)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            return TrialResult.Failed($"trial encode exited with {exitCode}: {Tail(stderr)}");
        }

        var output = new FileInfo(outputPath);
        if (!output.Exists || output.Length == 0)
        {
            return TrialResult.Failed("trial produced an empty file — the encoder advertised itself but wrote nothing");
        }

        // ffprobe the result: expected dimensions, and actual packets.
        (int probeExit, string probeOut) = await RunProcessAsync(
            ffprobePath,
            [
                "-v", "error", "-select_streams", "v:0", "-count_packets",
                "-show_entries", "stream=width,height,nb_read_packets", "-of", "csv=p=0", outputPath,
            ],
            TrialTimeout, cancellationToken).ConfigureAwait(false);

        if (probeExit != 0)
        {
            return TrialResult.Failed($"trial output does not probe: {Tail(probeOut)}");
        }

        string[] parts = probeOut.Trim().Split(',');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || !long.TryParse(parts[2], out long packets))
        {
            return TrialResult.Failed($"unexpected probe output: {probeOut.Trim()}");
        }

        if (width != arrangement.CanvasWidth || height != arrangement.CanvasHeight)
        {
            return TrialResult.Failed(
                $"trial dimensions {width}x{height} do not match the canvas {arrangement.CanvasWidth}x{arrangement.CanvasHeight}");
        }

        if (packets == 0)
        {
            return TrialResult.Failed("trial output contains zero packets");
        }

        return TrialResult.Passed(output.Length, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Removes the trial file. A file we cannot delete is not worth failing
    /// a recording over — the next trial overwrites it (<c>-y</c>) anyway.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;

        // Both pipes are drained concurrently — the same rule the supervisor lives
        // by, in miniature (a full pipe buffer would deadlock the trial).
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "trial timed out");
        }

        string stderr = await stderrTask.ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        return (process.ExitCode, stdout.Length > 0 ? stdout : stderr);
    }

    /// <summary>
    /// The most useful 400 characters of ffmpeg's complaint: the FIRST lines as well
    /// as the last.
    /// </summary>
    /// <remarks>
    /// This used to keep only the tail, and that hid a real bug for a long time. When
    /// the filter graph itself is rejected, ffmpeg says why on the very first line
    /// ("Padded dimensions cannot be smaller than input dimensions") and then prints
    /// several lines of consequences. Keeping only the tail reported every candidate
    /// as failing with a bare "Invalid argument", which reads like broken hardware and
    /// sent the diagnosis in entirely the wrong direction.
    /// </remarks>
    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length <= 400)
        {
            return trimmed;
        }

        return trimmed[..200] + " […] " + trimmed[^200..];
    }
}

/// <summary>Outcome of one trial: pass with measured output size (feeding the size
/// estimate), or fail with the reason (feeding the selection log).</summary>
public sealed record TrialResult(bool Success, string? FailureReason, long OutputBytes, TimeSpan Duration)
{
    public static TrialResult Passed(long outputBytes, TimeSpan duration) => new(true, null, outputBytes, duration);

    public static TrialResult Failed(string reason) => new(false, reason, 0, TimeSpan.Zero);
}
