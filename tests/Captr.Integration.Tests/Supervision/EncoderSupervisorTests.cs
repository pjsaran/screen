using System.Diagnostics;

using Captr.Core.Sessions;
using Captr.Core.Supervision;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Supervision;

/// <summary>
/// Supervision against the real FFmpeg binary with synthetic input (SPEC §14
/// integration list). CI-safe: no desktop, no GPU — trait Ffmpeg.
/// </summary>
[Trait("Category", "Ffmpeg")]
public class EncoderSupervisorTests
{
    [Fact]
    public async Task A_supervised_recording_stops_gracefully_and_leaves_playable_segments()
    {
        // Scenario: a normal short recording — start, run ~5 s, request stop.
        using var session = new SupervisionTestSession();
        var supervisor = new EncoderSupervisor(session.FfmpegPath, session.Journal, Logger.None);
        var spec = new EncoderRunSpec(session.LavfiArguments(), null, session.WorkingFolder);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SupervisionOutcome outcome = await supervisor.RunAsync(spec, null, stop.Token);

        outcome.Kind.ShouldBe(SupervisionEndKind.StoppedGracefully);
        supervisor.LatestProgress.ShouldNotBeNull();
        supervisor.LatestProgress!.Frame.ShouldBeGreaterThan(0);

        var segments = Directory.GetFiles(session.WorkingFolder, "seg-*.mkv");
        segments.ShouldNotBeEmpty();

        IReadOnlyList<JournalEvent> events = session.ReadJournal();
        events.OfType<EncoderProcessLaunched>().ShouldHaveSingleItem();
        events.OfType<StopRequested>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_external_kill_restarts_the_encoder_with_the_gap_journaled_and_not_counted()
    {
        // Scenario: a user "ends the task" mid-recording. The supervisor must
        // restart into a new segment, journal the honest gap, and NOT count the
        // termination toward the fallback threshold (SPEC §6/§14).
        using var session = new SupervisionTestSession();
        var supervisor = new EncoderSupervisor(session.FfmpegPath, session.Journal, Logger.None);
        var spec = new EncoderRunSpec(session.LavfiArguments(), null, session.WorkingFolder);

        using var stop = new CancellationTokenSource();
        Task<SupervisionOutcome> run = supervisor.RunAsync(spec, null, stop.Token);

        int firstPid = await WaitForLaunchedPidAsync(session, expectedLaunchCount: 1);
        await WaitUntilAsync(() => supervisor.LatestProgress is { Frame: > 5 }, TimeSpan.FromSeconds(10));

        // taskkill /F is what "End task" does — exit code 1 with a clean log.
        KillExternally(firstPid);

        // The supervisor must relaunch a NEW encoder process.
        int secondPid = await WaitForLaunchedPidAsync(session, expectedLaunchCount: 2);
        secondPid.ShouldNotBe(firstPid);

        await WaitUntilAsync(
            () => session.ReadJournal().OfType<GapRecorded>().Any(),
            TimeSpan.FromSeconds(15));

        await stop.CancelAsync();
        SupervisionOutcome outcome = await run;

        outcome.Kind.ShouldBe(SupervisionEndKind.StoppedGracefully);
        IReadOnlyList<JournalEvent> events = session.ReadJournal();
        EncoderRestarted restart = events.OfType<EncoderRestarted>().ShouldHaveSingleItem();
        restart.CountsTowardFallback.ShouldBeFalse();
        events.OfType<GapRecorded>().ShouldHaveSingleItem().Reason.ShouldBe("encoder restart");
        events.OfType<EncoderFellBack>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Repeated_faults_take_the_single_fallback_then_stop_loudly()
    {
        // Scenario: the encoder faults instantly every time (SPEC §14: repeated
        // software-encoder failure must stop loudly rather than degrade further).
        // Primary AND fallback arguments both fault, so the run must end
        // FailedLoudly after exactly one fallback attempt.
        using var session = new SupervisionTestSession();
        var supervisor = new EncoderSupervisor(session.FfmpegPath, session.Journal, Logger.None);
        var spec = new EncoderRunSpec(
            session.InstantFaultArguments(),
            session.InstantFaultArguments(),
            session.WorkingFolder);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SupervisionOutcome outcome = await supervisor.RunAsync(spec, null, safety.Token);

        outcome.Kind.ShouldBe(SupervisionEndKind.FailedLoudly);
        outcome.FailureReason.ShouldNotBeNull();

        IReadOnlyList<JournalEvent> events = session.ReadJournal();
        events.OfType<EncoderFellBack>().ShouldHaveSingleItem();
        events.OfType<EncoderRestarted>().ShouldAllBe(e => e.CountsTowardFallback);
    }

    [Fact]
    public async Task A_lost_desktop_retries_for_ever_and_never_ends_the_session()
    {
        // THE REGRESSION THIS PINS DOWN. A locked screen, a UAC prompt, or a closed
        // remote-desktop window makes FFmpeg exit with an error, but no other
        // encoder could have recorded a desktop that is not there — so it must
        // retry, not count toward the fallback, and never end the recording
        // (SPEC §6).
        //
        // Captr got this wrong in the only way that mattered: it matched log text
        // FFmpeg never prints, so every lost desktop was booked as an encoder fault.
        // On a machine with no hardware encoder there is no fallback to absorb them,
        // so three of them inside five minutes stopped the recording outright.
        //
        // The arguments below make FFmpeg itself write a real gdigrab capture-loss
        // line into its report file (the missing input is named after one), so this
        // exercises the whole chain rather than handing a string to the classifier.
        using var session = new SupervisionTestSession();
        var supervisor = new EncoderSupervisor(session.FfmpegPath, session.Journal, Logger.None);

        // No fallback arguments — deliberately the AWS WorkSpaces shape, where the
        // software encoder IS the primary and there is nothing left to fall back to.
        var spec = new EncoderRunSpec(session.CaptureLossArguments(), null, session.WorkingFolder);

        using var stop = new CancellationTokenSource();
        Task<SupervisionOutcome> run = supervisor.RunAsync(spec, null, stop.Token);

        // Far past the three faults that used to end the session. The backoff is
        // 1s, 2s, 4s… so five launches take roughly eight seconds.
        await WaitUntilAsync(
            () => session.ReadJournal().OfType<EncoderProcessLaunched>().Count() >= 5,
            TimeSpan.FromSeconds(40));

        run.IsCompleted.ShouldBeFalse("a desktop that is away must never end the recording");

        await stop.CancelAsync();
        SupervisionOutcome outcome = await run;
        outcome.Kind.ShouldBe(SupervisionEndKind.StoppedGracefully);

        IReadOnlyList<JournalEvent> events = session.ReadJournal();
        events.OfType<EncoderFellBack>().ShouldBeEmpty("a different encoder cannot conjure a desktop");
        events.OfType<EncoderRestarted>()
            .ShouldAllBe(e => !e.CountsTowardFallback, "capture losses never count toward the fallback");

        // The outage is journaled ONCE, not once per retry: a disconnected remote
        // session can last for hours and would otherwise bury the journal.
        events.OfType<SessionNote>()
            .Count(note => note.Text.Contains("nothing to capture", StringComparison.Ordinal))
            .ShouldBe(1);

        // The session ended while capture was still down, so nothing ever arrived to
        // close the hole. It must be journaled anyway: coverage is computed from
        // journaled gaps, and without this the recording would claim to be
        // continuous across a stretch it captured nothing during (SPEC §13).
        GapRecorded gap = events.OfType<GapRecorded>().ShouldHaveSingleItem();
        gap.Reason.ShouldBe("capture unavailable");
        gap.Duration.ShouldBeGreaterThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_flooded_encoder_log_neither_deadlocks_nor_exhausts_memory()
    {
        // Scenario: SPEC §5/§14 — flood the encoder's log output while consuming
        // the progress stream. With file-based output the two-pipe deadlock class
        // is structurally impossible; this proves it and checks the ring buffer
        // bound holds under a debug-level torrent.
        using var session = new SupervisionTestSession();
        var supervisor = new EncoderSupervisor(session.FfmpegPath, session.Journal, Logger.None);

        List<string> arguments = session.LavfiArguments();
        // showinfo logs one line per frame; debug report level multiplies it.
        int inputIndex = arguments.IndexOf("testsrc2=size=320x180:rate=10");
        arguments[inputIndex] = "testsrc2=size=320x180:rate=10,showinfo";
        var spec = new EncoderRunSpec(arguments, null, session.WorkingFolder);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        SupervisionOutcome outcome = await supervisor.RunAsync(
            spec, null, stop.Token, reportLogLevel: 48);

        outcome.Kind.ShouldBe(SupervisionEndKind.StoppedGracefully);
        supervisor.LatestProgress.ShouldNotBeNull("progress must keep flowing during the log flood");
        supervisor.LatestProgress!.Frame.ShouldBeGreaterThan(0);
        supervisor.LogTailSnapshot.Count.ShouldBeLessThanOrEqualTo(SupervisionConstants.LogTailLines);

        // Proof the flood actually happened: the report file is large.
        new FileInfo(Path.Combine(session.WorkingFolder, FfmpegProcess.ReportFileName))
            .Length.ShouldBeGreaterThan(100_000);
    }

    private static void KillExternally(int pid)
    {
        using var taskkill = Process.Start(new ProcessStartInfo
        {
            FileName = "taskkill",
            ArgumentList = { "/F", "/PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        taskkill!.WaitForExit();
    }

    private static async Task<int> WaitForLaunchedPidAsync(SupervisionTestSession session, int expectedLaunchCount)
    {
        await WaitUntilAsync(
            () => session.ReadJournal().OfType<EncoderProcessLaunched>().Count() >= expectedLaunchCount,
            TimeSpan.FromSeconds(20));
        return session.ReadJournal().OfType<EncoderProcessLaunched>().Skip(expectedLaunchCount - 1).First().ProcessId;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            stopwatch.Elapsed.ShouldBeLessThan(timeout, "condition not met in time");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
