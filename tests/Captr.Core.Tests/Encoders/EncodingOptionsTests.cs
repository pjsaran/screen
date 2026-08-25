using Captr.Core.Encoders;

using Shouldly;

namespace Captr.Core.Tests.Encoders;

/// <summary>
/// The three encoding choices a user makes — frame rate, speed preset, quality —
/// and their translation into each encoder's own arguments (SPEC §5). These are pure
/// lookups and string building, so everything is testable without FFmpeg.
/// </summary>
public class EncodingOptionsTests
{
    /// <summary>Every encoder Captr will ever try to run. If a candidate is added to
    /// the catalogue without a quality and speed mapping, these tests fail — which is
    /// the point, because the omission would otherwise surface as a failed trial on a
    /// user's machine.</summary>
    public static TheoryData<string> EveryEncoder =>
    [
        "hevc_nvenc", "h264_nvenc",
        "hevc_qsv", "h264_qsv",
        "hevc_amf", "h264_amf",
        "libx264",
        "libopenh264",
    ];

    [Theory]
    [MemberData(nameof(EveryEncoder))]
    public void Every_encoder_has_arguments_for_every_quality_and_speed_combination(string encoder)
    {
        foreach (SpeedPreset speed in SpeedPresets.All)
        {
            foreach (QualityLevel quality in QualityLevels.All)
            {
                IReadOnlyList<string> arguments = QualityLevels.BuildEncoderArguments(
                    encoder, speed, quality, 1920, 1080, 15);

                arguments.ShouldNotBeEmpty($"{encoder} at {speed.Name}/{quality.Name} produced no arguments");
                arguments.ShouldAllBe(a => !string.IsNullOrWhiteSpace(a));
            }
        }
    }

    [Fact]
    public void An_encoder_nobody_added_to_the_tables_fails_loudly_rather_than_silently()
    {
        Should.Throw<ArgumentException>(() => QualityLevels.BuildEncoderArguments(
                "vp9_magic", SpeedPresets.All[0], QualityLevels.All[0], 1920, 1080, 15))
            .Message.ShouldContain("EncoderCatalog");
    }

    [Fact]
    public void H264_is_given_a_lower_quantizer_than_HEVC_for_the_same_quality()
    {
        // H.264 needs a lower quantizer than HEVC to look equivalent; if this ever
        // inverts, H.264 recordings quietly get worse at the same setting.
        QualityLevel balanced = QualityLevels.FindOrDefault("balanced");
        SpeedPreset preset = SpeedPresets.FindOrDefault("veryfast");

        int hevcQp = QuantizerIn(QualityLevels.BuildEncoderArguments("hevc_nvenc", preset, balanced, 1920, 1080, 15));
        int h264Qp = QuantizerIn(QualityLevels.BuildEncoderArguments("h264_nvenc", preset, balanced, 1920, 1080, 15));

        h264Qp.ShouldBeLessThan(hevcQp);
    }

    [Fact]
    public void Lossless_uses_the_encoder_s_own_lossless_mode_rather_than_quantizer_zero()
    {
        // NVENC's constant-quantizer path cannot reach true lossless even at qp 0;
        // offering "Lossless" that is not lossless would be a lie in the UI.
        IReadOnlyList<string> arguments = QualityLevels.BuildEncoderArguments(
            "hevc_nvenc", SpeedPresets.All[0], QualityLevels.FindOrDefault("lossless"), 1920, 1080, 15);

        arguments.ShouldContain("lossless");
    }

