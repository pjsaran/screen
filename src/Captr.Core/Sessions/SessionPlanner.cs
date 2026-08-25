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
    /// <param name="qualityOverride">CLI/UI per-session quality-level override, if any.</param>
    /// <param name="speedPresetOverride">CLI/UI per-session speed-preset override, if any.</param>
    public async Task<(RecordingSession.SessionContext Context, SessionStarted StartEvent)> PlanAsync(
        CaptrSettings settings,
        int? frameRateOverride,
        string? qualityOverride,
        string? speedPresetOverride,
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

        string qualityName = qualityOverride ?? settings.Quality;
        QualityLevel quality = QualityLevels.Find(qualityName)
            ?? throw new SessionStartException(
                $"Unknown quality level '{qualityName}'. " +
                $"Available: {string.Join(", ", QualityLevels.All.Select(q => q.Name))}.");

        string speedName = speedPresetOverride ?? settings.SpeedPreset;
        SpeedPreset speed = SpeedPresets.Find(speedName)
            ?? throw new SessionStartException(
                $"Unknown speed preset '{speedName}'. " +
                $"Available: {string.Join(", ", SpeedPresets.All.Select(p => p.Name))}.");

        var template = new RecordingPlan
        {
            SessionId = sessionId,
            Sources = [.. selection.Included.Select(d =>
                new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height, d.VirtualX, d.VirtualY))],
            FrameRate = frameRate,
            Encoder = new EncoderSettings("placeholder", []),
            OverlayText = null,
            WorkingFolder = workingFolder,
        };

        // 4. Prove an encoder and learn its rate (SPEC §5/§6). Both come from the same
        //    step: on the first recording for this machine and these settings it runs
        //    one trial encode; afterwards it is a cache read, which is what makes
        //    pressing Record feel instant.
        string ffmpegPath = FfmpegLocator.FindFfmpeg();
        string ffprobePath = FfmpegLocator.FindFfprobe();

        // The SAME identity HealthReport uses when it re-derives the fingerprint for
        // the Diagnostics "Encoder" check. These two must agree or the check can
        // never find the cache — which is exactly what happened when this read the
        // four-part assembly version ("0.1.1.0") while diagnostics used the semantic
        // one ("0.1.1"): every recording proved an encoder and the page still said
        // "not measured yet". The informational version also carries the commit, so
        // a rebuilt app re-proves rather than trusting a cache it did not write.
        string appVersion = Captr.Core.Common.BuildInfo.Current().InformationalVersion;

        var encoderCache = new EncoderCache();
        var selector = new EncoderSelector(ffmpegPath, ffprobePath, encoderCache, _log);
        EncoderSelection encoderSelection;
        try
        {
            encoderSelection = await selector.SelectAsync(
                template, speed, quality, appVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (EncoderSelectionException exception)
        {
            throw new SessionStartException("No working encoder was found on this machine.\n" + exception.Message);
        }

        // The capture method travels with the encoder: whichever combination passed
        // the trial is the combination the recording runs — on a virtual desktop
        // that means GDI capture, chosen automatically.
        RecordingPlan plan = template with
        {
            Encoder = encoderSelection.Encoder,
            CaptureMethod = encoderSelection.CaptureMethod,
        };

        // 5. Disk preflight against the MEASURED rate (SPEC §5/§6) — the rate the
        // encoder trial actually wrote at on this canvas, never a hardcoded table.
        var diskGuard = new DiskGuard(workingFolder, encoderSelection.BytesPerHour);
        PreflightResult preflight = diskGuard.Preflight(
            new DriveInfo(Path.GetPathRoot(settings.WorkingFolder)!).AvailableFreeSpace);
        if (!preflight.CanStart)
        {
            throw new SessionStartException(preflight.RefusalMessage!);
        }

        // 6. Software fallback arguments (SPEC §6's one fallback), when available
        //    and when the selected encoder is itself hardware. The fallback encoder
        //    is whichever software tier the shipped build carries — libx264 when the
        //    GPL build is pinned, otherwise libopenh264.
        IReadOnlyList<string>? fallback = null;
        FfmpegCapabilities capabilities = FfmpegCapabilities.LoadFrom(ffmpegPath);
        if (!encoderSelection.IsSoftware && EncoderCatalog.SoftwareFallback(capabilities) is { } softwareEncoder)
        {
            ArrangementPlan arrangement = ArrangementPlanner.Plan(plan.Sources);
            fallback = FfmpegArgumentBuilder.Build(plan with
            {
                Encoder = new EncoderSettings(softwareEncoder, QualityLevels.BuildEncoderArguments(
                    softwareEncoder, speed, quality, arrangement.CanvasWidth, arrangement.CanvasHeight, frameRate)),
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
            Quality = quality.Name,
            SpeedPreset = speed.Name,
            EncoderArguments = FfmpegArgumentBuilder.Build(plan),
            WorkingFolder = workingFolder,
            Label = label,
        };

        var context = new RecordingSession.SessionContext(
            sessionId, workingFolder, ffmpegPath, ffprobePath, plan, fallback,
            encoderSelection.BytesPerHour, settings.ExcludedDisplayIds, quality.Name, speed.Name,
            encoderSelection.FromCache ? encoderCache : null);

        return (context, startEvent);
    }
}

/// <summary>Recording cannot start; the message is the complete user-facing
/// explanation of why and what to do (SPEC §8: "a specific message").</summary>
public sealed class SessionStartException(string message) : Exception(message);
