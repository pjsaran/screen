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

    [Theory]
    [InlineData(-1)]
    [InlineData(52)]
    public void Quality_override_outside_encoder_range_is_rejected(int qp)
    {
        SettingsValidator.Validate(Valid with { QualityOverride = qp })
            .ShouldContain(e => e.Field == nameof(CaptrSettings.QualityOverride));
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
}
