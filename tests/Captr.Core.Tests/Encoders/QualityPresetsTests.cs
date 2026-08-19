using Captr.Core.Encoders;

using Shouldly;

namespace Captr.Core.Tests.Encoders;

public class QualityPresetsTests
{
    private static QualityPreset SharpText => QualityPresets.Find(QualityPresets.DefaultName)!;

    [Fact]
    public void The_default_preset_exists_and_is_sharp_text()
    {
        SharpText.ShouldNotBeNull();
        SharpText.Name.ShouldBe("sharp-text");
    }

    [Fact]
    public void Presets_are_ordered_best_quality_first()
    {
        QualityPresets.All.Select(p => p.QuantizerStep).ShouldBeInOrder();
    }

    [Theory]
    [InlineData("hevc_nvenc", "-qp", "23")]
    [InlineData("h264_nvenc", "-qp", "21")]
    [InlineData("hevc_qsv", "-global_quality", "23")]
    [InlineData("hevc_amf", "-qp_i", "23")]
    public void Each_hardware_encoder_gets_its_own_dialect_of_quality_arguments(
        string encoder, string expectedFlag, string expectedValue)
    {
        var arguments = QualityPresets.BuildQualityArguments(encoder, SharpText, null, 1920, 1080, 15);

        int flagIndex = arguments.ToList().IndexOf(expectedFlag);
        flagIndex.ShouldBeGreaterThanOrEqualTo(0, $"expected {expectedFlag} in: {string.Join(' ', arguments)}");
        arguments[flagIndex + 1].ShouldBe(expectedValue);
    }

    [Fact]
    public void The_numeric_override_replaces_the_preset_quantizer()
    {
        var arguments = QualityPresets.BuildQualityArguments("hevc_nvenc", SharpText, 30, 1920, 1080, 15);

        arguments.ShouldContain("30");
        arguments.ShouldNotContain("23");
    }

    [Fact]
    public void The_software_encoder_scales_bitrate_with_canvas_and_frame_rate()
    {
        var smallCanvas = QualityPresets.BuildQualityArguments("libopenh264", SharpText, null, 1920, 1080, 15);
        var bigCanvas = QualityPresets.BuildQualityArguments("libopenh264", SharpText, null, 3840, 2160, 15);

        long ParseBitrate(IReadOnlyList<string> args) => long.Parse(args[args.ToList().IndexOf("-b:v") + 1], System.Globalization.CultureInfo.InvariantCulture);
        ParseBitrate(bigCanvas).ShouldBe(ParseBitrate(smallCanvas) * 4);
    }

    [Fact]
    public void An_unknown_encoder_fails_loudly_rather_than_producing_no_quality_arguments()
    {
        Should.Throw<ArgumentException>(() =>
            QualityPresets.BuildQualityArguments("libx264", SharpText, null, 1920, 1080, 15));
    }
}
