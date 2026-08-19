using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Captr.Core.Common;
using Captr.Core.Settings.Migrations;

namespace Captr.Core.Settings;

/// <summary>
/// Loads and saves <c>settings.json</c> in the user's roaming application data
/// (SPEC §8). Owns the file's location, atomic writes, migration-on-load, and
/// validation-on-save. If it fails, the user's configuration is lost or recording
/// starts with settings the user never chose.
/// </summary>
/// <remarks>
/// Rules enforced here rather than trusted to callers:
/// <list type="bullet">
/// <item>Writes go through <see cref="AtomicFile"/> — a crash mid-save can never
/// corrupt settings (SPEC §8: "written atomically").</item>
/// <item>A file from an older Captr is migrated forward before deserialising; a file
/// from a NEWER Captr refuses to load rather than silently dropping fields.</item>
/// <item>Saving validates first; invalid settings never reach disk.</item>
/// <item>An unreadable (corrupt) file is preserved as <c>settings.json.corrupt</c>
/// and defaults are returned — recording must not be blocked by a broken file, but
/// evidence is kept for diagnostics.</item>
/// </list>
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath;
    private readonly SettingsMigrator _migrator;

    /// <summary>Production store at <c>%APPDATA%\Captr\settings.json</c>.</summary>
    public SettingsStore()
        : this(DefaultSettingsPath(), SettingsMigrator.Default)
    {
    }

    /// <summary>Test seam: explicit path and migration chain.</summary>
    public SettingsStore(string settingsPath, SettingsMigrator migrator)
    {
        _settingsPath = settingsPath;
        _migrator = migrator;
    }

    /// <summary>Full path of the settings file this store reads and writes.</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>
    /// Loads settings, migrating an older schema forward. Returns defaults when the
    /// file does not exist. A corrupt file is set aside as <c>.corrupt</c> and
    /// defaults are returned. A file written by a NEWER Captr throws
    /// <see cref="SettingsMigrationException"/> — silently dropping the newer
    /// version's fields would destroy configuration on the next save.
    /// </summary>
    public CaptrSettings Load()
    {
        string? json = AtomicFile.ReadOrNull(_settingsPath);
        if (json is null)
        {
            return CaptrSettings.CreateDefault();
        }

        JsonObject document;
        try
        {
            document = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Settings root is not a JSON object.");
        }
        catch (JsonException)
        {
            File.Copy(_settingsPath, _settingsPath + ".corrupt", overwrite: true);
            return CaptrSettings.CreateDefault();
        }

        document = _migrator.MigrateToCurrent(document);

        CaptrSettings? settings = document.Deserialize<CaptrSettings>(SerializerOptions);
        return settings ?? CaptrSettings.CreateDefault();
    }

    /// <summary>Validates and saves. Throws <see cref="SettingsValidationException"/>
    /// with every problem when the settings are invalid — nothing is written.</summary>
    public void Save(CaptrSettings settings)
    {
        IReadOnlyList<SettingsError> errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        AtomicFile.Write(_settingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    /// <summary>
    /// Serialises settings for export. Credentials are not part of settings (they
    /// live in Windows Credential Manager), and the export SAYS so (SPEC §7:
    /// "settings export omits credentials and says so") — an admin reading the file
    /// must not wonder whether secrets were included.
    /// </summary>
    public static string Export(CaptrSettings settings)
    {
        JsonObject document = JsonSerializer.SerializeToNode(settings, SerializerOptions)!.AsObject();
        document.Insert(0, "_note",
            "Exported by Captr. Credentials are NOT included in exports — they remain in " +
            "Windows Credential Manager on the originating machine and must be re-provisioned " +
            "after import (see: captr auth).");
        return document.ToJsonString(SerializerOptions);
    }

    /// <summary>Parses an exported document (migrating if it came from an older
    /// version), validates it, and returns it. Stored credentials are untouched —
    /// import never deletes or overwrites a credential (SPEC §14: "import preserving
    /// them").</summary>
    public CaptrSettings Import(string exportedJson)
    {
        JsonObject document = JsonNode.Parse(exportedJson) as JsonObject
            ?? throw new JsonException("Imported settings are not a JSON object.");
        document.Remove("_note");

        document = _migrator.MigrateToCurrent(document);
        CaptrSettings settings = document.Deserialize<CaptrSettings>(SerializerOptions)
            ?? throw new JsonException("Imported settings could not be read.");

        IReadOnlyList<SettingsError> errors = SettingsValidator.Validate(settings);
        return errors.Count > 0 ? throw new SettingsValidationException(errors) : settings;
    }

    private static string DefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Captr", "settings.json");
}

/// <summary>Raised when settings fail validation; carries every field error so the
/// UI/CLI can show them all at once.</summary>
public sealed class SettingsValidationException(IReadOnlyList<SettingsError> errors)
    : Exception("Settings are invalid: " + string.Join(" | ", errors.Select(e => $"{e.Field}: {e.Message}")))
{
    public IReadOnlyList<SettingsError> Errors { get; } = errors;
}
