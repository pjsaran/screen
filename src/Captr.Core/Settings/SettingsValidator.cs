using Captr.Core.Encoders;
using Captr.Core.Interop;
using Captr.Core.Naming;

namespace Captr.Core.Settings;

/// <summary>
/// Every rule that makes a <see cref="CaptrSettings"/> instance usable, each with a
/// field name and a message telling the user exactly what to fix. Owns settings
/// validity; recording refuses to start while any rule fails (SPEC §8), so a missing
/// rule here means a confusing failure later — and an over-strict rule blocks
/// recording for no reason.
/// </summary>
public static class SettingsValidator
{
    /// <summary>The longest retention accepted: ten years.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>Validates and returns every problem found (not just the first —
    /// the settings page shows them all inline).</summary>
    public static IReadOnlyList<SettingsError> Validate(CaptrSettings settings)
    {
        var errors = new List<SettingsError>();

        if (!CaptureRates.IsSupported(settings.FrameRate))
        {
            errors.Add(new(nameof(settings.FrameRate),
                $"{settings.FrameRate} FPS is not one of the supported frame rates: " +
                $"{string.Join(", ", CaptureRates.All.Select(r => r.FramesPerSecond))}."));
        }

        if (SpeedPresets.Find(settings.SpeedPreset) is null)
        {
            errors.Add(new(nameof(settings.SpeedPreset),
                $"Unknown speed preset '{settings.SpeedPreset}'. Available: " +
                $"{string.Join(", ", SpeedPresets.All.Select(p => p.Name))}."));
        }

        if (QualityLevels.Find(settings.Quality) is null)
        {
            errors.Add(new(nameof(settings.Quality),
                $"Unknown quality level '{settings.Quality}'. Available: " +
                $"{string.Join(", ", QualityLevels.All.Select(q => q.Name))}."));
        }

        if (string.IsNullOrWhiteSpace(settings.WorkingFolder))
        {
            errors.Add(new(nameof(settings.WorkingFolder), "A working folder is required — it is where recordings are written before transfer."));
        }
        else if (!Path.IsPathFullyQualified(settings.WorkingFolder))
        {
            errors.Add(new(nameof(settings.WorkingFolder),
                $"The working folder must be a full path like C:\\Recordings, not '{settings.WorkingFolder}'."));
        }

        if (settings.RetentionDays > MaxRetentionDays)
        {
            // Unbounded, a value like 20000000 passed here and then overflowed the
            // retention arithmetic at the next host start - before the host opened
            // its pipe, so every start timed out until the setting was changed.
            errors.Add(new(nameof(settings.RetentionDays),
                $"Retention can be at most {MaxRetentionDays} days (ten years)."));
        }

        if (settings.RetentionDays < 0)
        {
            errors.Add(new(nameof(settings.RetentionDays), "Retention cannot be negative. Use 0 to allow cleanup as soon as transfer is confirmed."));
        }

        ValidatePattern(settings.OutputPattern, nameof(CaptrSettings.OutputPattern), errors);
        ValidateRetries(settings.Retries, errors);
        ValidateHotkeys(settings.Hotkeys, errors);

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DestinationSettings destination in settings.Destinations)
        {
            ValidateDestination(destination, seenNames, errors);
        }

