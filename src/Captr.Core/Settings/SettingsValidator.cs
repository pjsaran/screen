using System.Text.RegularExpressions;

using Captr.Core.Naming;

namespace Captr.Core.Settings;

/// <summary>
/// Every rule that makes a <see cref="CaptrSettings"/> instance usable, each with a
/// field name and a message telling the user exactly what to fix. Owns settings
/// validity; recording refuses to start while any rule fails (SPEC §8), so a missing
/// rule here means a confusing failure later — and an over-strict rule blocks
/// recording for no reason.
/// </summary>
public static partial class SettingsValidator
{
    /// <summary>Validates and returns every problem found (not just the first —
    /// the settings page shows them all inline).</summary>
    public static IReadOnlyList<SettingsError> Validate(CaptrSettings settings)
    {
        var errors = new List<SettingsError>();

        if (settings.FrameRate is < 1 or > 60)
        {
            errors.Add(new(nameof(settings.FrameRate),
                $"Frame rate must be between 1 and 60 (currently {settings.FrameRate}). Screen recording rarely benefits from more than 30."));
        }

        if (settings.QualityOverride is { } qp and (< 0 or > 51))
        {
            errors.Add(new(nameof(settings.QualityOverride),
                $"Quality override must be between 0 (visually lossless, huge) and 51 (unusable), or empty to use the preset (currently {qp})."));
        }

        if (string.IsNullOrWhiteSpace(settings.WorkingFolder))
        {
            errors.Add(new(nameof(settings.WorkingFolder), "A working folder is required — it is where recordings are written before delivery."));
        }
        else if (!Path.IsPathFullyQualified(settings.WorkingFolder))
        {
            errors.Add(new(nameof(settings.WorkingFolder),
                $"The working folder must be a full path like C:\\Recordings, not '{settings.WorkingFolder}'."));
        }

        if (settings.RetentionDays < 0)
        {
            errors.Add(new(nameof(settings.RetentionDays), "Retention cannot be negative. Use 0 to allow cleanup as soon as delivery is confirmed."));
        }

        ValidatePattern(settings.OutputPattern, errors);

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DestinationSettings destination in settings.Destinations)
        {
            ValidateDestination(destination, seenNames, errors);
        }

        return errors;
    }

    private static void ValidatePattern(string pattern, List<SettingsError> errors)
    {
        // Any {token} in the pattern must be one we support, otherwise it would land
        // literally in every file name and the user would only notice afterwards.
        foreach (Match match in TokenPattern().Matches(pattern))
        {
            if (!OutputNamer.SupportedTokens.Contains(match.Value, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new(nameof(CaptrSettings.OutputPattern),
                    $"Unknown token {match.Value} in the naming pattern. Supported: {string.Join(", ", OutputNamer.SupportedTokens)}."));
            }
        }
    }

    private static void ValidateDestination(
        DestinationSettings destination, HashSet<string> seenNames, List<SettingsError> errors)
    {
        const string field = nameof(CaptrSettings.Destinations);

        if (string.IsNullOrWhiteSpace(destination.Name))
        {
            errors.Add(new(field, "Every destination needs a name — it identifies the destination in the delivery queue."));
        }
        else if (!seenNames.Add(destination.Name))
        {
            errors.Add(new(field, $"Two destinations are named '{destination.Name}'. Names must be unique."));
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
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TokenPattern();
}

/// <summary>One validation problem: which field, and what to do about it.</summary>
public sealed record SettingsError(string Field, string Message);
