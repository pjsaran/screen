using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Captr.Core.Common;
using Captr.Core.Settings.Migrations;

namespace Captr.Core.Settings;

/// <summary>
/// Loads and saves <c>settings.json</c> in the user's local application data,
/// beside everything else Captr keeps (see <see cref="DefaultSettingsPath"/> for why
/// local rather than the roaming location SPEC §8 names). Owns the file's location,
/// atomic writes, migration-on-load, and
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
    /// <summary>
    /// How settings are written to disk — and the ONLY way they should ever be
    /// rendered as JSON. Exposed as <see cref="JsonOptions"/> so that
    /// <c>captr settings get</c> prints byte-for-byte what the file contains: enum
    /// members as their names rather than integers, and absent fields omitted rather
    /// than shown as null. Printing with different options produced output that
    /// looked like a different file from the one on disk.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <inheritdoc cref="SerializerOptions"/>
    public static JsonSerializerOptions JsonOptions => SerializerOptions;

    private readonly string _settingsPath;
    private readonly SettingsMigrator _migrator;

    /// <summary>Production store at <c>%LOCALAPPDATA%\Captr\settings.json</c>.</summary>
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
    /// file does not exist. Never throws for anything a FILE can contain — the host
    /// calls this before it can answer anyone, and the window before it can show
    /// anything, so a throw here used to mean no recording and no window at all.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A file that is not valid JSON, or is JSON of the wrong shape (a string
    /// where a number belongs, an unknown destination kind), is kept as
    /// <c>.corrupt</c> and the previous version or the defaults are used.</item>
    /// <item>A file written by a NEWER Captr (after a downgrade) is copied, untouched,
    /// to <c>settings.json.from-schema-N</c> — a name no save ever writes — and the
    /// defaults are used, as the installer's downgrade warning says they will be.
    /// Its fields are never silently dropped: upgrading again and restoring that copy
    /// gets every one of them back.</item>
    /// <item>Explicit <c>null</c>s become the defaults they stand for, instead of
    /// null references somewhere later.</item>
    /// </list>
    /// Whenever any of this happens <see cref="LoadProblem"/> says what and why.
    /// </remarks>
    public CaptrSettings Load()
    {
        LoadProblem = null;
        AdoptSettingsLeftInRoamingByAnOlderCaptr();

        string? json = AtomicFile.ReadOrNull(_settingsPath);

        // The file is gone. Before falling back to defaults — which silently discards
        // the user's working folder, destinations, and hotkeys — look for the previous
        // version kept beside it. Settings should not be recoverable only from a
        // backup somebody remembered to take.
        if (json is null)
        {
            return RecoverFromPreviousVersion("the settings file was missing")
                ?? CaptrSettings.CreateDefault();
        }

        try
        {
            JsonObject document = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Settings root is not a JSON object.");

            int? newerVersion = NewerSchemaVersion(document);
            if (newerVersion is { } version)
            {
                string keptAs = $"{_settingsPath}.from-schema-{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                if (!File.Exists(keptAs))
                {
                    File.Copy(_settingsPath, keptAs);
                }

                LoadProblem =
                    $"settings.json was written by a newer version of Captr (settings version {version}; this one " +
                    $"understands up to {CaptrSettings.CurrentSchemaVersion}), so Captr is using default settings. " +
                    $"Your settings are kept, untouched, in {keptAs} — install the newer Captr again to use them.";
                return CaptrSettings.CreateDefault();
            }

            document = _migrator.MigrateToCurrent(document);
            CaptrSettings settings = document.Deserialize<CaptrSettings>(SerializerOptions)
                ?? throw new JsonException("Settings deserialised to nothing.");
            return WithDefaultsForNulls(settings);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
                                              or NotSupportedException or ArgumentException)
        {
            // Keep the unreadable file for inspection, then try the previous version
            // for the same reason as above.
            File.Copy(_settingsPath, _settingsPath + ".corrupt", overwrite: true);
            CaptrSettings? recovered = RecoverFromPreviousVersion("the settings file could not be parsed");
            LoadProblem =
                $"settings.json could not be read ({exception.Message}). It was kept as settings.json.corrupt and " +
                (recovered is null ? "Captr is using default settings." : "the previous version of your settings was restored.");
            return recovered ?? CaptrSettings.CreateDefault();
        }
    }

    /// <summary>
    /// Set when <see cref="Load"/> could not use the file as it stood, saying what it
    /// did instead and where the original went. Null on a normal load. Shown by
    /// Diagnostics, <c>captr doctor</c>, and the Settings page.
    /// </summary>
    public string? LoadProblem { get; private set; }

    private static int? NewerSchemaVersion(JsonObject document)
    {
        // Read leniently: a malformed version is a corrupt file, handled by the caller.
        int version = document["schemaVersion"]?.GetValue<int>() ?? 1;
        return version > CaptrSettings.CurrentSchemaVersion ? version : null;
    }

    /// <summary>
    /// System.Text.Json honours an explicit <c>null</c> even for members declared
    /// non-nullable, so <c>"destinations": null</c> used to load "successfully" and
    /// fail with a null reference wherever the list was first used.
    /// </summary>
    private static CaptrSettings WithDefaultsForNulls(CaptrSettings settings)
    {
        CaptrSettings defaults = CaptrSettings.CreateDefault();

        // The '??' operands look redundant to the compiler, which believes the
        // declared non-nullability; the file is under no such obligation.
#pragma warning disable IDE0029, IDE0270
        return settings with
        {
            ExcludedDisplayIds = settings.ExcludedDisplayIds?.Where(id => id is not null).ToArray() ?? [],
            Destinations = settings.Destinations?.Where(d => d is not null).ToArray() ?? [],
            Retries = settings.Retries ?? defaults.Retries,
            Hotkeys = settings.Hotkeys is null
                ? defaults.Hotkeys
                : new HotkeySettings
                {
                    RecordToggle = settings.Hotkeys.RecordToggle ?? defaults.Hotkeys.RecordToggle,
                    PauseToggle = settings.Hotkeys.PauseToggle ?? defaults.Hotkeys.PauseToggle,
                },
            WorkingFolder = settings.WorkingFolder ?? defaults.WorkingFolder,
            OutputPattern = settings.OutputPattern ?? defaults.OutputPattern,
            Quality = settings.Quality ?? defaults.Quality,
            SpeedPreset = settings.SpeedPreset ?? defaults.SpeedPreset,
        };
#pragma warning restore IDE0029, IDE0270
    }

    /// <summary>
    /// Earlier versions of Captr kept settings in ROAMING application data. If one of
    /// those files is still there and this location has none, move it across so an
    /// upgrade keeps the user's configuration instead of silently reverting to
    /// defaults. Runs once in practice: after the move there is nothing left to find.
    /// </summary>
    /// <summary>
    /// Restores settings from the previous version kept beside the file, writing it
    /// back into place so the recovery is permanent rather than repeated on every
    /// load. Returns null when there is nothing usable to recover from.
    /// </summary>
    private CaptrSettings? RecoverFromPreviousVersion(string reason)
    {
        string? backup = AtomicFile.ReadPreviousVersionOrNull(_settingsPath);
        if (backup is null)
        {
            return null;
        }

        try
        {
            CaptrSettings? recovered = JsonNode.Parse(backup) is JsonObject document
                ? _migrator.MigrateToCurrent(document).Deserialize<CaptrSettings>(SerializerOptions)
                : null;

            if (recovered is null)
            {
                return null;
            }

            AtomicFile.Write(_settingsPath, backup);
            RecoveredFromPreviousVersion = reason;
            LoadProblem = $"Captr restored the previous version of your settings because {reason}.";
            return WithDefaultsForNulls(recovered);
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException
                                              or InvalidOperationException or FormatException or ArgumentException
                                              or SettingsMigrationException)
        {
            // A backup that will not load is no better than no backup; defaults are
            // valid and safe, and the .bak file stays on disk either way.
            return null;
        }
    }

    /// <summary>
    /// Set when <see cref="Load"/> had to fall back to the previous version, saying
    /// why. Null on a normal load. Surfaced by diagnostics so the user finds out that
    /// something went wrong with their settings rather than silently getting them back.
    /// </summary>
    public string? RecoveredFromPreviousVersion { get; private set; }

    private void AdoptSettingsLeftInRoamingByAnOlderCaptr()
    {
        // Only ever touches the production location. A test store points somewhere
        // else entirely and must not inherit whatever this machine happens to have.
        if (!string.Equals(_settingsPath, DefaultSettingsPath(), StringComparison.OrdinalIgnoreCase)
            || File.Exists(_settingsPath))
        {
            return;
        }

        string legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Captr", "settings.json");

        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Move(legacyPath, _settingsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the old file is not worth failing a start over; the user simply
            // gets defaults, which are valid and safe.
        }
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
        // keepPreviousVersion: the version being replaced is kept as
        // settings.json.bak, at the cost of one rename inside the same atomic
        // operation. Settings are small, they change rarely, and losing them costs a
        // user their working folder, destinations, and hotkeys — a free previous
        // version is worth having. The heartbeat, rewritten every second, does NOT
        // ask for one.
        AtomicFile.Write(
            _settingsPath, JsonSerializer.Serialize(settings, SerializerOptions), keepPreviousVersion: true);
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

    /// <summary>
    /// Where settings live: <c>%LOCALAPPDATA%\Captr\settings.json</c>, beside the
    /// recordings, logs, transfer queue, and encoder cache.
    /// </summary>
    /// <remarks>
    /// SPEC §8 asks for ROAMING application data. Captr deliberately uses local
    /// instead, because most of what these settings contain is bound to this machine
    /// and roaming it is at best useless and at worst confusing: display selections
    /// are EDID identities of physical monitors, the working folder and folder
    /// destinations are absolute paths, and the credential a SharePoint destination
    /// names is DPAPI-bound to this user AND machine, so it cannot follow the file
    /// anyway. Keeping every piece of Captr's state in one place also means a support
    /// bundle, a backup, or a clean-up is one folder rather than two.
    /// Settings export/import remains the supported way to move a configuration
    /// between machines, where the user chooses what applies.
    /// </remarks>
    public static string DefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captr", "settings.json");
}

/// <summary>Raised when settings fail validation; carries every field error so the
/// UI/CLI can show them all at once.</summary>
public sealed class SettingsValidationException(IReadOnlyList<SettingsError> errors)
    : Exception("Settings are invalid: " + string.Join(" | ", errors.Select(e => $"{e.Field}: {e.Message}")))
{
    public IReadOnlyList<SettingsError> Errors { get; } = errors;
}
