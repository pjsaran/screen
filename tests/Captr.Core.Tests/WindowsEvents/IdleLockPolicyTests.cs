using Captr.Core.WindowsEvents;

using Shouldly;

namespace Captr.Core.Tests.WindowsEvents;

/// <summary>
/// What Captr tells a user about a machine that locks itself. Captr never changes
/// any of these settings — the whole value is in saying, before a long unattended
/// recording rather than after, exactly what is going to happen to it.
/// </summary>
public class IdleLockPolicyTests
{
    private static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    [Fact]
    public void A_machine_left_alone_says_nothing()
    {
        IdleLockPolicy.Undisturbed.WillInterruptARecording.ShouldBeFalse();
        IdleLockPolicy.Undisturbed.Describe().ShouldBeEmpty();
    }

    [Fact]
    public void A_locking_screen_saver_costs_a_gap()
    {
        var policy = new IdleLockPolicy(FifteenMinutes, ScreenSaverLocks: true, InactivityLockTimeout: null);

        policy.TimeUntilCaptureIsLost.ShouldBe(FifteenMinutes);
        policy.TimeUntilTheScreenSaverIsRecorded.ShouldBeNull(
            "a locking screen saver takes the desktop away rather than being recorded");
        policy.Describe().ShouldContain("15 minutes");
        policy.Describe().ShouldContain("gap");
    }

    [Fact]
    public void A_screen_saver_that_does_NOT_lock_is_recorded_instead_of_the_screen()
    {
        // The quiet failure, and the reason this is reported separately. A
        // non-locking screen saver stays on the ordinary desktop, so capture keeps
        // working perfectly and faithfully records the screen saver: no gap, no
        // warning, and the content silently gone.
        var policy = new IdleLockPolicy(FifteenMinutes, ScreenSaverLocks: false, InactivityLockTimeout: null);

        policy.TimeUntilCaptureIsLost.ShouldBeNull("nothing takes the desktop away");
        policy.TimeUntilTheScreenSaverIsRecorded.ShouldBe(FifteenMinutes);
        policy.WillInterruptARecording.ShouldBeTrue();
        policy.Describe().ShouldContain("what gets recorded");
    }

    [Fact]
    public void A_policy_inactivity_lock_counts_even_with_no_screen_saver()
    {
        // The managed-machine case — an AWS WorkSpace typically has exactly this and
        // no screen saver at all.
        var policy = new IdleLockPolicy(null, ScreenSaverLocks: false, InactivityLockTimeout: FiveMinutes);

        policy.TimeUntilCaptureIsLost.ShouldBe(FiveMinutes);
        policy.Describe().ShouldContain("5 minutes");
    }

    [Fact]
    public void When_both_can_lock_the_soonest_one_is_what_gets_reported()
    {
        var policy = new IdleLockPolicy(FifteenMinutes, ScreenSaverLocks: true, InactivityLockTimeout: FiveMinutes);

        policy.TimeUntilCaptureIsLost.ShouldBe(FiveMinutes, "whichever fires first is what the user experiences");
        policy.Describe().ShouldContain("5 minutes");
        policy.Describe().ShouldNotContain("15 minutes");
    }

    [Fact]
    public void A_non_locking_screen_saver_and_a_lock_are_both_reported()
    {
        // They are different outcomes at different times, so collapsing them into one
        // sentence would hide one of them.
        var policy = new IdleLockPolicy(FiveMinutes, ScreenSaverLocks: false, InactivityLockTimeout: FifteenMinutes);

        policy.TimeUntilTheScreenSaverIsRecorded.ShouldBe(FiveMinutes);
        policy.TimeUntilCaptureIsLost.ShouldBe(FifteenMinutes);
        policy.Describe().ShouldContain("5 minutes");
        policy.Describe().ShouldContain("15 minutes");
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(15, "15 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(61, "1 hour 1 minute")]
    [InlineData(120, "2 hours")]
    public void Timeouts_are_spoken_the_way_a_person_would_say_them(int minutes, string expected)
    {
        // Never "00:15:00". This text goes straight onto the Diagnostics page and
        // into the journal, where a novice has to read it.
        var policy = new IdleLockPolicy(null, false, TimeSpan.FromMinutes(minutes));

        policy.Describe().ShouldContain(expected);
    }

    [Fact]
    public void Reading_the_real_machine_never_throws()
    {
        // A diagnostic that fails loudly about its own plumbing helps nobody, and
        // this one runs on every Diagnostics page load and at every recording start.
        IdleLockPolicy policy = IdleLockPolicy.Read();

        policy.ShouldNotBeNull();
        _ = policy.Describe();
    }
}
