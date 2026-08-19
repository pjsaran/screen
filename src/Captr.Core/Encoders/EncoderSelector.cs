using Serilog;

namespace Captr.Core.Encoders;

/// <summary>
/// Picks the encoder a session will actually use (SPEC §5): walk the candidates in
/// preference order, trial-encode the real canvas with each, first proven winner
/// takes it; cache the answer against the machine fingerprint. Owns the honesty
/// rule: the selection result reports the encoder that actually passed — never a
/// hardware claim that was not demonstrated.
/// </summary>
public sealed class EncoderSelector
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly EncoderCache _cache;
    private readonly ILogger _log;

    public EncoderSelector(string ffmpegPath, string ffprobePath, EncoderCache cache, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _cache = cache;
        _log = log.ForContext<EncoderSelector>();
    }

    /// <summary>
    /// Selects the encoder for the given capture sources and quality choice.
    /// </summary>
    /// <param name="template">The session's plan with a placeholder encoder; each
    /// candidate is substituted in for its trial so the trial runs the REAL graph.</param>
    /// <param name="preset">Quality preset in force.</param>
    /// <param name="numericOverride">The user's optional quantizer override.</param>
    public async Task<EncoderSelection> SelectAsync(
        RecordingPlan template, QualityPreset preset, int? numericOverride, string appVersion, CancellationToken cancellationToken)
    {
        FfmpegCapabilities capabilities = FfmpegCapabilities.LoadFrom(_ffmpegPath);
        ArrangementPlan arrangement = ArrangementPlanner.Plan(template.Sources);
        string fingerprint = EncoderCache.BuildFingerprint(
            arrangement.CanvasWidth, arrangement.CanvasHeight, appVersion, capabilities.BuildId);

        var attempts = new List<string>();

        // Cache hit: verify the cached winner with ONE quick trial rather than
        // trusting it blindly — a driver can break between sessions without its
        // version changing (rare, but a wrong encoder here records nothing).
        if (_cache.TryGet(fingerprint) is { } cachedName)
        {
            RecordingPlan cachedPlan = WithEncoder(template, cachedName, preset, numericOverride, arrangement);
            TrialResult cachedTrial = await EncoderTrial.RunAsync(
                _ffmpegPath, _ffprobePath, cachedPlan, seconds: 2, cancellationToken).ConfigureAwait(false);
            if (cachedTrial.Success)
            {
                _log.Information("Encoder {Encoder} confirmed from cache", cachedName);
                return new EncoderSelection(cachedPlan.Encoder, EncoderCatalog.IsSoftware(cachedName), FromCache: true, attempts);
            }

            _log.Warning("Cached encoder {Encoder} failed its confirmation trial ({Reason}); re-probing all candidates",
                cachedName, cachedTrial.FailureReason);
        }

        foreach (string candidate in EncoderCatalog.Candidates(capabilities))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RecordingPlan candidatePlan = WithEncoder(template, candidate, preset, numericOverride, arrangement);
            TrialResult trial = await EncoderTrial.RunAsync(
                _ffmpegPath, _ffprobePath, candidatePlan, seconds: 2, cancellationToken).ConfigureAwait(false);

            if (trial.Success)
            {
                _cache.Store(fingerprint, candidate);
                _log.Information("Encoder selected: {Encoder} (software: {IsSoftware}); rejected: {Rejected}",
                    candidate, EncoderCatalog.IsSoftware(candidate), attempts);
                return new EncoderSelection(candidatePlan.Encoder, EncoderCatalog.IsSoftware(candidate), FromCache: false, attempts);
            }

            attempts.Add($"{candidate}: {trial.FailureReason}");
            _log.Information("Encoder candidate {Encoder} rejected: {Reason}", candidate, trial.FailureReason);
        }

        throw new EncoderSelectionException(
            "No encoder passed its trial on this machine. Attempts:\n  " + string.Join("\n  ", attempts));
    }

    private static RecordingPlan WithEncoder(
        RecordingPlan template, string encoderName, QualityPreset preset, int? numericOverride, ArrangementPlan arrangement)
    {
        IReadOnlyList<string> qualityArguments = QualityPresets.BuildQualityArguments(
            encoderName, preset, numericOverride, arrangement.CanvasWidth, arrangement.CanvasHeight, template.FrameRate);
        return template with { Encoder = new EncoderSettings(encoderName, qualityArguments) };
    }
}

/// <summary>The proven selection: the settings a session will run with,
/// whether the encoder is the software fallback tier, whether the answer came from
/// cache, and — for the honesty report — every candidate that failed and why.</summary>
public sealed record EncoderSelection(
    EncoderSettings Encoder,
    bool IsSoftware,
    bool FromCache,
    IReadOnlyList<string> RejectedCandidates);

/// <summary>No encoder works at all — recording cannot start (reported loudly,
/// never worked around silently).</summary>
public sealed class EncoderSelectionException(string message) : Exception(message);
