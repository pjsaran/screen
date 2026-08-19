using Captr.Core.Sessions;
using Captr.Core.Settings;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Sessions;

/// <summary>
/// The full session engine end-to-end on this machine's real desktop and GPU
/// (trait Gpu): plan → record the actual screen → pause → resume → stop →
/// finalise. This is the closest thing to a user pressing the buttons.
/// </summary>
[Trait("Category", "Gpu")]
public class RecordingSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-session-").FullName;

    [Fact]
    public async Task A_real_recording_with_a_pause_finalises_with_honest_coverage()
    {
        CaptrSettings settings = CaptrSettings.CreateDefault() with { WorkingFolder = _root, FrameRate = 10 };

        var planner = new SessionPlanner(Logger.None);
        (RecordingSession.SessionContext context, SessionStarted startEvent) =
            await planner.PlanAsync(settings, null, null, label: null, TestContext.Current.CancellationToken);

        RecordingSession session = RecordingSession.Create(context, startEvent, Logger.None);
        Task<FinalizationResult> run = session.RunAsync(CancellationToken.None);

        // Record ~4 s, pause ~3 s, record ~4 s more, stop.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        session.RequestPause();
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        session.State.ShouldBe(SessionState.Paused);
        session.RequestResume();
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        session.RequestStop();

        FinalizationResult result = await run.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        // Output exists and plays.
        result.OutputFiles.ShouldNotBeEmpty();
        foreach (string output in result.OutputFiles)
        {
            ProbeResult probe = await FfprobeClient.ProbeAsync(
                context.FfprobePath, output, TestContext.Current.CancellationToken);
            probe.Success.ShouldBeTrue(probe.FailureReason);
        }

        // The pause shows up as an honest paused gap; coverage excludes it.
        result.Coverage.ContainsPausedGaps.ShouldBeTrue();
        result.Coverage.Gaps.ShouldContain(g => g.IsPause && g.Duration > TimeSpan.FromSeconds(2));

        // Segment lifecycle was journaled with hashes.
        IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
            Path.Combine(context.WorkingFolder, SessionJournal.FileName));
        events.OfType<SegmentOpened>().ShouldNotBeEmpty();
        events.OfType<SegmentClosed>().ShouldNotBeEmpty();
        events.OfType<SegmentClosed>().ShouldAllBe(s => s.Sha256.Length == 64);
        events.OfType<PauseStarted>().ShouldHaveSingleItem();
        events.OfType<PauseEnded>().ShouldHaveSingleItem();

        // The heartbeat was maintained and the ballast released.
        HeartbeatSnapshot.ReadOrNull(context.WorkingFolder).ShouldNotBeNull();
        File.Exists(Path.Combine(context.WorkingFolder, "ballast.bin")).ShouldBeFalse();

        session.State.ShouldBe(SessionState.Completed);
    }

    [Fact]
    public async Task Starting_with_every_display_excluded_is_refused_with_a_clear_message()
    {
        var enumeratedIds = new Captr.Core.Displays.DisplayEnumerator()
            .Enumerate().Select(d => d.StableId).ToList();
        CaptrSettings settings = CaptrSettings.CreateDefault() with
        {
            WorkingFolder = _root,
            ExcludedDisplayIds = enumeratedIds,
        };

        SessionStartException exception = await Should.ThrowAsync<SessionStartException>(() =>
            new SessionPlanner(Logger.None).PlanAsync(settings, null, null, null, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("deselected");
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
