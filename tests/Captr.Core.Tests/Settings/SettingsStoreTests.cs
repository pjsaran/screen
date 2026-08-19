using System.Text.Json.Nodes;

using Captr.Core.Settings;
using Captr.Core.Settings.Migrations;

using Shouldly;

namespace Captr.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-settings-").FullName;

    private SettingsStore MakeStore(SettingsMigrator? migrator = null) =>
        new(Path.Combine(_dir, "settings.json"), migrator ?? SettingsMigrator.Default);

    [Fact]
    public void Loading_with_no_file_returns_valid_defaults()
    {
        CaptrSettings settings = MakeStore().Load();

        settings.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
        SettingsValidator.Validate(settings).ShouldBeEmpty();
    }

    [Fact]
    public void Saved_settings_load_back_identically()
    {
        SettingsStore store = MakeStore();
        CaptrSettings original = CaptrSettings.CreateDefault() with
        {
            FrameRate = 20,
            ExcludedDisplayIds = [@"\\?\DISPLAY#ABC123#4&2c34d13&0"],
            QualityOverride = 24,
            Destinations =
            [
                new DestinationSettings { Name = "archive", Kind = DestinationKind.Folder, FolderPath = @"C:\archive" },
                new DestinationSettings
                {
                    Name = "sp",
                    Kind = DestinationKind.SharePoint,
                    SharePointSiteUrl = "https://contoso.sharepoint.com/sites/rec",
                    SharePointFolder = "Recordings",
                    CredentialName = "captr-sp",
                },
            ],
            Hotkeys = new HotkeySettings { Start = "Ctrl+Alt+F9" },
        };

        store.Save(original);
        CaptrSettings loaded = store.Load();

        loaded.ShouldBe(original with
        {
            // Records with collection properties compare by reference; compare fields.
            ExcludedDisplayIds = loaded.ExcludedDisplayIds,
            Destinations = loaded.Destinations,
        });
        loaded.ExcludedDisplayIds.ShouldBe(original.ExcludedDisplayIds);
        loaded.Destinations.ShouldBe(original.Destinations);
    }

    [Fact]
    public void Invalid_settings_are_rejected_and_nothing_is_written()
    {
        SettingsStore store = MakeStore();
        CaptrSettings invalid = CaptrSettings.CreateDefault() with { FrameRate = 0 };

        var exception = Should.Throw<SettingsValidationException>(() => store.Save(invalid));

        exception.Errors.ShouldContain(e => e.Field == nameof(CaptrSettings.FrameRate));
        File.Exists(store.SettingsPath).ShouldBeFalse();
    }

    [Fact]
    public void A_corrupt_file_is_preserved_as_evidence_and_defaults_are_returned()
    {
        SettingsStore store = MakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, "{ this is not json");

        CaptrSettings settings = store.Load();

        settings.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
        File.Exists(store.SettingsPath + ".corrupt").ShouldBeTrue();
    }

    [Fact]
    public void A_file_from_a_newer_captr_refuses_to_load_rather_than_dropping_fields()
    {
        SettingsStore store = MakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, $$"""{"schemaVersion": {{CaptrSettings.CurrentSchemaVersion + 1}} }""");

        Should.Throw<SettingsMigrationException>(() => store.Load())
            .Message.ShouldContain("newer");
    }

    // SPEC §14: "schema migration from prior-version fixtures". The chain machinery is
    // proven with a synthetic version-0 fixture; each real schema bump adds its real
    // fixture file beside this test.
    [Fact]
    public void An_old_schema_file_is_migrated_forward_on_load()
    {
        var migrator = new SettingsMigrator(new FakeV0ToV1());
        SettingsStore store = MakeStore(migrator);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """{"schemaVersion": 0, "fps": 24}""");

        CaptrSettings settings = store.Load();

        settings.FrameRate.ShouldBe(24);
        settings.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void A_missing_migration_step_fails_loudly_instead_of_guessing()
    {
        var migrator = new SettingsMigrator(); // empty chain
        SettingsStore store = MakeStore(migrator);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """{"schemaVersion": 0}""");

        Should.Throw<SettingsMigrationException>(() => store.Load());
    }

    [Fact]
    public void Export_states_that_credentials_are_not_included()
    {
        SettingsStore store = MakeStore();
        string exported = SettingsStore.Export(CaptrSettings.CreateDefault());

        exported.ShouldContain("Credentials are NOT included");
    }

    [Fact]
    public void Exported_settings_import_back_identically()
    {
        SettingsStore store = MakeStore();
        CaptrSettings original = CaptrSettings.CreateDefault() with { FrameRate = 25 };

        CaptrSettings imported = store.Import(SettingsStore.Export(original));

        imported.FrameRate.ShouldBe(25);
    }

    [Fact]
    public void Importing_invalid_settings_is_rejected()
    {
        SettingsStore store = MakeStore();
        string exported = SettingsStore.Export(CaptrSettings.CreateDefault() with { FrameRate = 20 });
        string tampered = exported.Replace("\"frameRate\": 20", "\"frameRate\": 500");

        Should.Throw<SettingsValidationException>(() => store.Import(tampered));
    }

    /// <summary>Synthetic migration: version 0 stored the frame rate as "fps".</summary>
    private sealed class FakeV0ToV1 : ISettingsMigration
    {
        public int FromVersion => 0;

        public JsonObject Migrate(JsonObject settings)
        {
            if (settings.Remove("fps", out JsonNode? fps))
            {
                settings["frameRate"] = fps;
            }

            return settings;
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
