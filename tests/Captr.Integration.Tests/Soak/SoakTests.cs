using System.Diagnostics;

using Captr.Core.Sessions;
using Captr.Core.Settings;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Soak;

/// <summary>
/// The soak test (SPEC §14): a long continuous recording with no handle leak, no
/// unbounded memory growth, no timestamp drift beyond a second, an internally
/// consistent journal, every segment verified, and coverage above 99.9%.
/// </summary>
/// <remarks>
/// <para>
/// Runs the REAL capture path — ddagrab into the machine's proven hardware encoder,
/// through the actual <see cref="RecordingSession"/> — not a synthetic source.
/// That matters for the drift assertion in particular: a lavfi source paced with
/// <c>-re</c> has its own pacing slop of roughly a percent, so measuring drift
/// against it measures FFmpeg's test-source timer rather than Captr's timeline.
/// Real capture is clocked by the compositor, which is what the spec's
/// one-second bar is actually about.
/// </para>
/// <para>
/// Duration is <c>CAPTR_SOAK_MINUTES</c> (default 6; the release runbook sets 600
/// for the specified ten-hour run — same assertions, longer clock).
/// </para>
/// </remarks>
[Trait("Category", "Soak")]
public class SoakTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-soak-").FullName;

    [Fact]
    public async Task A_long_recording_stays_healthy_leak_free_and_fully_covered()
    {
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("CAPTR_SOAK_MINUTES"), out int m) ? m : 6;
        TimeSpan duration = TimeSpan.FromMinutes(minutes);

        CaptrSettings settings = CaptrSettings.CreateDefault() with { WorkingFolder = _root, FrameRate = 10 };
        (RecordingSession.SessionContext context, SessionStarted startEvent) =
            await new SessionPlanner(Logger.None).PlanAsync(
                settings, null, null, "soak", TestContext.Current.CancellationToken);

        RecordingSession session = RecordingSession.Create(context, startEvent, Logger.None);

        using var currentProcess = Process.GetCurrentProcess();
        var samples = new List<(TimeSpan Elapsed, TimeSpan Encoded, int Handles, long ManagedBytes)>();
        var stopwatch = Stopwatch.StartNew();

        Task<FinalizationResult> run = session.RunAsync(CancellationToken.None);

        while (stopwatch.Elapsed < duration && !run.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None);
            currentProcess.Refresh();
            samples.Add((
                stopwatch.Elapsed,
                session.LatestProgress?.OutTime ?? TimeSpan.Zero,
                currentProcess.HandleCount,
                GC.GetTotalMemory(forceFullCollection: false)));
        }

        session.RequestStop();
        FinalizationResult result = await run.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        stopwatch.Stop();

        samples.Count.ShouldBeGreaterThanOrEqualTo(6, "the soak must be long enough to sample");

        // --- Timestamp DRIFT: does the wall-vs-encoded offset GROW? --------------
        // Not the absolute difference — a fixed offset is startup latency (device
        // open, first frame). Drift is that offset CHANGING, which is what makes a
        // long recording's timeline untrustworthy. Averaged at each end because a
        // single sample carries up to ~0.75 s of jitter on its own (FFmpeg writes
        // progress about twice a second; the tailer polls every 250 ms).
        double OffsetSeconds(int index) => (samples[index].Elapsed - samples[index].Encoded).TotalSeconds;
        double earlyOffset = Enumerable.Range(1, 3).Average(OffsetSeconds);
        double lateOffset = Enumerable.Range(samples.Count - 3, 3).Average(OffsetSeconds);

        Math.Abs(lateOffset - earlyOffset).ShouldBeLessThan(
            1.0,
            $"encoded time drifted from wall time by {(lateOffset - earlyOffset) * 1000:F0} ms " +
            $"over {samples[^1].Elapsed - samples[1].Elapsed} (offset {earlyOffset:F2}s → {lateOffset:F2}s)");

        // --- Leak checks: compare a late sample window to an early one -----------
        double earlyHandles = samples.Take(3).Average(s => s.Handles);
        double lateHandles = samples.TakeLast(3).Average(s => s.Handles);
        lateHandles.ShouldBeLessThan(earlyHandles + 300,
            $"handle count grew {earlyHandles:F0} → {lateHandles:F0} — that trend is a leak");

        double earlyMb = samples.Take(3).Average(s => s.ManagedBytes) / 1_000_000;
        double lateMb = samples.TakeLast(3).Average(s => s.ManagedBytes) / 1_000_000;
        lateMb.ShouldBeLessThan(earlyMb + 100,
            $"managed memory grew {earlyMb:F0} MB → {lateMb:F0} MB — that trend is a leak");

        // --- Coverage, repairs, integrity, journal --------------------------------
        result.RepairedSegments.ShouldBe(0, "a clean soak must not need repairs");
        result.Coverage.Coverage.ShouldBeGreaterThan(0.999);
        result.OutputFiles.ShouldNotBeEmpty();

        IntegrityRecord record = IntegrityRecord.ReadOrNull(context.WorkingFolder).ShouldNotBeNull();
        (await record.VerifyAsync(context.WorkingFolder, CancellationToken.None)).ShouldBeEmpty();

        IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
            Path.Combine(context.WorkingFolder, SessionJournal.FileName));
        events.OfType<SessionStarted>().ShouldHaveSingleItem();
        events.OfType<SessionFinalized>().ShouldHaveSingleItem();
        events.OfType<EncoderRestarted>().ShouldBeEmpty("a healthy soak has no restarts");
        events.OfType<GapRecorded>().ShouldBeEmpty("a healthy soak has no gaps");
        events.OfType<SegmentOpened>().Count().ShouldBeGreaterThan(0);
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
