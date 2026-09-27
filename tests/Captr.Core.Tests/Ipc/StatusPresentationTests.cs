using Captr.Core.Ipc;

using Shouldly;

namespace Captr.Core.Tests.Ipc;

/// <summary>
/// A recording that failed on its own is SHOWN as failed — in the window, the tray,
/// and the tooltip — until somebody acknowledges it or starts again.
/// </summary>
/// <remarks>
/// The host reports "idle" once a session has finished, and finalising usually
/// completes between two once-a-second polls, so a failed recording used to look
/// exactly like one somebody stopped: "Idle — nothing is recording".
/// </remarks>
public class StatusPresentationTests
{
    private static readonly Guid Session = Guid.NewGuid();

    private static StatusResponse Idle(string? result, string? reason = "The encoder failed repeatedly.") =>
        new("idle", null, null, null, null, null, null, null, null,
            LastOutcome: result is null ? null : new SessionOutcome(Session, result, reason, DateTimeOffset.UtcNow, @"C:\Rec\s1"));

    [Theory]
    [InlineData("failed")]
    [InlineData("faulted")]
    public void An_idle_recorder_whose_last_recording_ended_on_its_own_is_shown_as_failed(string result)
    {
        StatusResponse shown = StatusPresentation.ForDisplay(Idle(result), dismissedSessionId: null);

        shown.State.ShouldBe(StatusPresentation.Failed);
        shown.SessionId.ShouldBe(Session);
        shown.WorkingFolder.ShouldBe(@"C:\Rec\s1");
        StatusPresentation.DescribeFailure(shown).ShouldBe("The encoder failed repeatedly.");
    }

    [Fact]
    public void A_normal_stop_is_just_idle()
    {
        StatusPresentation.ForDisplay(Idle("completed", reason: null), null).State.ShouldBe("idle");
        StatusPresentation.ForDisplay(Idle(null), null).State.ShouldBe("idle");
    }

    [Fact]
    public void Once_acknowledged_the_failure_is_no_longer_shown()
    {
        StatusPresentation.ForDisplay(Idle("failed"), dismissedSessionId: Session).State.ShouldBe("idle");
    }

    [Fact]
    public void A_running_recording_is_never_overridden_by_an_older_failure()
    {
        StatusResponse recording = Idle("failed") with { State = "recording" };

        StatusPresentation.ForDisplay(recording, null).State.ShouldBe("recording");
    }

    [Fact]
    public void The_failure_stays_shown_after_the_recorder_has_exited()
    {
        var tracker = new StatusPresentation.Tracker();
        tracker.Present(Idle("failed")).State.ShouldBe(StatusPresentation.Failed);

        // The recorder exits when idle; the next poll finds nobody there.
        tracker.Present(Idle(null)).State.ShouldBe(StatusPresentation.Failed);
    }

    [Fact]
    public void Dismissing_returns_every_view_to_idle_and_it_stays_idle()
    {
        var tracker = new StatusPresentation.Tracker();
        tracker.Present(Idle("failed"));

        tracker.Dismiss().State.ShouldBe("idle");
        tracker.Present(Idle("failed")).State.ShouldBe("idle");
        tracker.Present(Idle(null)).State.ShouldBe("idle");
    }

    [Fact]
    public void Recording_again_forgets_the_old_failure()
    {
        var tracker = new StatusPresentation.Tracker();
        tracker.Present(Idle("failed"));
        tracker.Present(Idle("failed") with { State = "recording" }).State.ShouldBe("recording");

        tracker.Present(Idle(null)).State.ShouldBe("idle");
    }

    [Fact]
    public void A_failure_without_a_reason_still_says_what_to_do_next()
    {
        StatusPresentation.DescribeFailure(Idle("failed", reason: null)).ShouldContain("open Recordings");
    }
}
