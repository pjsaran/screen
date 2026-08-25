using Captr.Core.Encoders;

using Shouldly;

namespace Captr.Core.Tests.Encoders;

/// <summary>
/// Golden-file assertions on the exact generated argument vector across display
/// counts, mismatched sizes, arrangements, and overlay states (SPEC §14). Every case
/// uses fixed inputs so the output is bit-for-bit deterministic.
/// </summary>
public class FfmpegArgumentBuilderTests
{
    private static readonly Guid FixedSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly EncoderSettings NvencHevc = new(
        "hevc_nvenc", ["-rc", "constqp", "-qp", "23", "-preset", "p5", "-tune", "hq"]);

    private static readonly EncoderSettings SoftwareOpenH264 = new(
        "libopenh264", ["-b:v", "8M", "-maxrate", "10M", "-bufsize", "16M"]);

    private static RecordingPlan MakePlan(
        IReadOnlyList<CaptureSource> sources,
        string? overlay = null,
        EncoderSettings? encoder = null) => new()
        {
            SessionId = FixedSessionId,
            Sources = sources,
            FrameRate = 15,
            Encoder = encoder ?? NvencHevc,
            OverlayText = overlay,
            WorkingFolder = @"C:\work\s1",
        };

    [Fact]
    public void One_display_no_overlay()
    {
        GoldenFile.Assert("d1_nooverlay",
            FfmpegArgumentBuilder.Build(MakePlan([new CaptureSource(0, 1920, 1080)])));
    }

    [Fact]
    public void One_display_with_overlay()
    {
        GoldenFile.Assert("d1_overlay",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 1920, 1080)],
                overlay: "Captr %{localtime}")));
    }

    [Fact]
    public void One_display_with_overlay_full_of_special_characters()
    {
        // The characters SPEC §5 warns about: colons, backslashes, quotes, commas,
        // brackets — all must arrive escaped, never break the graph.
        GoldenFile.Assert("d1_overlay_specials",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 1920, 1080)],
                overlay: @"C:\path 10:30, [x];a='b'")));
    }

    [Fact]
    public void Two_equal_displays_stack_side_by_side()
    {
        GoldenFile.Assert("d2_equal_hstack",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 1920, 1080), new CaptureSource(1, 1920, 1080)])));
    }

    [Fact]
    public void Two_unequal_displays_pad_the_shorter_one()
    {
        GoldenFile.Assert("d2_unequal_hstack_pad",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 2560, 1440), new CaptureSource(1, 1920, 1080)])));
    }

    [Fact]
    public void Three_displays_within_width_limit_stay_side_by_side()
    {
        GoldenFile.Assert("d3_equal_hstack",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 1920, 1080), new CaptureSource(1, 1920, 1080), new CaptureSource(2, 1920, 1080)])));
    }

    [Fact]
    public void Three_ultrawides_exceed_the_width_limit_and_form_a_grid_with_a_black_cell()
    {
        GoldenFile.Assert("d3_ultrawide_grid",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 3440, 1440), new CaptureSource(1, 3440, 1440), new CaptureSource(2, 3440, 1440)])));
    }

    [Fact]
    public void Four_displays_form_a_full_grid()
    {
        GoldenFile.Assert("d4_grid",
            FfmpegArgumentBuilder.Build(MakePlan(
                [
                    new CaptureSource(0, 1920, 1080), new CaptureSource(1, 1920, 1080),
                    new CaptureSource(2, 1920, 1080), new CaptureSource(3, 1920, 1080),
                ],
                overlay: "quad")));
    }

    [Fact]
    public void The_software_fallback_encoder_gets_its_own_arguments()
    {
        GoldenFile.Assert("d1_sw_openh264",
            FfmpegArgumentBuilder.Build(MakePlan(
                [new CaptureSource(0, 1920, 1080)],
                encoder: SoftwareOpenH264)));
    }

    [Fact]
    public void Gdi_capture_declares_one_input_per_display_and_reads_from_input_labels()
    {
        // The compatibility path for machines whose display driver cannot serve
        // Desktop Duplication (AWS WorkSpaces and similar virtual desktops):
        // capture becomes a gdigrab INPUT, and the graph starts from [0:v].
        GoldenFile.Assert("d1_gdi",
            FfmpegArgumentBuilder.Build(
                MakePlan([new CaptureSource(0, 1920, 1080)], encoder: SoftwareOpenH264)
                with
                { CaptureMethod = CaptureMethod.Gdi }));
    }

    [Fact]
    public void Gdi_capture_addresses_each_display_by_virtual_desktop_position()
    {
        // A display LEFT of the primary has a negative virtual X — gdigrab must
        // receive it verbatim, or the wrong screen region is recorded.
        GoldenFile.Assert("d2_gdi_offsets",
            FfmpegArgumentBuilder.Build(
                MakePlan(
                    [new CaptureSource(0, 1920, 1080, -1920, 0), new CaptureSource(1, 1920, 1080, 0, 0)],
                    encoder: SoftwareOpenH264)
                with
                { CaptureMethod = CaptureMethod.Gdi }));
    }

    [Fact]
    public void A_later_arrangement_group_lands_in_the_segment_file_names()
    {
        var plan = MakePlan([new CaptureSource(0, 1920, 1080)]) with { ArrangementGroup = 3 };

        IReadOnlyList<string> arguments = FfmpegArgumentBuilder.Build(plan);

        arguments[^1].ShouldBe(@"C:\work\s1\seg-g03-%Y%m%d-%H%M%S.mkv");
    }

    [Fact]
    public void Arguments_are_a_vector_and_never_contain_a_joined_command_line()
    {
        IReadOnlyList<string> arguments = FfmpegArgumentBuilder.Build(
            MakePlan([new CaptureSource(0, 1920, 1080)]));

        // Option names and their values are separate elements: no element both
        // starts with '-' and embeds a space-separated value.
        arguments.Where(a => a.StartsWith('-')).ShouldAllBe(a => !a.Contains(' '));
    }
}
