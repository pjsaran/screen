using System.Globalization;

namespace Captr.Core.Encoders;

/// <summary>
/// Produces the encoder process's complete argument vector from a
/// <see cref="RecordingPlan"/> (SPEC §5). Owns argument ORDER and exact text; both
/// are pinned by golden-file tests. Pure — building arguments never touches disk or
/// processes. If it is wrong, every recording is wrong the same way, which is exactly
/// why its output is golden-filed rather than trusted.
/// </summary>
/// <remarks>
/// The result is a vector (one argument per element) handed to
/// <c>ProcessStartInfo.ArgumentList</c> — never a concatenated command line
/// (SPEC §5). There is no shell anywhere, so no shell-quoting layer exists to hide
/// escaping mistakes: what this class emits is exactly what FFmpeg parses.
/// </remarks>
public static class FfmpegArgumentBuilder
{
    /// <summary>Builds the full argument vector for one recording session.</summary>
    public static IReadOnlyList<string> Build(RecordingPlan plan)
    {
        ArrangementPlan arrangement = ArrangementPlanner.Plan(plan.Sources);
        var arguments = new List<string>
        {
            // Quiet, machine-supervised operation: no banner, no human progress
            // spinner (progress goes to the machine-readable file below), warnings
            // and errors only on stderr.
            "-hide_banner",
            "-nostats",
            "-loglevel", "warning",

            "-filter_complex", FilterGraphBuilder.Build(plan, arrangement),
            "-map", "[v]",

            "-c:v", plan.Encoder.CodecName,
        };

        arguments.AddRange(plan.Encoder.QualityArguments);

        int segmentSeconds = EncodingConstants.SegmentSeconds;
        arguments.AddRange(
        [
            // An IDR frame at every segment boundary so segments later join by pure
            // stream copy (SPEC §5). The segment muxer only cuts at keyframes —
            // without this expression FFmpeg never rolls the file at all
            // (verified empirically; see Encoding/README.md).
            "-force_key_frames", FormattableString.Invariant($"expr:gte(t,n_forced*{segmentSeconds})"),
            "-g", EncodingConstants.GopCeiling(plan.FrameRate).ToString(CultureInfo.InvariantCulture),

            // Session marker inside every segment's container metadata, cross-checked
            // when re-adopting an orphaned encoder process (SPEC §4).
            "-metadata", FormattableString.Invariant($"{EncodingConstants.SessionMetadataKey}={plan.SessionId:D}"),

            // Machine-readable progress to a FILE so the encoder survives host death
            // and the supervisor can re-attach by tailing (SPEC §4/§6).
            "-progress", Path.Combine(plan.WorkingFolder, EncodingConstants.ProgressFileName),

            // Clock-aligned five-minute Matroska segments, each starting at t=0 so
            // the concat joiner can re-time them (SPEC §6).
            "-f", "segment",
            "-segment_format", "matroska",
            "-segment_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-segment_atclocktime", "1",
            "-reset_timestamps", "1",
            "-strftime", "1",
            Path.Combine(plan.WorkingFolder, SegmentFileNamePattern(plan.ArrangementGroup)),
        ]);

        return arguments;
    }

    /// <summary>
    /// The strftime pattern for segment files, e.g. <c>seg-g01-20260819-143000.mkv</c>.
    /// The arrangement group is baked into the name so finalisation can group
    /// join-compatible segments without probing (SPEC §6: one output per arrangement).
    /// </summary>
    public static string SegmentFileNamePattern(int arrangementGroup) =>
        FormattableString.Invariant($"seg-g{arrangementGroup:00}-%Y%m%d-%H%M%S.mkv");
}
