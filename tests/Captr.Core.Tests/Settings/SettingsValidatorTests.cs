using Captr.Core.Settings;

using Shouldly;

namespace Captr.Core.Tests.Settings;

public class SettingsValidatorTests
{
    private static CaptrSettings Valid => CaptrSettings.CreateDefault();

    [Fact]
    public void Default_settings_are_valid()
    {
        SettingsValidator.Validate(Valid).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(61)]
    public void Frame_rate_outside_1_to_60_is_rejected(int fps)
    {
        SettingsValidator.Validate(Valid with { FrameRate = fps })
            .ShouldContain(e => e.Field == nameof(CaptrSettings.FrameRate));
    }

    [Fact]
    public void An_unknown_quality_level_is_rejected_and_the_message_lists_the_real_ones()
    {
        SettingsError error = SettingsValidator.Validate(Valid with { Quality = "amazing" })
            .ShouldHaveSingleItem();

        error.Field.ShouldBe(nameof(CaptrSettings.Quality));
        error.Message.ShouldContain("balanced");
    }

    [Fact]
    public void An_unknown_speed_preset_is_rejected_and_the_message_lists_the_real_ones()
    {
        SettingsError error = SettingsValidator.Validate(Valid with { SpeedPreset = "turbo" })
            .ShouldHaveSingleItem();

        error.Field.ShouldBe(nameof(CaptrSettings.SpeedPreset));
        error.Message.ShouldContain("veryfast");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(120)]
    public void A_frame_rate_that_is_not_one_of_the_offered_rates_is_rejected(int fps)
    {
        SettingsValidator.Validate(Valid with { FrameRate = fps })
            .ShouldContain(e => e.Field == nameof(CaptrSettings.FrameRate));
    }

    [Fact]
    public void A_destination_kind_that_is_listed_but_not_built_is_refused_before_it_can_lose_a_recording()
    {
        SettingsError error = SettingsValidator.Validate(Valid with
        {
            Destinations = [new DestinationSettings { Name = "bucket", Kind = DestinationKind.AmazonS3 }],
        }).ShouldHaveSingleItem();

        error.Field.ShouldBe(nameof(CaptrSettings.Destinations));
        error.Message.ShouldContain("cannot transfer");
    }

    [Fact]
    public void A_relative_working_folder_is_rejected_with_an_example()
    {
        var errors = SettingsValidator.Validate(Valid with { WorkingFolder = "recordings" });

        errors.ShouldContain(e => e.Field == nameof(CaptrSettings.WorkingFolder));
        errors.Single(e => e.Field == nameof(CaptrSettings.WorkingFolder)).Message.ShouldContain("C:\\");
    }

    [Fact]
    public void Negative_retention_is_rejected()
    {
        SettingsValidator.Validate(Valid with { RetentionDays = -1 })
            .ShouldContain(e => e.Field == nameof(CaptrSettings.RetentionDays));
    }

    [Fact]
    public void An_unknown_naming_token_is_rejected_and_the_supported_ones_are_listed()
    {
        var errors = SettingsValidator.Validate(Valid with { OutputPattern = "{machine} {seesion}" });

        var error = errors.Single(e => e.Field == nameof(CaptrSettings.OutputPattern));
        error.Message.ShouldContain("{seesion}");
        error.Message.ShouldContain("{machine}");
    }

    [Fact]
    public void A_folder_destination_without_a_path_is_rejected()
    {
        var settings = Valid with
        {
            Destinations = [new DestinationSettings { Name = "d", Kind = DestinationKind.Folder }],
        };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("no folder path"));
    }

    [Fact]
    public void A_sharepoint_destination_without_a_site_url_is_rejected()
    {
        var settings = Valid with
        {
            Destinations = [new DestinationSettings { Name = "sp", Kind = DestinationKind.SharePoint }],
        };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("no site URL"));
    }

    [Fact]
    public void Duplicate_destination_names_are_rejected()
    {
        var settings = Valid with
        {
            Destinations =
            [
                new DestinationSettings { Name = "same", Kind = DestinationKind.Folder, FolderPath = @"C:\a" },
                new DestinationSettings { Name = "SAME", Kind = DestinationKind.Folder, FolderPath = @"C:\b" },
            ],
        };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("unique"));
    }

    [Fact]
    public void All_problems_are_reported_at_once_not_just_the_first()
    {
        var settings = Valid with { FrameRate = 0, RetentionDays = -1 };

        SettingsValidator.Validate(settings).Count.ShouldBe(2);
    }

    // ---- Destination folders may carry naming tokens ---------------------------

    [Fact]
    public void A_destination_folder_may_contain_naming_tokens()
    {
        var settings = Valid with
        {
            Destinations =
            [
                new DestinationSettings
                {
                    Name = "archive",
                    Kind = DestinationKind.Folder,
                    FolderPath = @"\\nas\video\{date:yyyy-MM}",
                },
            ],
        };

        SettingsValidator.Validate(settings).ShouldBeEmpty();
    }

    [Fact]
    public void A_destination_folder_whose_token_would_produce_an_illegal_character_is_rejected()
    {
        // Caught here, while the user is looking at the field — not eight hours later
        // when a finished recording has nowhere to go.
        var settings = Valid with
        {
            Destinations =
            [
                new DestinationSettings
                {
                    Name = "archive",
                    Kind = DestinationKind.Folder,
                    FolderPath = @"D:\Rec\{start:HH:mm:ss}",
                },
            ],
        };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("':'"));
    }

    [Fact]
    public void An_unknown_token_in_a_sharepoint_folder_is_rejected()
    {
        var settings = Valid with
        {
            Destinations =
            [
                new DestinationSettings
                {
                    Name = "sp",
                    Kind = DestinationKind.SharePoint,
                    SharePointSiteUrl = "https://contoso.sharepoint.com/sites/x",
                    SharePointFolder = "Docs/{whoops}",
                },
            ],
        };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("{whoops}"));
    }

    // ---- The retry policy has to describe a run that terminates ------------------

    [Fact]
    public void The_shipped_retry_defaults_are_valid() =>
        SettingsValidator.Validate(Valid with { Retries = new RetrySettings() }).ShouldBeEmpty();

    [Fact]
    public void Zero_automatic_attempts_is_rejected_because_a_transfer_must_be_tried_once()
    {
        var settings = Valid with { Retries = new RetrySettings { MaxAttempts = 0 } };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("at least 1"));
    }

    [Fact]
    public void An_absurd_number_of_attempts_is_rejected_as_a_mistake()
    {
        var settings = Valid with { Retries = new RetrySettings { MaxAttempts = 500 } };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("50"));
    }

    [Fact]
    public void A_ceiling_shorter_than_the_first_wait_is_rejected()
    {
        // Otherwise the backoff would shrink with each attempt instead of growing.
        var settings = Valid with
        {
            Retries = new RetrySettings { FirstRetrySeconds = 600, MaxRetrySeconds = 60 },
        };

        SettingsValidator.Validate(settings).ShouldContain(e => e.Message.Contains("cannot be shorter"));
    }
}
