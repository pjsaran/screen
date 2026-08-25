using System.Text.Json.Nodes;

namespace Captr.Core.Settings.Migrations;

/// <summary>
/// One forward step of the settings schema: takes a settings document at
/// <see cref="FromVersion"/> and returns it at <c>FromVersion + 1</c>. Implementations
/// live in this folder, one class per step, named after what changed and which step
/// it is (e.g. <c>SplitQualityAndToggleHotkeysV1ToV2</c>).
/// </summary>
public interface ISettingsMigration
{
    /// <summary>The schema version this migration upgrades FROM.</summary>
    int FromVersion { get; }

    /// <summary>Transforms the raw JSON document in place and returns it. Work on the
    /// JSON, not the typed model — the whole point is that the old shape no longer
    /// matches the current <see cref="CaptrSettings"/>.</summary>
    JsonObject Migrate(JsonObject settings);
}

/// <summary>
/// Runs settings documents forward through every registered migration until they
/// reach <see cref="CaptrSettings.CurrentSchemaVersion"/> (SPEC §8: versioned with
/// forward migration). Owns the ordering and the version bookkeeping so individual
/// migrations stay single-purpose. If it fails, a user upgrading Captr loses their
/// configuration — which is why upgrades are fixture-tested against real old files.
/// </summary>
public sealed class SettingsMigrator
{
    private readonly Dictionary<int, ISettingsMigration> _byFromVersion;

    /// <summary>The production migration chain. Add new migrations here AND to the
    /// fixture tests in <c>tests/Captr.Core.Tests/Settings/</c>.</summary>
    public static SettingsMigrator Default { get; } = new(
        new SplitQualityAndToggleHotkeysV1ToV2(),
        new RenameFramerateAndPresetV2ToV3());

    public SettingsMigrator(params IReadOnlyList<ISettingsMigration> migrations)
    {
        _byFromVersion = migrations.ToDictionary(m => m.FromVersion);
    }

    /// <summary>
    /// Migrates a raw settings document to the current schema version. Throws
    /// <see cref="SettingsMigrationException"/> when the document is newer than this
    /// build understands, or when a step in the chain is missing — both mean the file
    /// must not be rewritten, or data would be destroyed.
    /// </summary>
    public JsonObject MigrateToCurrent(JsonObject settings)
    {
        int version = settings["schemaVersion"]?.GetValue<int>() ?? 1;

        if (version > CaptrSettings.CurrentSchemaVersion)
        {
            throw new SettingsMigrationException(
                $"settings.json is schema version {version}, but this build understands up to " +
                $"{CaptrSettings.CurrentSchemaVersion}. It was written by a newer Captr — " +
                "upgrade the application instead of downgrading the file.");
        }

        while (version < CaptrSettings.CurrentSchemaVersion)
        {
            if (!_byFromVersion.TryGetValue(version, out ISettingsMigration? migration))
            {
                throw new SettingsMigrationException(
                    $"No migration is registered from schema version {version}. " +
                    "This is a bug: every version bump must ship its migration.");
            }

            settings = migration.Migrate(settings);
            version++;
            settings["schemaVersion"] = version;
        }

        return settings;
    }
}

/// <summary>Raised when settings cannot be migrated; the caller must leave the file
/// untouched and surface the message.</summary>
public sealed class SettingsMigrationException(string message) : Exception(message);
