using Captr.Core.WindowsEvents;

using Shouldly;

namespace Captr.Integration.Tests.WindowsEvents;

/// <summary>
/// The display stays on for the whole recording, whichever thread touches the
/// request (SPEC §6: a display that powers off makes Desktop Duplication record
/// black).
/// </summary>
/// <remarks>
/// The request used to be made with SetThreadExecutionState, whose state belongs to
/// the calling THREAD. A recording's async code runs on whichever pool thread is
/// free: the request was made on one, "released" on another (clearing nothing), and
/// vanished when the pool retired the first one — mid-recording. These tests make
/// and release the request on threads that then end, and read the machine's real
/// execution state. Another program holding the display on at the same moment would
/// make the reading meaningless, so that case is reported as inconclusive.
/// </remarks>
[Trait("Category", "Os")]
public class ExecutionStateHolderTests
{
    private static bool DisplayRequired() => ExecutionStateHolder.IsDisplayRequiredSystemWide();

    private static T OnAThreadThatThenEnds<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void The_display_stays_required_after_the_thread_that_asked_has_ended()
    {
        if (DisplayRequired())
        {
            Assert.Skip("Another program is already holding the display on, so the reading proves nothing.");
        }

        ExecutionStateHolder holder = OnAThreadThatThenEnds(() => new ExecutionStateHolder());
        try
        {
            DisplayRequired().ShouldBeTrue("the recording's request must outlive the thread that made it");
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public void Releasing_from_a_different_thread_really_releases()
    {
        if (DisplayRequired())
        {
            Assert.Skip("Another program is already holding the display on, so the reading proves nothing.");
        }

        var holder = new ExecutionStateHolder();
        OnAThreadThatThenEnds(() =>
        {
            holder.Release();
            return 0;
        });

        DisplayRequired().ShouldBeFalse("an idle machine must be allowed to sleep again after recording");
    }
}
