using System.Diagnostics;

using Captr.Core.Sessions;
using Captr.Core.Supervision;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Sessions;

/// <summary>
/// Clip extraction at segment boundaries by stream copy (SPEC §9). The point of the
/// feature is that no re-encoding happens, so the tests check both the arithmetic
/// (the clip is the right length) and the guarantee (the bytes are the original
/// encoder's, not a re-compression).
/// </summary>
[Trait("Category", "Ffmpeg")]
public class SegmentClipperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-clip-").FullName;
    private readonly string _ffmpeg = FfmpegLocator.FindFfmpeg();
    private readonly string _ffprobe = FfmpegLocator.FindFfprobe();

    /// <summary>Records ~10 s of synthetic video in 2-second segments and finalises
    /// it, giving a session with five clippable boundaries.</summary>
    private async Task<string> MakeFinalisedSessionAsync(CancellationToken cancellationToken)
    {
        string folder = Path.Combine(_dir, "session");
        Directory.CreateDirectory(folder);

        using (SessionJournal.CreateNew(folder, new SessionStarted
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid(),
            LocalTimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = "TEST",
            UserName = "test",
            AppVersion = "test",
            FfmpegBuildId = "test",
            Displays = [],
            CanvasWidth = 320,
            CanvasHeight = 180,
            FrameRate = 10,
            EncoderName = "libopenh264",
            QualityPreset = "test",
            EncoderArguments = [],
            WorkingFolder = folder,
        }))
        {
        }

        var startInfo = new ProcessStartInfo { FileName = _ffmpeg, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[]
        {
            // -re paces the synthetic source at real time. Without it, ten seconds
            // of video encode in an instant and every 2-second segment lands in the
            // SAME wall-clock second, so the strftime file names collide and each
            // segment overwrites the last. Production never hits this (segments are
            // 300 s and clock-aligned), but the test must model real timing.
            "-hide_banner", "-nostats", "-loglevel", "error", "-re",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10", "-t", "10",
            "-c:v", "libopenh264", "-b:v", "500k",
            "-force_key_frames", "expr:gte(t,n_forced*2)", "-g", "1000",
            "-f", "segment", "-segment_format", "matroska", "-segment_time", "2",
            "-reset_timestamps", "1", "-strftime", "1",
            Path.Combine(folder, "seg-g01-%H%M%S.mkv"),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (Process process = Process.Start(startInfo)!)
        {
            await process.WaitForExitAsync(cancellationToken);
        }

        await new FinalizationPipeline(_ffmpeg, _ffprobe, Logger.None).RunAsync(folder, cancellationToken);
        return folder;
    }

    [Fact]
    public async Task Segments_are_listed_with_their_offsets_so_a_range_can_be_chosen()
    {
        string folder = await MakeFinalisedSessionAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<ClipCandidate> candidates = SegmentClipper.ListSegments(folder);

        candidates.Count.ShouldBeGreaterThanOrEqualTo(4);
        candidates[0].Index.ShouldBe(1);
        candidates[0].StartOffset.ShouldBe(TimeSpan.Zero);
        // Offsets accumulate: each starts where the previous one ended.
        for (int i = 1; i < candidates.Count; i++)
        {
            candidates[i].StartOffset.ShouldBe(candidates[i - 1].StartOffset + candidates[i - 1].Duration);
        }
    }

    [Fact]
    public async Task A_clip_of_the_middle_segments_has_their_combined_duration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string folder = await MakeFinalisedSessionAsync(cancellationToken);
        IReadOnlyList<ClipCandidate> candidates = SegmentClipper.ListSegments(folder);

        string clip = await new SegmentClipper(_ffmpeg, _ffprobe, Logger.None)
            .ExtractAsync(folder, 2, 3, cancellationToken);

        ProbeResult probe = await FfprobeClient.ProbeAsync(_ffprobe, clip, cancellationToken);
        probe.Success.ShouldBeTrue(probe.FailureReason);

        TimeSpan expected = candidates[1].Duration + candidates[2].Duration;
        (probe.Duration - expected).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(0.5));
        probe.Width.ShouldBe(320);
        probe.Height.ShouldBe(180);
    }

    [Fact]
    public async Task Extraction_is_a_stream_copy_so_the_clip_reuses_the_original_bytes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string folder = await MakeFinalisedSessionAsync(cancellationToken);

        string clip = await new SegmentClipper(_ffmpeg, _ffprobe, Logger.None)
            .ExtractAsync(folder, 1, 2, cancellationToken);

        // A re-encode would change the compressed size materially; a stream copy
        // reproduces the source payload plus a little container overhead.
        IReadOnlyList<ClipCandidate> candidates = SegmentClipper.ListSegments(folder);
        long sourceBytes = candidates.Take(2)
            .Sum(c => new FileInfo(Path.Combine(folder, c.FileName)).Length);
        long clipBytes = new FileInfo(clip).Length;

        clipBytes.ShouldBeGreaterThan((long)(sourceBytes * 0.9));
        clipBytes.ShouldBeLessThan((long)(sourceBytes * 1.1));
    }

    [Fact]
    public async Task An_out_of_range_request_is_rejected_before_anything_is_written()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string folder = await MakeFinalisedSessionAsync(cancellationToken);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            new SegmentClipper(_ffmpeg, _ffprobe, Logger.None).ExtractAsync(folder, 1, 99, cancellationToken));

        Directory.GetFiles(folder, "clip-*.mkv").ShouldBeEmpty();
    }

    [Fact]
    public async Task Clipping_an_unfinalised_folder_fails_with_a_clear_message()
    {
        string empty = Path.Combine(_dir, "not-finalised");
        Directory.CreateDirectory(empty);

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            new SegmentClipper(_ffmpeg, _ffprobe, Logger.None)
                .ExtractAsync(empty, 1, 1, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("finalised");
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
