using Serilog;

namespace Captr.Core.Encoders;

/// <summary>
/// Picks the encoder a session will actually use (SPEC §5): walk the candidates in
/// preference order, trial-encode the real canvas with each, first proven winner
/// takes it; cache the answer, and the rate it wrote at, against the machine
/// fingerprint. Owns the honesty rule: the selection result reports the encoder that
/// actually passed — never a hardware claim that was not demonstrated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cache hit skips the trials.</b> Probing costs a 2-second confirmation
/// encode plus an 8-second size measurement, and a user pressing Record waits for
/// both. On the second and every later recording with the same GPU, driver, canvas,
/// and settings, the answer cannot have changed for any reason the fingerprint does
/// not already cover, so the cached answer is used immediately and the recording
/// starts at once.
/// </para>
/// <para>
/// The rare case the fingerprint cannot see — a driver that broke without changing
/// its version — is caught by the recording itself: the supervisor notices an encoder
/// producing no output within seconds, and the session engine calls
/// <see cref="EncoderCache.Invalidate"/> so the next start re-probes properly. That
/// costs one failed start instead of ten seconds on every start.
/// </para>
/// </remarks>
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
    /// Selects the encoder for the given capture sources and encoding choices, and
    /// returns the rate it writes at — measured on a first run, remembered after.
    /// </summary>
    /// <param name="template">The session's plan with a placeholder encoder; each
    /// candidate is substituted in for its trial so the trial runs the REAL graph.</param>
    /// <param name="speed">The speed preset in force.</param>
    /// <param name="quality">The quality level in force.</param>
    public async Task<EncoderSelection> SelectAsync(
        RecordingPlan template,
        SpeedPreset speed,
        QualityLevel quality,
        string appVersion,
        CancellationToken cancellationToken)
    {
        FfmpegCapabilities capabilities = FfmpegCapabilities.LoadFrom(_ffmpegPath);
        ArrangementPlan arrangement = ArrangementPlanner.Plan(template.Sources);
        string fingerprint = EncoderCache.BuildFingerprint(
            arrangement.CanvasWidth, arrangement.CanvasHeight, appVersion, capabilities.BuildId,
            template.FrameRate, speed.Name, quality.Name);

        if (_cache.TryGet(fingerprint) is { } cached)
        {
            _log.Information(
                "Encoder {Encoder} ({Capture}) taken from cache at {GigabytesPerHour:F2} GB/h — skipping the probe so recording starts immediately",
                cached.EncoderName, cached.CaptureMethod, cached.BytesPerHour / 1_000_000_000.0);

            RecordingPlan cachedPlan = WithEncoder(template, cached.EncoderName, speed, quality, arrangement);
            return new EncoderSelection(
                cachedPlan.Encoder,
                EncoderCatalog.IsSoftware(cached.EncoderName),
                FromCache: true,
                RejectedCandidates: [],
                BytesPerHour: cached.BytesPerHour,
                CaptureMethod: cached.CaptureMethod);
        }

        var attempts = new List<string>();

        // Capture methods, in preference order. Desktop Duplication is cheap and
        // GPU-side; GDI costs real CPU but works on virtual display drivers (AWS
        // WorkSpaces, VMs, some RDP hosts) where Desktop Duplication cannot even
        // create its D3D11 device. Without this second pass, such machines reported
        // "no working encoder" when every encoder was fine and only capture was not.
        foreach (CaptureMethod captureMethod in new[] { CaptureMethod.DesktopDuplication, CaptureMethod.Gdi })
        {
            foreach (string candidate in EncoderCatalog.Candidates(capabilities))
            {
                cancellationToken.ThrowIfCancellationRequested();

                RecordingPlan candidatePlan = WithEncoder(template, candidate, speed, quality, arrangement)
                    with
                { CaptureMethod = captureMethod };

                // One trial does both jobs: it proves the encoder works AND measures how
                // fast it writes, which is what the disk preflight needs. Running a short
                // probe followed by a longer measurement would double the wait for no
                // extra information.
                TrialResult trial = await EncoderTrial.RunAsync(
                    _ffmpegPath, _ffprobePath, candidatePlan, EncodingConstants.EncoderTrialSeconds, cancellationToken)
                    .ConfigureAwait(false);

                if (trial.Success)
                {
                    long bytesPerHour = (long)(trial.OutputBytes / trial.Duration.TotalSeconds * 3600);
                    _cache.Store(fingerprint, candidate, bytesPerHour, captureMethod);
                    _log.Information(
                        "Encoder selected: {Encoder} (software: {IsSoftware}, capture: {Capture}) at {GigabytesPerHour:F2} GB/h; rejected: {Rejected}",
                        candidate, EncoderCatalog.IsSoftware(candidate), captureMethod,
                        bytesPerHour / 1_000_000_000.0, attempts);

                    return new EncoderSelection(
                        candidatePlan.Encoder,
                        EncoderCatalog.IsSoftware(candidate),
                        FromCache: false,
                        attempts,
                        bytesPerHour,
                        captureMethod);
                }

                attempts.Add($"{candidate} ({Describe(captureMethod)}): {trial.FailureReason}");
                _log.Information("Encoder candidate {Encoder} ({Capture}) rejected: {Reason}",
                    candidate, captureMethod, trial.FailureReason);

                // When the CAPTURE failed, every remaining candidate on this method
                // will fail the same way — the trial died before the encoder ran.
                // Skip straight to the next capture method instead of burning a
                // trial per candidate on an error that has nothing to do with them.
                if (captureMethod == CaptureMethod.DesktopDuplication && IsCaptureFailure(trial.FailureReason))
                {
                    _log.Information(
                        "Desktop Duplication capture itself failed; trying the remaining candidates with GDI capture");
                    break;
                }
            }
        }

        throw new EncoderSelectionException(
            "No encoder passed its trial on this machine, with either capture method. Attempts:\n  "
            + string.Join("\n  ", attempts));
    }

    /// <summary>True when a trial's stderr shows the ddagrab capture chain failing —
    /// the signature of a machine whose display driver cannot serve Desktop
    /// Duplication, as opposed to an encoder that does not work.</summary>
    private static bool IsCaptureFailure(string? failureReason) =>
        failureReason is not null
        && (failureReason.Contains("ddagrab", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("D3D11", StringComparison.OrdinalIgnoreCase));

    private static string Describe(CaptureMethod method) =>
        method == CaptureMethod.Gdi ? "GDI capture" : "Desktop Duplication capture";

    private static RecordingPlan WithEncoder(
        RecordingPlan template, string encoderName, SpeedPreset speed, QualityLevel quality, ArrangementPlan arrangement)
    {
        IReadOnlyList<string> encoderArguments = QualityLevels.BuildEncoderArguments(
            encoderName, speed, quality, arrangement.CanvasWidth, arrangement.CanvasHeight, template.FrameRate);
        return template with { Encoder = new EncoderSettings(encoderName, encoderArguments) };
    }
}

/// <summary>The proven selection: the settings a session will run with, whether the
/// encoder is the software fallback tier, whether the answer came from cache, every
/// candidate that failed and why (for the honesty report), the rate the encoder
/// writes at (which drives the disk preflight), and the capture method the proof
/// used — the session must record with the method that passed, not the default.</summary>
public sealed record EncoderSelection(
    EncoderSettings Encoder,
    bool IsSoftware,
    bool FromCache,
    IReadOnlyList<string> RejectedCandidates,
    long BytesPerHour,
    CaptureMethod CaptureMethod = CaptureMethod.DesktopDuplication);

/// <summary>No encoder works at all — recording cannot start (reported loudly,
/// never worked around silently).</summary>
public sealed class EncoderSelectionException(string message) : Exception(message);
