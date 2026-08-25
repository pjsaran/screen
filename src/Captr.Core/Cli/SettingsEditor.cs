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

            // "preset" is the name in settings.json; "speedPreset" was its name in
            // schema 2 and still works so an existing script keeps running.
            "preset" or "speedpreset" => settings with { SpeedPreset = value },
            "quality" => settings with { Quality = value },
            "workingfolder" => settings with { WorkingFolder = value },
            "outputpattern" => settings with { OutputPattern = value },
            "retentiondays" => settings with { RetentionDays = ParseInt(key, value) },
            "startminimised" or "startminimized" => settings with { StartMinimised = ParseBool(key, value) },
            "closetotray" => settings with { CloseToTray = ParseBool(key, value) },
            "minimisewhilerecording" or "minimizewhilerecording" => settings with
            {
                MinimiseWhileRecording = ParseBool(key, value),
            },
            "maxattempts" => settings with { Retries = settings.Retries with { MaxAttempts = ParseInt(key, value) } },
            "firstretryseconds" => settings with
            {
                Retries = settings.Retries with { FirstRetrySeconds = ParseInt(key, value) },
            },
            "maxretryseconds" => settings with
            {
                Retries = settings.Retries with { MaxRetrySeconds = ParseInt(key, value) },
            },
            "recordtogglehotkey" => settings with { Hotkeys = settings.Hotkeys with { RecordToggle = value } },
            "pausetogglehotkey" => settings with { Hotkeys = settings.Hotkeys with { PauseToggle = value } },
            "excludeddisplayids" => settings with
            {
                ExcludedDisplayIds = value.Length == 0
                    ? []
                    : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            },
            _ => throw new ArgumentException(
                $"Unknown setting '{key}'. Available: framerate, preset, quality, workingFolder, outputPattern, " +
                "retentionDays, maxAttempts, firstRetrySeconds, maxRetrySeconds, startMinimised, closeToTray, " +
                "minimiseWhileRecording, recordToggleHotkey, pauseToggleHotkey, " +
                "excludedDisplayIds (semicolon-separated)."),
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
