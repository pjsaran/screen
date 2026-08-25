using System.Text.Json.Nodes;

using Captr.Core.Encoders;

namespace Captr.Core.Settings.Migrations;

/// <summary>
/// Schema 1 → 2. Two user-visible changes landed together:
/// <list type="bullet">
///   <item><description>The single <c>qualityPreset</c> (plus its numeric
///   <c>qualityOverride</c>) split into an independent <c>speedPreset</c> — how much
///   CPU the encoder may spend — and <c>quality</c> — how good the picture must
///   look. The old presets bundled the two together, so each one maps to the
///   (speed, quality) pair that best reproduces what it used to do.</description></item>
///   <item><description>The three hotkeys (start, pause, stop) became two toggles.
///   The old start key becomes the record toggle and the old pause key becomes the
///   pause toggle; the old stop key has nowhere to go, because the record toggle now
///   does its job.</description></item>
/// </list>
/// </summary>
public sealed class SplitQualityAndToggleHotkeysV1ToV2 : ISettingsMigration
{
    public int FromVersion => 1;

    /// <summary>
    /// Old preset → new (speed, quality). "archival" was visually lossless, so it
    /// becomes Maximum rather than Lossless: true CRF 0 would be a large, unrequested
    /// jump in file size for someone who is merely upgrading.
    /// </summary>
    private static readonly Dictionary<string, (string Speed, string Quality)> PresetMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["archival"] = ("faster", "maximum"),
            ["sharp-text"] = ("veryfast", "high"),
            ["balanced"] = ("veryfast", "balanced"),
            ["compact"] = ("ultrafast", "compact"),
        };

    public JsonObject Migrate(JsonObject settings)
    {
        string oldPreset = settings["qualityPreset"]?.GetValue<string>() ?? "sharp-text";
        (string speed, string quality) = PresetMap.TryGetValue(oldPreset, out (string, string) mapped)
            ? mapped
            : (SpeedPresets.DefaultName, QualityLevels.DefaultName);

        settings.Remove("qualityPreset");
        settings.Remove("qualityOverride");
        settings["speedPreset"] = speed;
        settings["quality"] = quality;

        if (settings["hotkeys"] is JsonObject hotkeys)
        {
            string start = hotkeys["start"]?.GetValue<string>() ?? string.Empty;
            string pause = hotkeys["pause"]?.GetValue<string>() ?? string.Empty;

            hotkeys.Remove("start");
            hotkeys.Remove("pause");
            hotkeys.Remove("stop");
            hotkeys["recordToggle"] = start;
            hotkeys["pauseToggle"] = pause;
        }

        return settings;
    }
}
