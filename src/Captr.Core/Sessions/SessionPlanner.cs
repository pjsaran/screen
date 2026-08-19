using System.Globalization;

using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Settings;
using Captr.Core.Supervision;

using Serilog;

namespace Captr.Core.Sessions;

/// <summary>
/// Everything that must be true BEFORE a recording session may exist: settings
/// valid, at least one display included, an encoder proven by trial, and enough
/// disk for the measured rate plus headroom. Owns the start-refusal messages —
/// every "cannot start" the user ever sees originates here, so each one says
/// exactly what to fix.
/// </summary>
public sealed class SessionPlanner
{
    private readonly ILogger _log;

    public SessionPlanner(ILogger log) => _log = log;

    /// <summary>
    /// Plans a session or throws <see cref="SessionStartException"/> with the
    /// user-facing reason. On success, returns the context plus the ready-made
    /// SessionStarted journal event.
    /// </summary>
    /// <param name="settings">Validated settings (the caller loads them).</param>
    /// <param name="frameRateOverride">CLI/UI per-session override, if any.</param>
    /// <param name="qualityPresetOverride">CLI/UI per-session override, if any.</param>
    public async Task<(RecordingSession.SessionContext Context, SessionStarted StartEvent)> PlanAsync(
        CaptrSettings settings,
        int? frameRateOverride,
        string? qualityPresetOverride,
        string? label,
        CancellationToken cancellationToken)
    {
        // 1. Settings must be valid (SPEC §8: block starting with a specific message).
        IReadOnlyList<SettingsError> errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new SessionStartException(
                "Settings are invalid:\n  " + string.Join("\n  ", errors.Select(e => $"{e.Field}: {e.Message}")));
        }

        // 2. Resolve displays by stable identity (SPEC §5).
        var seenStore = new SeenDisplaysStore();
        IReadOnlyList<DisplayInfo> attached = new DisplayEnumerator().Enumerate();
        ResolvedSelection selection = DisplaySelection.Resolve(attached, settings.ExcludedDisplayIds, seenStore.Load());
        seenStore.MarkSeen(attached);

        if (!selection.CanStartRecording)
        {
            throw new SessionStartException(
                "Every display is deselected — there is nothing to record. Re-enable at least one display in settings.");
        }

        foreach (DisplayInfo newDisplay in selection.NewlyAttached)
        {
            _log.Information("New display detected and included by default: {Name} (display {Number})",
                newDisplay.FriendlyName, newDisplay.WindowsDisplayNumber);
        }

        // 3. Working folder for this session.
        var sessionId = Guid.NewGuid();
        string workingFolder = Path.Combine(
            settings.WorkingFolder,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + sessionId.ToString("N")[..8]);

        int frameRate = frameRateOverride ?? settings.FrameRate;
        QualityPreset preset = QualityPresets.Find(qualityPresetOverride ?? settings.QualityPreset)
            ?? throw new SessionStartException(
                $"Unknown quality preset '{qualityPresetOverride ?? settings.QualityPreset}'. " +
                $"Available: {string.Join(", ", QualityPresets.All.Select(p => p.Name))}.");

        var template = new RecordingPlan
        {
            SessionId = sessionId,
            Sources = [.. selection.Included.Select(d => new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height))],
            FrameRate = frameRate,
            Encoder = new EncoderSettings("placeholder", []),
            OverlayText = null,
            WorkingFolder = workingFolder,
        };

        // 4. Prove an encoder (SPEC §5) and measure the rate (SPEC §6).
        string ffmpegPath = FfmpegLocator.FindFfmpeg();
        string ffprobePath = FfmpegLocator.FindFfprobe();
        string appVersion = typeof(SessionPlanner).Assembly.GetName().Version?.ToString() ?? "0";

        var selector = new EncoderSelector(ffmpegPath, ffprobePath, new EncoderCache(), _log);
        EncoderSelection encoderSelection;
        try
        {
            encoderSelection = await selector.SelectAsync(
                template, preset, settings.QualityOverride, appVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (EncoderSelectionException exception)
        {
            throw new SessionStartException("No working encoder was found on this machine.\n" + exception.Message);
        }

        RecordingPlan plan = template with { Encoder = encoderSelection.Encoder };
        SizeEstimate estimate = await SizeEstimator.MeasureAsync(ffmpegPath, ffprobePath, plan, cancellationToken)
            .ConfigureAwait(false);

        // 5. Disk preflight against the measured rate (SPEC §6).
        var diskGuard = new DiskGuard(workingFolder, estimate.BytesPerHour);
        PreflightResult preflight = diskGuard.Preflight(
            new DriveInfo(Path.GetPathRoot(settings.WorkingFolder)!).AvailableFreeSpace);
        if (!preflight.CanStart)
        {
            throw new SessionStartException(preflight.RefusalMessage!);
        }

        // 6. Software fallback arguments (SPEC §6's one fallback), when available
        //    and when the selected encoder is itself hardware.
        IReadOnlyList<string>? fallback = null;
        FfmpegCapabilities capabilities = FfmpegCapabilities.LoadFrom(ffmpegPath);
        if (!encoderSelection.IsSoftware && capabilities.HasOpenH264)
        {
            ArrangementPlan arrangement = ArrangementPlanner.Plan(plan.Sources);
            fallback = FfmpegArgumentBuilder.Build(plan with
            {
                Encoder = new EncoderSettings("libopenh264", QualityPresets.BuildQualityArguments(
                    "libopenh264", preset, null, arrangement.CanvasWidth, arrangement.CanvasHeight, frameRate)),
            });
        }

        ArrangementPlan finalArrangement = ArrangementPlanner.Plan(plan.Sources);
        var startEvent = new SessionStarted
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = sessionId,
            LocalTimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            AppVersion = appVersion,
            FfmpegBuildId = capabilities.BuildId,
            Displays =
            [
                .. selection.Included.Select(d => new RecordedDisplay
                {
                    StableId = d.StableId,
                    WindowsDisplayNumber = d.WindowsDisplayNumber,
                    Width = d.Width,
                    Height = d.Height,
                }),
            ],
            CanvasWidth = finalArrangement.CanvasWidth,
            CanvasHeight = finalArrangement.CanvasHeight,
            FrameRate = frameRate,
            EncoderName = encoderSelection.Encoder.CodecName,
            QualityPreset = preset.Name,
            EncoderArguments = FfmpegArgumentBuilder.Build(plan),
            WorkingFolder = workingFolder,
            Label = label,
        };

        var context = new RecordingSession.SessionContext(
            sessionId, workingFolder, ffmpegPath, ffprobePath, plan, fallback,
            estimate.BytesPerHour, settings.ExcludedDisplayIds);

        return (context, startEvent);
    }
}

/// <summary>Recording cannot start; the message is the complete user-facing
/// explanation of why and what to do (SPEC §8: "a specific message").</summary>
public sealed class SessionStartException(string message) : Exception(message);
