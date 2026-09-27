using Captr.Core.WindowsEvents;

using Shouldly;

namespace Captr.Core.Tests.WindowsEvents;

/// <summary>
/// Session 0 and non-interactive window stations have no desktop to capture; a
/// recording started there used to "succeed" and contain nothing but black.
/// </summary>
public class InteractiveSessionTests
{
    [Fact]
    public void Session_zero_is_refused_with_the_fix_named()
    {
        string? reason = InteractiveSession.Evaluate(sessionId: 0, windowStation: "WinSta0");

        reason.ShouldNotBeNull();
        reason.ShouldContain("session 0");
        reason.ShouldContain("Run only when user is logged on");
    }

    [Fact]
    public void A_service_window_station_is_refused_even_outside_session_zero()
    {
        InteractiveSession.Evaluate(sessionId: 2, windowStation: "Service-0x0-3e7$").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("WinSta0")]
    [InlineData("winsta0")]
    [InlineData(null)]
    public void The_signed_in_users_desktop_is_allowed(string? windowStation)
    {
        InteractiveSession.Evaluate(sessionId: 1, windowStation).ShouldBeNull();
    }

    [Fact]
    public void This_test_run_itself_is_on_an_interactive_desktop()
    {
        // The real probe, on the machine running the suite: if this fails, either the
        // probe is wrong or the suite is running somewhere no recording could work.
        InteractiveSession.WhyCaptureIsImpossible().ShouldBeNull();
    }
}
