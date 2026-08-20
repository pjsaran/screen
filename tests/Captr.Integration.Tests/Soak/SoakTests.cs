using System.Diagnostics;

using Captr.Core.Sessions;
using Captr.Core.Supervision;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Soak;

/// <summary>
/// The soak test (SPEC §14): a long continuous recording with no handle leak, no
/// unbounded memory growth, no timestamp drift beyond a second, an internally
/// consistent journal, every segment verified, and coverage above 99.9%.
/// </summary>
/// <remarks>
/// Duration comes from the CAPTR_SOAK_MINUTES environment variable (default 6 for
/// a local smoke of the machinery; the release runbook sets 600 for the full
/// ten-hour run — same assertions, longer clock). Trait Soak keeps it out of
/// normal runs.
/// </remarks>
[Trait("Category", "Soak")]
public class SoakTests
{
    [Fact]
    public async Task A_long_recording_stays_healthy_leak_free_and_fully_covered()
    {
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("CAPTR_SOAK_MINUTES"), out int m) ? m : 6;
        TimeSpan duration = TimeSpan.FromMinutes(minutes);

        using var session = new Supervision.SupervisionTestSession();
        var supervisor = new EncoderSupervisor(session.FfmpegPath, session.Journal, Logger.None);
        var spec = new EncoderRunSpec(
            session.LavfiArguments(segmentSeconds: 30), null, session.WorkingFolder);

        using var currentProcess = Process.GetCurrentProcess();
        var samples = new List<(TimeSpan Elapsed, TimeSpan Encoded, int Handles, long ManagedBytes)>();
        var stopwatch = Stopwatch.StartNew();

        using var stop = new CancellationTokenSource(duration);
        Task<SupervisionOutcome> run = supervisor.RunAsync(spec, null, stop.Token);

        while (!run.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None);
            currentProcess.Refresh();
            samples.Add((
                stopwatch.Elapsed,
                supervisor.LatestProgress?.OutTime ?? TimeSpan.Zero,
                currentProcess.HandleCount,
                GC.GetTotalMemory(forceFullCollection: false)));
        }

        SupervisionOutcome outcome = await run;
        stopwatch.Stop();
        outcome.Kind.ShouldBe(SupervisionEndKind.StoppedGracefully);
        samples.Count.ShouldBeGreaterThanOrEqualTo(6, "the soak must be long enough to sample");

        // --- Timestamp DRIFT: does the wall-vs-encoded offset GROW? --------------
        // Not the absolute difference: a fixed offset is just startup latency
        // (device open, first frame). Drift is that offset CHANGING over time,
        // which is what makes a long recording's timeline untrustworthy.
        //
        // Averaged over several samples at each end, because a single sample
        // carries up to ~0.75 s of measurement jitter on its own (FFmpeg writes a
        // progress block roughly twice a second and the tailer polls every 250 ms).
        // Comparing two lone samples would put ±1.5 s of noise against a 1 s bar —
        // the assertion would be measuring the clock of the test, not the product.
        double EarlyOffsetSeconds(int index) => (samples[index].Elapsed - samples[index].Encoded).TotalSeconds;
        double earlyOffset = Enumerable.Range(1, 3).Average(EarlyOffsetSeconds);
        double lateOffset = Enumerable.Range(samples.Count - 3, 3).Average(EarlyOffsetSeconds);

        Math.Abs(lateOffset - earlyOffset).ShouldBeLessThan(
            1.0,
            $"encoded time drifted from wall time by {(lateOffset - earlyOffset) * 1000:F0} ms " +
            $"over {samples[^1].Elapsed - samples[1].Elapsed} " +
            $"(offset {earlyOffset:F2}s → {lateOffset:F2}s)");

        // --- Leak checks: compare a late sample window to an early one -----------
        double earlyHandles = samples.Take(3).Average(s => s.Handles);
        double lateHandles = samples.TakeLast(3).Average(s => s.Handles);
        lateHandles.ShouldBeLessThan(earlyHandles + 300,
            $"handle count grew {earlyHandles:F0} → {lateHandles:F0} — that trend is a leak");

        double earlyMb = samples.Take(3).Average(s => s.ManagedBytes) / 1_000_000;
        double lateMb = samples.TakeLast(3).Average(s => s.ManagedBytes) / 1_000_000;
        lateMb.ShouldBeLessThan(earlyMb + 100,
            $"managed memory grew {earlyMb:F0} MB → {lateMb:F0} MB — that trend is a leak");

        // --- Finalise; every segment must verify, coverage must exceed 99.9% -----
        // Release the journal writer first, as RecordingSession does in production
        // before finalisation reopens it to append the terminal event.
        session.Journal.Dispose();
        string ffprobe = FfmpegLocator.FindFfprobe();
        FinalizationResult result = await new FinalizationPipeline(FfmpegLocator.FindFfmpeg(), ffprobe, Logger.None)
            .RunAsync(session.WorkingFolder, CancellationToken.None);

        result.RepairedSegments.ShouldBe(0, "a clean soak must not need repairs");
        result.Coverage.Coverage.ShouldBeGreaterThan(0.999);

        IntegrityRecord record = IntegrityRecord.ReadOrNull(session.WorkingFolder).ShouldNotBeNull();
        (await record.VerifyAsync(session.WorkingFolder, CancellationToken.None)).ShouldBeEmpty();

        // --- Journal internal consistency ----------------------------------------
        IReadOnlyList<JournalEvent> events = session.ReadJournal();
        events.OfType<SessionStarted>().ShouldHaveSingleItem();
        events.OfType<SessionFinalized>().ShouldHaveSingleItem();
        events.OfType<EncoderRestarted>().ShouldBeEmpty("a healthy soak has no restarts");
        events.OfType<GapRecorded>().ShouldBeEmpty("a healthy soak has no gaps");
    }
}
