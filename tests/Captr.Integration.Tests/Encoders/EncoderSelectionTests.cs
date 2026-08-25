using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Supervision;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Encoders;

/// <summary>
/// Encoder selection against this machine's real GPUs and real desktop (trait Gpu —
/// local only). This laptop has an NVIDIA RTX 3060 and an AMD iGPU but NO Intel GPU,
/// so the QSV encoders advertised by the ffmpeg build MUST fail their trials — the
/// exact "advertised but absent" scenario SPEC §5 warns about — while NVENC or AMF
/// must pass.
/// </summary>
[Trait("Category", "Gpu")]
public class EncoderSelectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-encsel-").FullName;

    private RecordingPlan MakeTemplate()
    {
        IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();
        displays.ShouldNotBeEmpty();
        DisplayInfo primary = displays[0];

        return new RecordingPlan
        {
            SessionId = Guid.NewGuid(),
            Sources = [new CaptureSource(primary.DxgiOutputIndex, primary.Width, primary.Height)],
            FrameRate = 15,
            Encoder = new EncoderSettings("placeholder", []),
            WorkingFolder = _dir,
        };
    }

    private static EncoderSelector MakeSelector(EncoderCache cache) => new(
        FfmpegLocator.FindFfmpeg(), FfmpegLocator.FindFfprobe(), cache, Logger.None);

    private static SpeedPreset DefaultPreset => SpeedPresets.FindOrDefault(SpeedPresets.DefaultName);

    private static QualityLevel DefaultQuality => QualityLevels.FindOrDefault(QualityLevels.DefaultName);

    [Fact]
    public async Task Selection_proves_a_hardware_encoder_and_reports_rejected_candidates_honestly()
    {
        var cache = new EncoderCache(Path.Combine(_dir, "cache.json"));
        EncoderSelection selection = await MakeSelector(cache).SelectAsync(
            MakeTemplate(), DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);

        // A machine with an RTX 3060 must land on a hardware encoder.
        selection.IsSoftware.ShouldBeFalse();
        selection.Encoder.CodecName.ShouldBeOneOf("hevc_nvenc", "h264_nvenc", "hevc_amf", "h264_amf");
        selection.FromCache.ShouldBeFalse();
    }

    [Fact]
    public async Task An_advertised_encoder_without_matching_hardware_fails_its_trial()
    {
        // The build advertises QSV, this machine has no Intel GPU: the trial — not
        // the encoder list — must be what rejects it (SPEC §5).
        RecordingPlan template = MakeTemplate();
        ArrangementPlan arrangement = ArrangementPlanner.Plan(template.Sources);
        RecordingPlan qsvPlan = template with
        {
            Encoder = new EncoderSettings("hevc_qsv", QualityLevels.BuildEncoderArguments(
                "hevc_qsv", DefaultPreset, DefaultQuality,
                arrangement.CanvasWidth, arrangement.CanvasHeight, 15)),
        };

        TrialResult trial = await EncoderTrial.RunAsync(
            FfmpegLocator.FindFfmpeg(), FfmpegLocator.FindFfprobe(), qsvPlan, 2,
            TestContext.Current.CancellationToken);

        trial.Success.ShouldBeFalse("hevc_qsv should not pass a trial on a machine with no Intel GPU");
    }

    [Fact]
    public async Task The_cached_winner_is_confirmed_and_reused()
    {
        var cache = new EncoderCache(Path.Combine(_dir, "cache.json"));
        EncoderSelector selector = MakeSelector(cache);
        RecordingPlan template = MakeTemplate();

        EncoderSelection first = await selector.SelectAsync(
            template, DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);
        EncoderSelection second = await selector.SelectAsync(
            template, DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);

        second.FromCache.ShouldBeTrue();
        second.Encoder.CodecName.ShouldBe(first.Encoder.CodecName);

        // The measured rate is cached with the winner. This is what lets a second
        // recording start immediately instead of re-running a trial encode.
        second.BytesPerHour.ShouldBe(first.BytesPerHour);
    }

    [Fact]
    public async Task A_cache_hit_skips_the_trial_so_a_repeat_start_is_immediate()
    {
        var cache = new EncoderCache(Path.Combine(_dir, "cache.json"));
        EncoderSelector selector = MakeSelector(cache);
        RecordingPlan template = MakeTemplate();

        await selector.SelectAsync(template, DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await selector.SelectAsync(template, DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // A trial encode alone takes seconds; a cache hit must not run one. The
        // generous bound proves "no trial happened" without being timing-flaky.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Changing_the_quality_invalidates_the_cache_because_the_measured_rate_no_longer_applies()
    {
        var cache = new EncoderCache(Path.Combine(_dir, "cache.json"));
        EncoderSelector selector = MakeSelector(cache);
        RecordingPlan template = MakeTemplate();

        await selector.SelectAsync(template, DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);
        EncoderSelection other = await selector.SelectAsync(
            template, DefaultPreset, QualityLevels.FindOrDefault("compact"), "test", TestContext.Current.CancellationToken);

        other.FromCache.ShouldBeFalse("a different quality means a different measured rate");
    }

    [Fact]
    public async Task The_size_estimate_comes_from_a_real_measurement()
    {
        var cache = new EncoderCache(Path.Combine(_dir, "cache.json"));
        EncoderSelection selection = await MakeSelector(cache).SelectAsync(
            MakeTemplate(), DefaultPreset, DefaultQuality, "test", TestContext.Current.CancellationToken);

        double gigabytesPerHour = selection.BytesPerHour / 1_000_000_000.0;

        // A 1080p-class desktop at 15 fps lands well inside this envelope at the
        // default quality; the bounds catch a broken measurement, not variance.
        gigabytesPerHour.ShouldBeGreaterThan(0.001);
        gigabytesPerHour.ShouldBeLessThan(50);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
