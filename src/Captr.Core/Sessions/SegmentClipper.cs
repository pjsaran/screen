using System.Diagnostics;

using Serilog;

namespace Captr.Core.Sessions;

/// <summary>
/// Extracts a clip from a finished recording by STREAM COPY at segment boundaries
/// (SPEC §9). Owns the "no re-encode" guarantee: a clip is a join of whole
/// segments, so it is bit-identical footage produced in seconds regardless of
/// length, and it can never soften the picture. Cutting mid-segment would require
/// re-encoding, so this deliberately does not offer it — the boundaries are the
/// product's unit of time.
/// </summary>
public sealed class SegmentClipper
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly ILogger _log;

    public SegmentClipper(string ffmpegPath, string ffprobePath, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _log = log.ForContext<SegmentClipper>();
    }

    /// <summary>Lists the session's segments with their durations, so a caller can
    /// choose a range. Index 1 is the first segment (what a user would call it).</summary>
    public static IReadOnlyList<ClipCandidate> ListSegments(string sessionFolder)
    {
        IntegrityRecord? record = IntegrityRecord.ReadOrNull(sessionFolder);
        if (record is null)
        {
            return [];
        }

        var candidates = new List<ClipCandidate>();
        TimeSpan offset = TimeSpan.Zero;
        int index = 1;
        foreach (FinalizedSegment segment in record.Segments)
        {
            candidates.Add(new ClipCandidate(index++, segment.FileName, offset, segment.Duration));
            offset += segment.Duration;
        }

        return candidates;
    }

    /// <summary>
    /// Joins segments <paramref name="firstIndex"/>..<paramref name="lastIndex"/>
    /// (inclusive, 1-based) into a new file by stream copy, and verifies its
    /// duration against the sum of its inputs.
    /// </summary>
    public async Task<string> ExtractAsync(
        string sessionFolder, int firstIndex, int lastIndex, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClipCandidate> segments = ListSegments(sessionFolder);
        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                $"No integrity record in {sessionFolder} — a clip can only be taken from a finalised recording.");
        }

        if (firstIndex < 1 || lastIndex > segments.Count || firstIndex > lastIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstIndex),
                $"Segment range {firstIndex}-{lastIndex} is outside 1-{segments.Count}.");
        }

        List<ClipCandidate> chosen = [.. segments.Skip(firstIndex - 1).Take(lastIndex - firstIndex + 1)];

        string clipPath = Path.Combine(
            sessionFolder, FormattableString.Invariant($"clip-{firstIndex:00}-{lastIndex:00}.mkv"));
        string listPath = Path.Combine(
            sessionFolder, FormattableString.Invariant($"clip-{firstIndex:00}-{lastIndex:00}.txt"));

        await File.WriteAllLinesAsync(
            listPath,
            chosen.Select(c => "file '" + Path.Combine(sessionFolder, c.FileName).Replace('\\', '/').Replace("'", "'\\''") + "'"),
            cancellationToken).ConfigureAwait(false);

        int exitCode = await RunAsync(
            _ffmpegPath,
            [
                "-hide_banner", "-nostats", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy", "-f", "matroska", clipPath,
            ],
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Stream-copy clip extraction failed with exit code {exitCode}.");
        }

        // Verify: same rule as the full join — the result must match its inputs.
        ProbeResult probe = await FfprobeClient.ProbeAsync(_ffprobePath, clipPath, cancellationToken).ConfigureAwait(false);
        if (!probe.Success)
        {
            throw new InvalidOperationException($"The extracted clip does not probe: {probe.FailureReason}");
        }

        TimeSpan expected = TimeSpan.FromTicks(chosen.Sum(c => c.Duration.Ticks));
        TimeSpan tolerance = TimeSpan.FromSeconds(0.5 + (expected.TotalSeconds * 0.02));
        if ((probe.Duration - expected).Duration() > tolerance)
        {
            _log.Warning("Clip duration {Actual} differs from the sum of its segments {Expected}",
                probe.Duration, expected);
        }

        _log.Information("Extracted clip {Clip}: segments {First}-{Last}, {Duration}",
            clipPath, firstIndex, lastIndex, probe.Duration);
        return clipPath;
    }

    private static async Task<int> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>One segment offered as a clip boundary: its 1-based number, file name,
/// offset from the start of the recording, and duration.</summary>
public sealed record ClipCandidate(int Index, string FileName, TimeSpan StartOffset, TimeSpan Duration);