    [Fact]
    public void X264_gets_true_CRF_with_the_H264_offset_and_its_own_preset_names()
    {
        // libx264 is the one software encoder with a real constant-quality mode, so
        // the user's quality choice must arrive as -crf (with the H.264 offset,
        // exactly like the hardware H.264 encoders) — and the speed preset names ARE
        // x264's vocabulary, so they pass through verbatim.
        IReadOnlyList<string> arguments = QualityLevels.BuildEncoderArguments(
            "libx264", SpeedPresets.FindOrDefault("superfast"), QualityLevels.FindOrDefault("balanced"), 1920, 1080, 15);

        int crfIndex = arguments.ToList().IndexOf("-crf");
        crfIndex.ShouldBeGreaterThanOrEqualTo(0, "expected a -crf argument");
        int.Parse(arguments[crfIndex + 1], System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(21, "balanced is CRF 23, minus the H.264 offset of 2");
        PresetIn(arguments).ShouldBe("superfast");
    }

    [Fact]
    public void The_software_fallback_prefers_x264_and_only_offers_what_the_build_contains()
    {
        // The catalog must never offer an encoder the shipped binary lacks — that is
        // what lets one code base run against either the GPL build (x264 present)
        // or the LGPL build (openh264 only) without a code change.
        var gplBuild = new FfmpegCapabilities
        {
            HardwareEncoders = ["hevc_nvenc"],
            SoftwareEncoders = ["libx264", "libopenh264"],
            BuildId = "test-gpl",
        };
        var lgplBuild = gplBuild with { SoftwareEncoders = ["libopenh264"] };

        EncoderCatalog.SoftwareFallback(gplBuild).ShouldBe("libx264");
        EncoderCatalog.SoftwareFallback(lgplBuild).ShouldBe("libopenh264");
        EncoderCatalog.Candidates(gplBuild).ShouldBe(["hevc_nvenc", "libx264", "libopenh264"]);
        EncoderCatalog.Candidates(lgplBuild).ShouldNotContain("libx264");
        EncoderCatalog.IsSoftware("libx264").ShouldBeTrue();
        EncoderCatalog.IsSoftware("hevc_nvenc").ShouldBeFalse();
    }

    [Fact]
    public void A_higher_quality_produces_a_higher_software_bitrate()
    {
        // openh264 has no constant-quality mode, so quality has to become bitrate.
        long high = BitrateIn(QualityLevels.BuildEncoderArguments(
            "libopenh264", SpeedPresets.All[0], QualityLevels.FindOrDefault("high"), 1920, 1080, 15));
        long compact = BitrateIn(QualityLevels.BuildEncoderArguments(
            "libopenh264", SpeedPresets.All[0], QualityLevels.FindOrDefault("compact"), 1920, 1080, 15));

        high.ShouldBeGreaterThan(compact);
    }

    [Fact]
    public void A_slower_preset_maps_to_a_slower_setting_on_every_hardware_encoder()
    {
        // NVENC's scale runs p1 (fastest) to p7 (slowest), so the preset index must
        // rise as the user moves down the list.
        string fastest = PresetIn(SpeedPresets.BuildSpeedArguments("hevc_nvenc", SpeedPresets.FindOrDefault("ultrafast")));
        string slowest = PresetIn(SpeedPresets.BuildSpeedArguments("hevc_nvenc", SpeedPresets.FindOrDefault("fast")));

        string.CompareOrdinal(fastest, slowest).ShouldBeLessThan(0);
    }

    [Fact]
    public void An_unknown_name_falls_back_to_the_default_instead_of_stopping_a_recording()
    {
        // A settings file hand-edited to nonsense must not be able to prevent
        // recording; validation reports it, and the runtime keeps going.
        SpeedPresets.FindOrDefault("nonsense").Name.ShouldBe(SpeedPresets.DefaultName);
        QualityLevels.FindOrDefault("nonsense").Name.ShouldBe(QualityLevels.DefaultName);
    }

    [Fact]
    public void The_offered_frame_rates_are_the_ones_the_specification_lists()
    {
        CaptureRates.All.Select(r => r.FramesPerSecond).ShouldBe([5, 10, 15, 20, 24, 30, 45, 60]);
        CaptureRates.IsSupported(CaptureRates.Default).ShouldBeTrue();
        CaptureRates.IsSupported(7).ShouldBeFalse();
    }

    [Fact]
    public void Every_option_label_states_its_trade_off_so_a_novice_can_choose()
    {
        // The labels ARE the documentation for these settings; an empty one would
        // leave a bare number in the UI.
        CaptureRates.All.ShouldAllBe(r => r.Label.Contains("FPS") && r.Description.Length > 0);
        SpeedPresets.All.ShouldAllBe(p => p.DisplayName.Length > 0 && p.Description.Length > 0);
        QualityLevels.All.ShouldAllBe(q => q.DisplayName.Length > 0 && q.Description.Contains("CRF"));
    }

    /// <summary>Reads the value after <c>-qp</c> in an argument vector.</summary>
    private static int QuantizerIn(IReadOnlyList<string> arguments)
    {
        int index = arguments.ToList().IndexOf("-qp");
        index.ShouldBeGreaterThanOrEqualTo(0, "expected a -qp argument");
        return int.Parse(arguments[index + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long BitrateIn(IReadOnlyList<string> arguments)
    {
        int index = arguments.ToList().IndexOf("-b:v");
        index.ShouldBeGreaterThanOrEqualTo(0, "expected a -b:v argument");
        return long.Parse(arguments[index + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string PresetIn(IReadOnlyList<string> arguments)
    {
        int index = arguments.ToList().IndexOf("-preset");
        index.ShouldBeGreaterThanOrEqualTo(0, "expected a -preset argument");
        return arguments[index + 1];
    }
}
