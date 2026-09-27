using System.Diagnostics;

using Captr.Core.Sessions;
using Captr.Core.Supervision;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Sessions;

/// <summary>
/// Finalisation and recovery against real FFmpeg, real segments, real corruption
/// (SPEC §14): a clean stop finalises; a deliberately truncated segment is repaired
/// with durations reconciling; recovery runs the SAME path automatically; and the
/// verification pass detects on-disk tampering.
/// </summary>
[Trait("Category", "Ffmpeg")]
public class FinalizationPipelineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-finalize-").FullName;
    private readonly string _ffmpeg = FfmpegLocator.FindFfmpeg();
    private readonly string _ffprobe = FfmpegLocator.FindFfprobe();

    private FinalizationPipeline MakePipeline() => new(_ffmpeg, _ffprobe, Logger.None);

    /// <summary>Creates a session folder with a journal and records lavfi content
    /// into 2-second segments. When <paramref name="killHard"/> is set, the encoder
    /// is terminated mid-segment, leaving the final segment truncated on disk —
    /// the exact artefact a crash or power cut leaves behind.</summary>
    private async Task<string> RecordSessionAsync(string name, int seconds, bool killHard)
    {
        string folder = Path.Combine(_root, name);
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
            Quality = "balanced",
            SpeedPreset = "veryfast",
            EncoderArguments = [],
            WorkingFolder = folder,
        }))
        {
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!killHard)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (string argument in new List<string>
        {
            "-c:v", "libopenh264", "-b:v", "1M",
            "-force_key_frames", "expr:gte(t,n_forced*2)", "-g", "1000",
            "-f", "segment", "-segment_format", "matroska", "-segment_time", "2",
            "-reset_timestamps", "1", "-strftime", "1",
            Path.Combine(folder, "seg-g01-%H%M%S.mkv"),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        if (killHard)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);
            process.Kill(); // Mid-segment: the open segment file is left truncated.
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return folder;
    }

    [Fact]
    public async Task A_clean_stop_finalises_with_full_integrity_and_a_verifiable_record()
    {
        string folder = await RecordSessionAsync("clean", seconds: 7, killHard: false);

        FinalizationResult result = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);

        result.OutputFiles.ShouldHaveSingleItem();
        result.RepairedSegments.ShouldBe(0);
        result.SegmentCount.ShouldBeGreaterThanOrEqualTo(3);

        // The joined output plays and covers the recorded length.
        ProbeResult probe = await FfprobeClient.ProbeAsync(_ffprobe, result.OutputFiles[0], TestContext.Current.CancellationToken);
        probe.Success.ShouldBeTrue(probe.FailureReason);
        probe.Duration.ShouldBeGreaterThan(TimeSpan.FromSeconds(6));
        probe.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(8));

        // The integrity record verifies against the untouched disk.
        IntegrityRecord record = IntegrityRecord.ReadOrNull(folder).ShouldNotBeNull();
        (await record.VerifyAsync(folder, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        // The journal carries the terminal event — recovery will skip this session.
        SessionJournal.ReadAll(Path.Combine(folder, SessionJournal.FileName))
            .OfType<SessionFinalized>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Time_the_journal_says_was_recorded_but_no_footage_holds_is_reported_as_lost()
    {
        // Recovery once logged "29 s of footage recovered, nothing lost, coverage 100 %"
        // for a session whose only segment could not be repaired - it had produced no
        // playable footage at all. The journal says when recording HAPPENED; only the
        // segments say what EXISTS.
        string folder = await RecordSessionAsync("short-footage", seconds: 4, killHard: false);
        string journal = Path.Combine(folder, SessionJournal.FileName);
        string[] lines = await File.ReadAllLinesAsync(journal, TestContext.Current.CancellationToken);
        // The session "started" 30 s before the footage did: 26 s with nothing on disk.
        lines[0] = lines[0].Replace(
            System.Text.Json.JsonDocument.Parse(lines[0]).RootElement.GetProperty("timestampUtc").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(-34).ToString("O"), StringComparison.Ordinal);
        await File.WriteAllLinesAsync(journal, lines, TestContext.Current.CancellationToken);

        FinalizationResult result = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);

        result.Coverage.RecordedSpan.ShouldBeLessThan(TimeSpan.FromSeconds(8), "only what is on disk counts as recorded");
        result.Coverage.Gaps.ShouldContain(gap => gap.Reason.StartsWith("footage lost") && gap.Duration > TimeSpan.FromSeconds(20));
        result.Coverage.Coverage.ShouldBeLessThan(0.5);
    }

    [Fact]
    public async Task A_normal_recording_reports_no_lost_footage()
    {
        string folder = await RecordSessionAsync("normal", seconds: 5, killHard: false);

        FinalizationResult result = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);

        result.Coverage.Gaps.ShouldNotContain(gap => gap.Reason.StartsWith("footage lost"));
    }

    [Fact]
    public async Task A_single_segment_recording_is_finalised_without_a_second_copy_of_the_footage()
    {
        // The output of a one-segment recording used to be a full COPY of it, doubling
        // the footage on disk at exactly the moment a recording had stopped for lack
        // of space. It is now a second name for the same bytes.
        string folder = await RecordSessionAsync("single", seconds: 1, killHard: false);
        Directory.GetFiles(folder, "seg-*.mkv").ShouldHaveSingleItem();

        FinalizationResult result = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);

        string output = result.OutputFiles.ShouldHaveSingleItem();
        HardLinkNames(output).Count.ShouldBe(2, "the output and the segment are one file on disk");
        (await IntegrityRecord.ReadOrNull(folder)!.VerifyAsync(folder, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_room_to_join_the_recording_waits_intact_for_space_rather_than_failing_mid_write()
    {
        // Joining writes a file as large as all the segments beside them. After a
        // stop for low disk there was not room, and the join died with an FFmpeg exit
        // code. Now the room is checked first; the segments stay, the session stays
        // unfinalised, and recovery completes it once space is freed.
        string folder = await RecordSessionAsync("no-room", seconds: 5, killHard: false);
        string[] segmentsBefore = Directory.GetFiles(folder, "seg-*.mkv");
        segmentsBefore.Length.ShouldBeGreaterThan(1);
        var pipeline = new FinalizationPipeline(_ffmpeg, _ffprobe, Logger.None, freeSpace: _ => 1_000_000);

        NotEnoughSpaceToFinaliseException refusal = await Should.ThrowAsync<NotEnoughSpaceToFinaliseException>(
            () => pipeline.RunAsync(folder, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("safe");
        Directory.GetFiles(folder, "seg-*.mkv").ShouldBe(segmentsBefore, ignoreOrder: true);
        SessionJournal.ReadAll(Path.Combine(folder, SessionJournal.FileName)).OfType<SessionFinalized>().ShouldBeEmpty();

        FinalizationResult later = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);
        later.OutputFiles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_leftover_repair_scratch_file_is_not_mistaken_for_a_segment()
    {
        string folder = await RecordSessionAsync("leftover", seconds: 3, killHard: false);
        string[] segments = Directory.GetFiles(folder, "seg-*.mkv");
        File.Copy(segments[0], segments[0] + ".repaired.mkv");

        FinalizationResult result = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);

        result.SegmentCount.ShouldBe(segments.Length);
    }

    /// <summary>Every name the file is known by, via fsutil (no elevation needed).</summary>
    private static List<string> HardLinkNames(string path)
    {
        var startInfo = new ProcessStartInfo("fsutil", ["hardlink", "list", path])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    [Fact]
    public async Task A_truncated_final_segment_is_repaired_and_the_original_kept()
    {
        string folder = await RecordSessionAsync("truncated", seconds: 5, killHard: true);

        FinalizationResult result = await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);

        // The kill left the open segment truncated; the pipeline must have healed it
        // (or, if ffmpeg managed to close it in its dying moment, found it intact).
        result.OutputFiles.ShouldHaveSingleItem();
        ProbeResult probe = await FfprobeClient.ProbeAsync(_ffprobe, result.OutputFiles[0], TestContext.Current.CancellationToken);
        probe.Success.ShouldBeTrue(probe.FailureReason);
        probe.Duration.ShouldBeGreaterThan(TimeSpan.FromSeconds(3));

        if (result.RepairedSegments > 0)
        {
            // The original of every repaired segment is preserved, not deleted.
            Directory.GetFiles(folder, "*.original").ShouldNotBeEmpty();
            result.Notes.ShouldContain(n => n.Contains("repaired"));
        }
    }

    [Fact]
    public async Task Recovery_finds_the_interrupted_session_finalises_it_and_reports()
    {
        string folder = await RecordSessionAsync("crashed", seconds: 5, killHard: true);

        var scanner = new RecoveryScanner(MakePipeline(), Logger.None);
        IReadOnlyList<RecoveryReport> reports = await scanner.ScanAndRecoverAsync(_root, TestContext.Current.CancellationToken);

        RecoveryReport report = reports.ShouldHaveSingleItem();
        report.Succeeded.ShouldBeTrue(report.FailureReason);
        report.SessionFolder.ShouldBe(folder);
        report.RecoveredDuration.ShouldBeGreaterThan(TimeSpan.FromSeconds(3));

        // A second scan finds nothing left to recover — the same session never
        // recovers twice.
        (await scanner.ScanAndRecoverAsync(_root, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_verification_pass_detects_a_tampered_segment()
    {
        string folder = await RecordSessionAsync("tampered", seconds: 5, killHard: false);
        await MakePipeline().RunAsync(folder, TestContext.Current.CancellationToken);
        IntegrityRecord record = IntegrityRecord.ReadOrNull(folder).ShouldNotBeNull();

        // Flip one byte in the middle of the first segment.
        string victim = Path.Combine(folder, record.Segments[0].FileName);
        using (var stream = new FileStream(victim, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            int original = stream.ReadByte();
            stream.Position = stream.Length / 2;
            stream.WriteByte((byte)(original ^ 0xFF));
        }

        IReadOnlyList<string> problems = await record.VerifyAsync(folder, TestContext.Current.CancellationToken);

        problems.ShouldContain(p => p.Contains("SHA-256 mismatch"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
