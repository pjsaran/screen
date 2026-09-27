using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.WindowsEvents;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Sessions;

/// <summary>
/// A start in session 0 is refused before any capture begins (the end-to-end CLI
/// suite covers the same refusal through `captr start` and its exit code).
/// </summary>
/// <remarks>
/// Scenario: a scheduled task configured "Run whether user is logged on or not" runs
/// `captr start`. The recording used to start, report success, and record black for
/// as long as the task ran. The suite cannot put itself in session 0, so it uses the
/// documented simulation variable, which can only ever cause a refusal.
/// </remarks>
[Trait("Category", "Os")]
public class SessionZeroTests
{
    [Fact]
    public async Task Planning_a_recording_in_session_zero_is_refused_before_anything_starts()
    {
        Environment.SetEnvironmentVariable(InteractiveSession.SimulateSession0Variable, "1");
        try
        {
            var planner = new SessionPlanner(Logger.None);

            SessionStartException refusal = await Should.ThrowAsync<SessionStartException>(() =>
                planner.PlanAsync(CaptrSettings.CreateDefault(), null, null, null, null, TestContext.Current.CancellationToken));

            refusal.Message.ShouldContain("session 0");
        }
        finally
        {
            Environment.SetEnvironmentVariable(InteractiveSession.SimulateSession0Variable, null);
        }
    }
}
