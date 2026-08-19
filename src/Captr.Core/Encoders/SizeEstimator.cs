namespace Captr.Core.Encoders;

/// <summary>
/// Estimates recording size from a short trial encode of the REAL canvas — actual
/// screen content through the actual encoder — never a hardcoded table (SPEC §5).
/// Owns the arithmetic from measured bytes to "GB per hour / hours until the disk
/// is full"; the disk-space refusal in DiskGuard builds on the same numbers.
/// </summary>
public static class SizeEstimator
{
    /// <summary>Trial length. Longer = steadier estimate; 8 s captures enough real
    /// screen variation without a noticeable pre-recording delay.</summary>
    public const int TrialSeconds = 8;

    /// <summary>Runs the measurement trial with the plan's selected encoder.</summary>
    public static async Task<SizeEstimate> MeasureAsync(
        string ffmpegPath, string ffprobePath, RecordingPlan plan, CancellationToken cancellationToken)
    {
        TrialResult trial = await EncoderTrial.RunAsync(
            ffmpegPath, ffprobePath, plan, TrialSeconds, cancellationToken).ConfigureAwait(false);

        if (!trial.Success)
        {
            throw new EncoderSelectionException($"Size-estimate trial failed: {trial.FailureReason}");
        }

        double bytesPerSecond = trial.OutputBytes / trial.Duration.TotalSeconds;
        return new SizeEstimate((long)(bytesPerSecond * 3600));
    }
}

/// <summary>A measured size estimate, expressed the way SPEC §5 asks: GB per hour,
/// total for a session length, hours the free space allows.</summary>
public sealed record SizeEstimate(long BytesPerHour)
{
    public double GigabytesPerHour => BytesPerHour / 1_000_000_000.0;

    /// <summary>Estimated total for a typical session of the given length.</summary>
    public double GigabytesFor(TimeSpan sessionLength) => GigabytesPerHour * sessionLength.TotalHours;

    /// <summary>How long the given free space lasts at this rate.</summary>
    public TimeSpan RecordingTimeFor(long freeBytes) =>
        BytesPerHour <= 0 ? TimeSpan.MaxValue : TimeSpan.FromHours(freeBytes / (double)BytesPerHour);
}