        return errors;
    }

    private static void ValidatePattern(string pattern, string field, List<SettingsError> errors)
    {
        // Every {token} must be one we support, and any :format after it must both
        // parse and produce a legal file name. Otherwise the problem would only
        // surface when a finished recording is named — the worst possible moment.
        // OutputNamer owns the rules and the wording, so the two cannot drift.
        if (OutputNamer.DescribePatternProblem(pattern) is { } problem)
        {
            errors.Add(new(field, problem));
        }
    }

    /// <summary>
    /// The retry policy has to describe a run that actually terminates and does not
    /// hammer the destination. Zero attempts would mean a transfer that never even
    /// tries; a first delay longer than the ceiling would mean the backoff shrinks.
    /// </summary>
    private static void ValidateRetries(RetrySettings retries, List<SettingsError> errors)
    {
        const string field = nameof(CaptrSettings.Retries);

        if (retries.MaxAttempts < 1)
        {
            errors.Add(new(field, "Automatic attempts must be at least 1 — a transfer has to be tried at least once."));
        }
        else if (retries.MaxAttempts > 50)
        {
            errors.Add(new(field,
                "Automatic attempts above 50 are almost certainly a mistake. A destination that has refused " +
                "50 times needs a person, not another attempt."));
        }

        if (retries.FirstRetrySeconds < 1)
        {
            errors.Add(new(field, "The first retry delay must be at least 1 second."));
        }

        if (retries.MaxRetrySeconds < retries.FirstRetrySeconds)
        {
            errors.Add(new(field,
                $"The longest retry delay ({retries.MaxRetrySeconds}s) cannot be shorter than the first one " +
                $"({retries.FirstRetrySeconds}s)."));
        }
    }

    /// <summary>
    /// Each hotkey must be a combination Captr can register, must not be something a
    /// person types in the ordinary course of things, and the two must differ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There were no hotkey rules at all. A typo saved through the CLI was accepted and
    /// surfaced only as a message box the next time the window opened, and two toggles
    /// set to the same keys were reported as "already in use by another application" —
    /// the other application being Captr itself.
    /// </para>
    /// <para>
    /// None of these rules blocks a recording (<see cref="SettingsError.BlocksRecording"/>
    /// is false). They were added after release, a hotkey has nothing to do with whether
    /// footage can be captured, and a settings file that recorded yesterday must record
    /// today. The file is refused when it is next EDITED, which is when the person can
    /// act on the message.
    /// </para>
    /// </remarks>
    private static void ValidateHotkeys(HotkeySettings hotkeys, List<SettingsError> errors)
    {
        const string field = nameof(CaptrSettings.Hotkeys);

        if (HotkeyCombination.DescribeProblem(hotkeys.RecordToggle) is { } record)
        {
            errors.Add(new(field, "Start/stop recording: " + record) { BlocksRecording = false });
        }

        if (HotkeyCombination.DescribeProblem(hotkeys.PauseToggle) is { } pause)
        {
            errors.Add(new(field, "Pause/resume: " + pause) { BlocksRecording = false });
        }

        if (HotkeyCombination.AreSame(hotkeys.RecordToggle, hotkeys.PauseToggle))
        {
            errors.Add(new(field, "Start/stop and pause/resume can't use the same keys.") { BlocksRecording = false });
        }
    }

    /// <summary>
    /// A destination FOLDER may contain the same tokens as a file name, so a
    /// transfer can land in a dated subfolder. The tokens are checked here for the
    /// same reason names are: a broken one must be caught while the user is typing,
    /// not when a finished recording has nowhere to go.
    /// </summary>
    /// <remarks>
    /// The full-path, stream and ".." rules were added after release. A settings file
    /// saved before them still RECORDS - those errors do not block a start, so an
    /// upgrade can never stop someone recording over a destination - but the file is
    /// refused when edited, Diagnostics reports it, and the transfer itself fails with
    /// the same words instead of writing somewhere unexpected.
    /// </remarks>
    private static void ValidateFolderPattern(string? folder, bool isLocalFolder, string field, List<SettingsError> errors)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (OutputNamer.DescribeFolderPathProblem(folder) is { } problem)
        {
            errors.Add(new(field, problem));
        }
        else if (OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder) is { } placement)
        {
            errors.Add(new(field, placement) { BlocksRecording = false });
        }
    }

    private static void ValidateDestination(
        DestinationSettings destination, HashSet<string> seenNames, List<SettingsError> errors)
    {
        const string field = nameof(CaptrSettings.Destinations);

        if (string.IsNullOrWhiteSpace(destination.Name))
        {
            errors.Add(new(field, "Every destination needs a name — it identifies the destination in the transfer queue."));
        }
        else if (!seenNames.Add(destination.Name))
        {
            errors.Add(new(field, $"Two destinations are named '{destination.Name}'. Names must be unique."));
        }

        // Kinds that are listed but not built must be refused here rather than
        // failing at transfer time, when the recording is already finished.
        if (!DestinationKinds.IsImplemented(destination.Kind))
        {
            DestinationKindInfo info = DestinationKinds.Describe(destination.Kind);
            errors.Add(new(field,
                $"Destination '{destination.Name}' uses {info.DisplayName}, which Captr cannot transfer to yet. " +
                "Choose a local/network folder or SharePoint, or disable the destination."));
        }

        switch (destination.Kind)
        {
            case DestinationKind.Folder when string.IsNullOrWhiteSpace(destination.FolderPath):
                errors.Add(new(field, $"Destination '{destination.Name}' is a folder destination but has no folder path."));
                break;
            case DestinationKind.SharePoint when string.IsNullOrWhiteSpace(destination.SharePointSiteUrl):
                errors.Add(new(field, $"Destination '{destination.Name}' is a SharePoint destination but has no site URL."));
                break;
        }

        if (!string.IsNullOrWhiteSpace(destination.FileNamePattern))
        {
            ValidatePattern(destination.FileNamePattern, field, errors);
        }

        ValidateFolderPattern(destination.FolderPath, destination.Kind == DestinationKind.Folder, field, errors);
        ValidateFolderPattern(destination.SharePointFolder, isLocalFolder: false, field, errors);
    }
}

/// <summary>One validation problem: which field, and what to do about it.</summary>
public sealed record SettingsError(string Field, string Message)
{
    /// <summary>Whether this problem stops a recording from starting. Only rules
    /// added after release set it false (see ValidateFolderPattern and
    /// ValidateHotkeys); everything else blocks, as SPEC §8 asks.</summary>
    public bool BlocksRecording { get; init; } = true;
}
