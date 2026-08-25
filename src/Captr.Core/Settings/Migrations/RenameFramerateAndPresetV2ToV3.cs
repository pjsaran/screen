using System.Text.Json.Nodes;

namespace Captr.Core.Settings.Migrations;

/// <summary>
/// Schema 2 → 3. Two keys were renamed to match how the settings are spoken about
/// and labelled in the UI: <c>frameRate</c> became <c>framerate</c> (one word), and
/// <c>speedPreset</c> became <c>preset</c> (unambiguous next to <c>quality</c>).
/// Only the JSON spelling changed; the values carry over untouched. The same version
/// gave hotkeys defaults, so a file recording none is given them here too.
/// </summary>
public sealed class RenameFramerateAndPresetV2ToV3 : ISettingsMigration
{
    public int FromVersion => 2;

    public JsonObject Migrate(JsonObject settings)
    {
        Rename(settings, "frameRate", "framerate");
        Rename(settings, "speedPreset", "preset");
        SupplyDefaultHotkeys(settings);
        return settings;
    }

    /// <summary>
    /// Fills in the default hotkeys where the file records no combination.
    /// </summary>
    /// <remarks>
    /// The model's defaults only apply to keys that are ABSENT. Files written before
    /// hotkeys had defaults contain explicit empty strings, which deserialise as
    /// "deliberately disabled" — so an upgrading user would silently get no hotkeys
    /// at all while a fresh install got two. This makes the two agree. A user who
    /// genuinely wants none can still clear them afterwards; that choice is then
    /// theirs rather than an accident of when they installed.
    /// </remarks>
    private static void SupplyDefaultHotkeys(JsonObject settings)
    {
        if (settings["hotkeys"] is not JsonObject hotkeys)
        {
            return;
        }

        var defaults = new HotkeySettings();
        FillIfBlank(hotkeys, "recordToggle", defaults.RecordToggle);
        FillIfBlank(hotkeys, "pauseToggle", defaults.PauseToggle);
    }

    private static void FillIfBlank(JsonObject hotkeys, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(hotkeys[key]?.GetValue<string>()))
        {
            hotkeys[key] = value;
        }
    }

    /// <summary>Moves a value to its new key. Does nothing when the old key is absent
    /// (the setting was never written) or the new key already exists (a file already
    /// carrying the new spelling must not be clobbered).</summary>
    private static void Rename(JsonObject settings, string oldName, string newName)
    {
        if (settings.ContainsKey(newName) || !settings.Remove(oldName, out JsonNode? value))
        {
            return;
        }

        settings[newName] = value;
    }
}
