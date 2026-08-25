using Captr.Core.Encoders;
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

        if (settings.RetentionDays < 0)
        {
            errors.Add(new(nameof(settings.RetentionDays), "Retention cannot be negative. Use 0 to allow cleanup as soon as transfer is confirmed."));
        }

        ValidatePattern(settings.OutputPattern, nameof(CaptrSettings.OutputPattern), errors);
        ValidateRetries(settings.Retries, errors);

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
    /// A destination FOLDER may contain the same tokens as a file name, so a
    /// transfer can land in a dated subfolder. The tokens are checked here for the
    /// same reason names are: a broken one must be caught while the user is typing,
    /// not when a finished recording has nowhere to go.
    /// </summary>
    private static void ValidateFolderPattern(string? folder, string field, List<SettingsError> errors)
    {
        if (!string.IsNullOrWhiteSpace(folder)
            && OutputNamer.DescribeFolderPathProblem(folder) is { } problem)
        {
            errors.Add(new(field, problem));
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

        ValidateFolderPattern(destination.FolderPath, field, errors);
        ValidateFolderPattern(destination.SharePointFolder, field, errors);
    }
}

/// <summary>One validation problem: which field, and what to do about it.</summary>
public sealed record SettingsError(string Field, string Message);
