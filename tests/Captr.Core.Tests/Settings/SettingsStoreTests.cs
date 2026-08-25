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

    // ---- What the installer relies on -------------------------------------------
    //
    // `captr settings init` writes a default file on a fresh machine and NEVER
    // overwrites an existing one, so an upgrade keeps the user's configuration. The
    // CLI verb is a thin wrapper over these two properties.

    [Fact]
    public void A_default_configuration_is_valid_and_carries_the_current_schema()
    {
        CaptrSettings defaults = CaptrSettings.CreateDefault();

        defaults.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
        SettingsValidator.Validate(defaults).ShouldBeEmpty();

        // The installer saves exactly this, so it has to survive a round trip.
        // Compared field by field rather than with record equality: a record's
        // generated Equals compares IReadOnlyList members by REFERENCE, so an
        // identical configuration fails the comparison purely because one side
        // deserialised into a List and the other was built as an array.
        SettingsStore store = MakeStore();
        store.Save(defaults);

        CaptrSettings reloaded = MakeStore().Load();
        reloaded.SchemaVersion.ShouldBe(defaults.SchemaVersion);
        reloaded.FrameRate.ShouldBe(defaults.FrameRate);
        reloaded.SpeedPreset.ShouldBe(defaults.SpeedPreset);
        reloaded.Quality.ShouldBe(defaults.Quality);
        reloaded.WorkingFolder.ShouldBe(defaults.WorkingFolder);
        reloaded.OutputPattern.ShouldBe(defaults.OutputPattern);
        reloaded.RetentionDays.ShouldBe(defaults.RetentionDays);
        reloaded.Retries.ShouldBe(defaults.Retries);
        reloaded.Hotkeys.ShouldBe(defaults.Hotkeys);
        reloaded.Destinations.ShouldBeEmpty();
        reloaded.ExcludedDisplayIds.ShouldBeEmpty();
    }

    [Fact]
    public void A_default_configuration_with_a_preset_working_folder_is_still_valid()
    {
        // The installer's /WORKINGFOLDER switch produces exactly this.
        CaptrSettings settings = CaptrSettings.CreateDefault() with { WorkingFolder = @"D:\Recordings" };

        SettingsValidator.Validate(settings).ShouldBeEmpty();
    }

    // ---- Recovering settings that went missing or unreadable --------------------
    //
    // Losing settings.json costs a user their working folder, their destinations, and
    // their hotkeys, and the failure is silent: Captr simply starts with defaults and
    // records into a different place. Every save therefore leaves the version it
    // replaced beside it, and a load that finds nothing usable reaches for it.

    [Fact]
    public void A_save_keeps_the_version_it_replaced()
    {
        SettingsStore store = MakeStore();
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 7 });
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 9 });

        File.Exists(Path.Combine(_dir, "settings.json.bak"))
            .ShouldBeTrue("the previous version should be kept beside the file");
    }

    [Fact]
    public void The_first_ever_save_has_no_previous_version_to_keep()
    {
        MakeStore().Save(CaptrSettings.CreateDefault() with { RetentionDays = 7 });

        File.Exists(Path.Combine(_dir, "settings.json.bak"))
            .ShouldBeFalse("there was nothing to replace, so there is nothing to keep");
    }

    [Fact]
    public void Settings_that_go_missing_are_recovered_from_the_previous_version()
    {
        SettingsStore store = MakeStore();
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 7, WorkingFolder = @"D:\Recordings" });
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 9, WorkingFolder = @"D:\Recordings" });

        // Whatever the cause — a stray delete, a half-finished uninstall, a test that
        // trampled it — the file is simply not there any more.
        File.Delete(Path.Combine(_dir, "settings.json"));

        SettingsStore reopened = MakeStore();
        CaptrSettings recovered = reopened.Load();

        recovered.WorkingFolder.ShouldBe(@"D:\Recordings");
        reopened.RecoveredFromPreviousVersion.ShouldNotBeNull().ShouldContain("missing");

        // The recovery is written back, so it happens once rather than on every load.
        File.Exists(Path.Combine(_dir, "settings.json")).ShouldBeTrue();
        MakeStore().Load().WorkingFolder.ShouldBe(@"D:\Recordings");
    }

    [Fact]
    public void Settings_that_will_not_parse_are_recovered_from_the_previous_version()
    {
        SettingsStore store = MakeStore();
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 7 });
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 9 });

        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ this is not json");

        SettingsStore reopened = MakeStore();
        reopened.Load().RetentionDays.ShouldBe(7, "the version before the damaged one");
        reopened.RecoveredFromPreviousVersion.ShouldNotBeNull().ShouldContain("parsed");

        // The unreadable file is kept for inspection rather than thrown away.
        File.Exists(Path.Combine(_dir, "settings.json.corrupt")).ShouldBeTrue();
    }

    [Fact]
    public void With_no_usable_previous_version_the_defaults_are_used_and_nothing_is_claimed()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ broken");
        File.WriteAllText(Path.Combine(_dir, "settings.json.bak"), "also broken");

        SettingsStore store = MakeStore();
        CaptrSettings settings = store.Load();

        SettingsValidator.Validate(settings).ShouldBeEmpty();
        store.RecoveredFromPreviousVersion.ShouldBeNull("nothing was recovered, so nothing should say it was");
    }

    [Fact]
    public void A_normal_load_does_not_claim_to_have_recovered_anything()
    {
        SettingsStore store = MakeStore();
        store.Save(CaptrSettings.CreateDefault() with { RetentionDays = 7 });

        SettingsStore reopened = MakeStore();
        reopened.Load().RetentionDays.ShouldBe(7);
        reopened.RecoveredFromPreviousVersion.ShouldBeNull();
    }

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
            Quality = "high",
            SpeedPreset = "faster",
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
            Hotkeys = new HotkeySettings { RecordToggle = "Ctrl+Alt+F9", PauseToggle = "Ctrl+Alt+F10" },
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
    // fixture beside this test.
    [Fact]
    public void An_old_schema_file_is_migrated_forward_on_load()
    {
        var migrator = new SettingsMigrator(
            new FakeV0ToV1(),
            new SplitQualityAndToggleHotkeysV1ToV2(),
            new RenameFramerateAndPresetV2ToV3());
        SettingsStore store = MakeStore(migrator);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """{"schemaVersion": 0, "fps": 24}""");

        CaptrSettings settings = store.Load();

        settings.FrameRate.ShouldBe(24);
        settings.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
    }

    // The REAL v1 fixture: a settings file written by the previous Captr, with the
    // single quality preset and three separate hotkeys. Losing someone's
    // configuration on upgrade is the failure this test exists to prevent.
    [Fact]
    public void A_real_version_1_settings_file_upgrades_without_losing_the_user_s_choices()
    {
        SettingsStore store = MakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """
            {
              "schemaVersion": 1,
              "frameRate": 30,
              "qualityPreset": "sharp-text",
              "qualityOverride": 20,
              "retentionDays": 21,
              "hotkeys": { "start": "Ctrl+Alt+F9", "pause": "Ctrl+Alt+F10", "stop": "Ctrl+Alt+F11" }
            }
            """);

        CaptrSettings settings = store.Load();

        settings.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
        settings.RetentionDays.ShouldBe(21);

        // v1 spelled it "frameRate"; v3 spells it "framerate". The VALUE has to survive
        // the rename — losing it would silently reset someone's capture rate.
        settings.FrameRate.ShouldBe(30, "settings untouched by the migration must survive it");

        // "sharp-text" bundled quality and effort together; it maps to the pair that
        // reproduces it.
        settings.Quality.ShouldBe("high");
        settings.SpeedPreset.ShouldBe("veryfast");

        // The old start key becomes the record toggle, the old pause key the pause
        // toggle; the old stop key has nowhere to go because the record toggle now
        // does its job.
        settings.Hotkeys.RecordToggle.ShouldBe("Ctrl+Alt+F9");
        settings.Hotkeys.PauseToggle.ShouldBe("Ctrl+Alt+F10");
    }

    // The v2 fixture: written after quality split into speed + quality, but before
    // the keys were renamed. Both spellings must land on the same settings.
    [Fact]
    public void A_version_2_settings_file_has_its_renamed_keys_carried_over()
    {
        SettingsStore store = MakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """
            {
              "schemaVersion": 2,
              "frameRate": 24,
              "speedPreset": "faster",
              "quality": "maximum"
            }
            """);

        CaptrSettings settings = store.Load();

        settings.SchemaVersion.ShouldBe(CaptrSettings.CurrentSchemaVersion);
        settings.FrameRate.ShouldBe(24);
        settings.SpeedPreset.ShouldBe("faster");
        settings.Quality.ShouldBe("maximum");
    }

    [Fact]
    public void An_upgraded_file_with_no_hotkeys_is_given_the_defaults()
    {
        // A v2 file records hotkeys as explicit empty strings, which deserialise as
        // "deliberately disabled" — so without this an upgrading user would end up
        // with no hotkeys while a fresh install got two.
        SettingsStore store = MakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """
            {
              "schemaVersion": 2,
              "hotkeys": { "recordToggle": "", "pauseToggle": "" }
            }
            """);

        CaptrSettings settings = store.Load();

        settings.Hotkeys.RecordToggle.ShouldBe("Ctrl+Alt+F9");
        settings.Hotkeys.PauseToggle.ShouldBe("Ctrl+Alt+F10");
    }

    [Fact]
    public void An_upgraded_file_keeps_hotkeys_the_user_already_chose()
    {
        SettingsStore store = MakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """
            {
              "schemaVersion": 2,
              "hotkeys": { "recordToggle": "Ctrl+Shift+R", "pauseToggle": "" }
            }
            """);

        CaptrSettings settings = store.Load();

        settings.Hotkeys.RecordToggle.ShouldBe("Ctrl+Shift+R", "a chosen hotkey must never be overwritten");
        settings.Hotkeys.PauseToggle.ShouldBe("Ctrl+Alt+F10");
    }

    [Fact]
    public void The_settings_file_uses_the_short_key_names()
    {
        // The names in the file are part of the product: people read and hand-edit it,
        // and `captr settings get` must show the same thing.
        string exported = SettingsStore.Export(CaptrSettings.CreateDefault());

        // Case-sensitive on purpose: "framerate" and "frameRate" differ only in case,
        // and Shouldly's default string comparison would call them the same.
        exported.ShouldContain("\"framerate\"", Case.Sensitive);
        exported.ShouldContain("\"preset\"", Case.Sensitive);
        exported.ShouldNotContain("\"frameRate\"", Case.Sensitive);
        exported.ShouldNotContain("\"speedPreset\"", Case.Sensitive);
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
        CaptrSettings original = CaptrSettings.CreateDefault() with { FrameRate = 24 };

        CaptrSettings imported = store.Import(SettingsStore.Export(original));

        imported.FrameRate.ShouldBe(24);
    }

    [Fact]
    public void Importing_invalid_settings_is_rejected()
    {
        SettingsStore store = MakeStore();
        string exported = SettingsStore.Export(CaptrSettings.CreateDefault() with { FrameRate = 20 });
        string tampered = exported.Replace("\"framerate\": 20", "\"framerate\": 500");

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
