using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Core.Tests.Supervision;

/// <summary>
/// The encoder log tail: bounded for diagnostics (SPEC §6), and sliceable per
/// encoder process so one process's errors cannot be blamed on the next one.
/// </summary>
public class LogRingBufferTests
{
    [Fact]
    public void The_tail_is_bounded_and_keeps_the_newest_lines()
    {
        var buffer = new LogRingBuffer(capacity: 3);
        foreach (string line in new[] { "a", "b", "c", "d" })
        {
            buffer.Add(line);
        }

        buffer.Snapshot().ShouldBe(["b", "c", "d"]);
    }

    [Fact]
    public void A_mark_slices_off_everything_an_earlier_encoder_process_wrote()
    {
        // THE BUG THIS PREVENTS. FFmpeg rewrites its report file on every relaunch,
        // but the tail spans the whole session. Classification reads the tail to
        // decide fault-vs-external, and a fault marches the session toward a loud
        // stop — so one stale "Error" line from a process that died ten minutes ago
        // could condemn a perfectly clean exit and end a recording nobody asked to
        // end. Each process is judged only on what it wrote itself.
        var buffer = new LogRingBuffer(capacity: 100);
        buffer.Add("[hevc_nvenc] Error initializing encoder");

        long afterRelaunch = buffer.Mark();
        buffer.Add("[info] frame= 120 fps=30");

        buffer.SnapshotSince(afterRelaunch).ShouldBe(["[info] frame= 120 fps=30"]);
        buffer.Snapshot().Count.ShouldBe(2, "diagnostics still see the whole tail");

        SupervisorPolicy.ClassifyExit(
            stopWasRequested: false, killedForStall: false, exitCode: 1,
            logTail: buffer.SnapshotSince(afterRelaunch))
            .ShouldBe(ExitKind.External, "the new process's own log is clean");
    }

    [Fact]
    public void A_mark_taken_before_a_line_includes_that_line()
    {
        var buffer = new LogRingBuffer(capacity: 10);
        long start = buffer.Mark();
        buffer.Add("first");
        buffer.Add("second");

        buffer.SnapshotSince(start).ShouldBe(["first", "second"]);
    }

    [Fact]
    public void A_slice_whose_lines_were_evicted_degrades_to_what_is_still_held()
    {
        // A flooding encoder can push a mark's lines out of a bounded buffer. The
        // slice is best-effort by design — it must never throw or return nonsense,
        // because the caller is about to decide whether to keep recording.
        var buffer = new LogRingBuffer(capacity: 2);
        long start = buffer.Mark();
        foreach (string line in new[] { "a", "b", "c", "d" })
        {
            buffer.Add(line);
        }

        buffer.SnapshotSince(start).ShouldBe(["c", "d"]);
    }

    [Fact]
    public void A_mark_from_the_future_yields_nothing_rather_than_throwing()
    {
        var buffer = new LogRingBuffer(capacity: 4);
        buffer.Add("a");

        buffer.SnapshotSince(99).ShouldBeEmpty();
    }
}
