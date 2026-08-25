using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Serilog;

namespace Captr.Core.Sessions;

/// <summary>
/// The single path that turns a session's working folder into verified, joined
/// output files — for BOTH a clean stop and crash recovery, which by SPEC §6 must be
/// the same code: recovery is simply this pipeline applied to a session whose last
/// segment is truncated. Owns steps 2–6 of §6: probe every segment, repair truncated
/// ones (keeping originals until the repair verifies), reconcile durations against
/// the journal, hash everything into the integrity record, join by stream copy (one
/// output per arrangement group), and name the result. It NEVER deletes working
/// files. This is the code that runs on the worst day; it must never regress
/// (SPEC §14 chaos note).
/// </summary>
public sealed class FinalizationPipeline
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromMinutes(30);

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly ILogger _log;

    public FinalizationPipeline(string ffmpegPath, string ffprobePath, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _log = log.ForContext<FinalizationPipeline>();
    }

    /// <summary>
    /// Finalises the session in <paramref name="workingFolder"/>. The encoder must
    /// already be stopped (step 1 of §6 belongs to the supervisor).
    /// </summary>
    public async Task<FinalizationResult> RunAsync(string workingFolder, CancellationToken cancellationToken)
    {
        IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
            Path.Combine(workingFolder, SessionJournal.FileName));
        SessionStarted start = events.OfType<SessionStarted>().First();

        var notes = new List<string>();

        // --- 2. Probe every segment; repair what does not play -------------------
        var segments = new List<FinalizedSegment>();
        int repaired = 0;
        foreach (string segmentPath in Directory.GetFiles(workingFolder, "seg-*.mkv").Order(StringComparer.Ordinal))
        {
            (FinalizedSegment? segment, bool wasRepaired, string? note) =
                await ProbeAndRepairAsync(segmentPath, cancellationToken).ConfigureAwait(false);
            if (note is not null)
            {
                notes.Add(note);
            }

            if (segment is not null)
            {
                segments.Add(segment);
                repaired += wasRepaired ? 1 : 0;
            }
        }

        // --- 3. Reconcile summed durations vs wall clock vs journal --------------
        DateTimeOffset? fallbackEnd = LastEvidenceOfLife(workingFolder, segments);
        CoverageReport coverage = CoverageCalculator.Compute(events, fallbackEnd);

        TimeSpan summedSegments = TimeSpan.FromTicks(segments.Sum(s => s.Duration.Ticks));
        TimeSpan discrepancy = (summedSegments - coverage.RecordedSpan).Duration();
        string? reconciliationNote = null;
        if (discrepancy > ReconciliationTolerance(coverage.RecordedSpan))
        {
            reconciliationNote =
                $"Summed segment durations ({summedSegments}) differ from journal-derived recorded span " +
                $"({coverage.RecordedSpan}) by {discrepancy}. The recording is preserved as-is; the discrepancy is recorded, not hidden.";
            notes.Add(reconciliationNote);
            _log.Warning("Duration reconciliation discrepancy: {Discrepancy}", discrepancy);
        }

        // --- 5. Join by stream copy, one output per arrangement group ------------
        var outputs = new List<string>();
        foreach (IGrouping<int, FinalizedSegment> group in segments
                     .GroupBy(s => s.ArrangementGroup)
                     .OrderBy(g => g.Key))
        {
            string joinedPath = await JoinGroupAsync(workingFolder, group.Key, [.. group], notes, cancellationToken)
                .ConfigureAwait(false);
            outputs.Add(joinedPath);
        }

        // --- 4+6. Hash everything, close out the integrity record ----------------
        var record = new IntegrityRecord
        {
            SessionId = start.SessionId,
            FinalizedUtc = DateTimeOffset.UtcNow,
            Segments = segments,
            Outputs = await HashFilesAsync(outputs, cancellationToken).ConfigureAwait(false),
            Coverage = coverage,
            RepairedSegments = repaired,
            Notes = notes,
        };
        record.Write(workingFolder);

        // The terminal journal event — its presence is what recovery scans for.
        using (SessionJournal journal = SessionJournal.OpenExisting(workingFolder))
        {
            journal.Append(new SessionFinalized
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                OutputFiles = outputs,
                TotalSpan = coverage.TotalSpan,
                RecordedSpan = coverage.RecordedSpan,
                GapCount = coverage.GapCount,
                ReconciliationNote = reconciliationNote,
            });
        }

        _log.Information(
            "Finalised session {SessionId}: {Outputs} output(s), {Segments} segment(s), {Repaired} repaired, coverage {Coverage:P1}",
            start.SessionId, outputs.Count, segments.Count, repaired, coverage.Coverage);

        return new FinalizationResult(start, outputs, coverage, segments.Count, repaired, notes);
    }

    /// <summary>Half a second plus 2% of the span: segment muxer rounding and
    /// per-segment container overhead accumulate legitimately.</summary>
    private static TimeSpan ReconciliationTolerance(TimeSpan recordedSpan) =>
        TimeSpan.FromSeconds(0.5 + recordedSpan.TotalSeconds * 0.02);

    private async Task<(FinalizedSegment? Segment, bool Repaired, string? Note)> ProbeAndRepairAsync(
        string segmentPath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(segmentPath);
        ProbeResult probe = await FfprobeClient.ProbeAsync(_ffprobePath, segmentPath, cancellationToken).ConfigureAwait(false);
        if (probe.Success)
        {
            return (await ToFinalizedSegmentAsync(segmentPath, probe, cancellationToken).ConfigureAwait(false), false, null);
        }

        _log.Warning("Segment {Segment} does not probe ({Reason}); attempting repair", fileName, probe.FailureReason);

        // Repair by tolerant remux INTO A NEW FILE; the original is only set aside
        // — never deleted — and only after the repaired copy verifies (SPEC §6).
        string repairedPath = segmentPath + ".repaired.mkv";
        int exitCode = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats", "-loglevel", "error", "-y",
                "-err_detect", "ignore_err", "-fflags", "+genpts+igndts",
                "-i", segmentPath, "-c", "copy", "-f", "matroska", repairedPath,
            ],
            cancellationToken).ConfigureAwait(false);

        ProbeResult repairedProbe = exitCode == 0
            ? await FfprobeClient.ProbeAsync(_ffprobePath, repairedPath, cancellationToken).ConfigureAwait(false)
            : ProbeResult.Failed($"repair remux exited {exitCode}");

        if (!repairedProbe.Success)
        {
            string note = $"Segment {fileName} could not be repaired ({repairedProbe.FailureReason}); its footage is lost and accounted as a gap.";
            _log.Error("Segment {Segment} unrepairable: {Reason}", fileName, repairedProbe.FailureReason);
            return (null, false, note);
        }

        // Verified: set the original aside and promote the repaired file.
        string originalKeptAs = segmentPath + ".original";
        File.Move(segmentPath, originalKeptAs);
        File.Move(repairedPath, segmentPath);

        FinalizedSegment segment = await ToFinalizedSegmentAsync(segmentPath, repairedProbe, cancellationToken).ConfigureAwait(false);
        return (segment, true, $"Segment {fileName} was truncated and repaired ({repairedProbe.Duration} recovered); original kept as {Path.GetFileName(originalKeptAs)}.");
    }

    private static async Task<FinalizedSegment> ToFinalizedSegmentAsync(
        string path, ProbeResult probe, CancellationToken cancellationToken)
    {
        return new FinalizedSegment(
            FileName: Path.GetFileName(path),
            ArrangementGroup: ParseArrangementGroup(Path.GetFileName(path)),
            Duration: probe.Duration,
            SizeBytes: new FileInfo(path).Length,
            Sha256: await HashFileAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>"seg-g02-20260819-101500.mkv" → 2. Group 1 assumed for foreign names
    /// so a stray file cannot crash recovery.</summary>
    internal static int ParseArrangementGroup(string fileName)
    {
        const string prefix = "seg-g";
        if (fileName.StartsWith(prefix, StringComparison.Ordinal)
            && fileName.Length > prefix.Length + 2
            && int.TryParse(fileName.AsSpan(prefix.Length, 2), out int group))
        {
            return group;
        }

        return 1;
    }

    private async Task<string> JoinGroupAsync(
        string workingFolder, int group, List<FinalizedSegment> segments,
        List<string> notes, CancellationToken cancellationToken)
    {
        string joinedPath = Path.Combine(workingFolder, $"joined-g{group:00}.mkv");

        if (segments.Count == 1)
        {
            // A single segment needs no join — copy semantics via hard link would be
            // clever; a plain copy is boring and predictable. Working files stay.
            File.Copy(Path.Combine(workingFolder, segments[0].FileName), joinedPath, overwrite: true);
            return joinedPath;
        }

        // concat demuxer list. Single quotes with escaping per ffmpeg's concat rules.
        string listPath = Path.Combine(workingFolder, $"join-g{group:00}.txt");
        await File.WriteAllLinesAsync(
            listPath,
            segments.Select(s =>
                "file '" + Path.Combine(workingFolder, s.FileName).Replace('\\', '/').Replace("'", "'\\''") + "'"),
            cancellationToken).ConfigureAwait(false);

        int exitCode = await RunFfmpegAsync(
            [
                "-hide_banner", "-nostats", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy", "-f", "matroska", joinedPath,
            ],
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Stream-copy join of group {group} failed with exit code {exitCode}.");
        }

        // Verify the join: its duration must match the sum of its inputs (SPEC §6).
        ProbeResult joinedProbe = await FfprobeClient.ProbeAsync(_ffprobePath, joinedPath, cancellationToken).ConfigureAwait(false);
        TimeSpan expected = TimeSpan.FromTicks(segments.Sum(s => s.Duration.Ticks));
        if (!joinedProbe.Success)
        {
            throw new InvalidOperationException($"Joined file for group {group} does not probe: {joinedProbe.FailureReason}");
        }

        if ((joinedProbe.Duration - expected).Duration() > ReconciliationTolerance(expected))
        {
            notes.Add(FormattableString.Invariant(
                $"Joined group {group} duration {joinedProbe.Duration} differs from the sum of its segments {expected} beyond tolerance."));
        }

        return joinedPath;
    }

    private async Task<int> RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(JoinTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return -1;
        }

        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _log.Warning("ffmpeg step failed ({ExitCode}): {Stderr}", process.ExitCode, stderr.Trim());
        }

        return process.ExitCode;
    }

    /// <summary>Best evidence of when a crashed session actually stopped: the last
    /// heartbeat, or failing that the newest segment's write time.</summary>
    private static DateTimeOffset? LastEvidenceOfLife(string workingFolder, List<FinalizedSegment> segments)
    {
        if (HeartbeatSnapshot.ReadOrNull(workingFolder) is { } heartbeat)
        {
            return heartbeat.WrittenUtc;
        }

        if (segments.Count > 0)
        {
            return segments
                .Select(s => (DateTimeOffset?)File.GetLastWriteTimeUtc(Path.Combine(workingFolder, s.FileName)))
                .Max();
        }

        return null;
    }

    private static async Task<IReadOnlyList<HashedFile>> HashFilesAsync(
        List<string> paths, CancellationToken cancellationToken)
    {
        var hashed = new List<HashedFile>(paths.Count);
        foreach (string path in paths)
        {
            hashed.Add(new HashedFile(
                Path.GetFileName(path),
                new FileInfo(path).Length,
                await HashFileAsync(path, cancellationToken).ConfigureAwait(false)));
        }

        return hashed;
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>What finalisation produced, for reporting and transfer hand-off.</summary>
public sealed record FinalizationResult(
    SessionStarted Session,
    IReadOnlyList<string> OutputFiles,
    CoverageReport Coverage,
    int SegmentCount,
    int RepairedSegments,
    IReadOnlyList<string> Notes);

/// <summary>One verified segment inside the integrity record.</summary>
public sealed record FinalizedSegment(
    string FileName,
    int ArrangementGroup,
    TimeSpan Duration,
    long SizeBytes,
    string Sha256);

/// <summary>A hashed output file inside the integrity record.</summary>
public sealed record HashedFile(string FileName, long SizeBytes, string Sha256);
