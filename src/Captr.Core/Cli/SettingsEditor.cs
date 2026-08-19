using System.Globalization;

using Captr.Core.Settings;

namespace Captr.Core.Cli;

/// <summary>
/// Applies a <c>captr settings set KEY VALUE</c> change to the typed settings
/// model. Owns the key names the CLI accepts and their parsing; a wrong value
/// fails here with a message naming the expected form, before anything is saved.
/// </summary>
public static class SettingsEditor
{
    /// <summary>Applies one change and returns the updated settings (unsaved).</summary>
    public static CaptrSettings Apply(CaptrSettings settings, string key, string value)
    {
        return key.ToLowerInvariant() switch
        {
            "framerate" => settings with { FrameRate = ParseInt(key, value) },
            "qualitypreset" => settings with { QualityPreset = value },
            "qualityoverride" => settings with
            {
                QualityOverride = string.IsNullOrWhiteSpace(value) ? null : ParseInt(key, value),
            },
            "workingfolder" => settings with { WorkingFolder = value },
            "outputpattern" => settings with { OutputPattern = value },
            "retentiondays" => settings with { RetentionDays = ParseInt(key, value) },
            "startminimised" or "startminimized" => settings with { StartMinimised = ParseBool(key, value) },
            "closetotray" => settings with { CloseToTray = ParseBool(key, value) },
            "excludeddisplayids" => settings with
            {
                ExcludedDisplayIds = value.Length == 0
                    ? []
                    : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            },
            _ => throw new ArgumentException(
                $"Unknown setting '{key}'. Available: frameRate, qualityPreset, qualityOverride, workingFolder, " +
                "outputPattern, retentionDays, startMinimised, closeToTray, excludedDisplayIds (semicolon-separated)."),
        };
    }

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not a whole number for setting '{key}'.");

    private static bool ParseBool(string key, string value) =>
        bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not true/false for setting '{key}'.");
}
