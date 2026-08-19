using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Core.Tests.Sessions;

/// <summary>
/// Gap accounting from synthetic journals (SPEC §14). Coverage arithmetic must be
/// honest in every edge case — these tests are the specification of "honest".
/// </summary>
public class CoverageCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    private static SessionStarted Start() => new()
    {
        TimestampUtc = T0,
        SessionId = Guid.NewGuid(),
        LocalTimeZoneId = "UTC",
        MachineName = "M",
        UserName = "U",
        AppVersion = "0.1.0",
        FfmpegBuildId = "ff",
        Displays = [],
        CanvasWidth = 1,
        CanvasHeight = 1,
        FrameRate = 15,
        EncoderName = "e",
        QualityPreset = "q",
        EncoderArguments = [],
        WorkingFolder = "w",
    };

    private static SessionFinalized FinalizedAt(TimeSpan afterStart) => new()
    {
        TimestampUtc = T0 + afterStart,
        OutputFiles = [],
        TotalSpan = TimeSpan.Zero,
        RecordedSpan = TimeSpan.Zero,
        GapCount = 0,
        ReconciliationNote = null,
    };

    [Fact]
    public void A_clean_session_has_full_coverage_and_no_gaps()
    {
        var report = CoverageCalculator.Compute([Start(), FinalizedAt(TimeSpan.FromMinutes(10))]);

        report.TotalSpan.ShouldBe(TimeSpan.FromMinutes(10));
        report.RecordedSpan.ShouldBe(TimeSpan.FromMinutes(10));
        report.Coverage.ShouldBe(1.0);
        report.GapCount.ShouldBe(0);
        report.ContainsPausedGaps.ShouldBeFalse();
    }

    [Fact]
    public void An_explicit_gap_is_reported_with_its_timestamp_duration_and_reason()
    {
        var gap = new GapRecorded
        {
            TimestampUtc = T0 + TimeSpan.FromMinutes(5),
            GapStartUtc = T0 + TimeSpan.FromMinutes(4),
            Duration = TimeSpan.FromSeconds(30),
            Reason = "encoder restart",
        };

        var report = CoverageCalculator.Compute([Start(), gap, FinalizedAt(TimeSpan.FromMinutes(10))]);

        report.GapCount.ShouldBe(1);
        report.Gaps[0].StartUtc.ShouldBe(T0 + TimeSpan.FromMinutes(4));
        report.Gaps[0].Duration.ShouldBe(TimeSpan.FromSeconds(30));
        report.Gaps[0].Reason.ShouldBe("encoder restart");
        report.RecordedSpan.ShouldBe(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_pause_resume_pair_counts_as_a_paused_gap()
    {
        var report = CoverageCalculator.Compute(
        [
            Start(),
            new PauseStarted { TimestampUtc = T0 + TimeSpan.FromMinutes(2) },
            new PauseEnded { TimestampUtc = T0 + TimeSpan.FromMinutes(3) },
            FinalizedAt(TimeSpan.FromMinutes(10)),
        ]);

        report.GapCount.ShouldBe(1);
        report.Gaps[0].IsPause.ShouldBeTrue();
        report.Gaps[0].Duration.ShouldBe(TimeSpan.FromMinutes(1));
        report.ContainsPausedGaps.ShouldBeTrue();
        report.RecordedSpan.ShouldBe(TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void A_pause_never_resumed_runs_to_the_end_of_the_session()
    {
        // The session died (or was stopped) while paused — an invisible hole unless
        // accounted for, which is exactly why SPEC §6 makes pause hard to forget.
        var report = CoverageCalculator.Compute(
        [
            Start(),
            new PauseStarted { TimestampUtc = T0 + TimeSpan.FromMinutes(8) },
            FinalizedAt(TimeSpan.FromMinutes(10)),
        ]);

        report.GapCount.ShouldBe(1);
        report.Gaps[0].Duration.ShouldBe(TimeSpan.FromMinutes(2));
        report.Gaps[0].Reason.ShouldContain("never resumed");
    }

    [Fact]
    public void A_crashed_session_uses_the_supplied_fallback_end_time()
    {
        // No SessionFinalized event: recovery passes the last heartbeat time.
        var report = CoverageCalculator.Compute(
            [Start(), new PauseStarted { TimestampUtc = T0 + TimeSpan.FromMinutes(1) }],
            fallbackEndUtc: T0 + TimeSpan.FromMinutes(7));

        report.EndUtc.ShouldBe(T0 + TimeSpan.FromMinutes(7));
        report.TotalSpan.ShouldBe(TimeSpan.FromMinutes(7));
        // The unresumed pause runs to that fallback end.
        report.Gaps[0].Duration.ShouldBe(TimeSpan.FromMinutes(6));
    }

    [Fact]
    public void A_crashed_session_with_no_fallback_uses_the_last_event_time()
    {
        var report = CoverageCalculator.Compute(
            [Start(), new StopRequested { TimestampUtc = T0 + TimeSpan.FromMinutes(4), Reason = "user" }]);

        report.EndUtc.ShouldBe(T0 + TimeSpan.FromMinutes(4));
    }

    [Fact]
    public void Overreported_gaps_never_produce_negative_coverage()
    {
        var hugeGap = new GapRecorded
        {
            TimestampUtc = T0 + TimeSpan.FromMinutes(1),
            GapStartUtc = T0,
            Duration = TimeSpan.FromHours(5),
            Reason = "clock jumped mid-gap",
        };

        var report = CoverageCalculator.Compute([Start(), hugeGap, FinalizedAt(TimeSpan.FromMinutes(10))]);

        report.RecordedSpan.ShouldBe(TimeSpan.Zero);
        report.Coverage.ShouldBe(0.0);
    }

    [Fact]
    public void A_zero_length_session_counts_as_fully_covered()
    {
        var report = CoverageCalculator.Compute([Start(), FinalizedAt(TimeSpan.Zero)]);

        report.Coverage.ShouldBe(1.0);
    }

    [Fact]
    public void Gaps_are_reported_in_chronological_order()
    {
        var late = new GapRecorded { TimestampUtc = T0 + TimeSpan.FromMinutes(9), GapStartUtc = T0 + TimeSpan.FromMinutes(8), Duration = TimeSpan.FromSeconds(1), Reason = "late" };
        var early = new GapRecorded { TimestampUtc = T0 + TimeSpan.FromMinutes(9), GapStartUtc = T0 + TimeSpan.FromMinutes(2), Duration = TimeSpan.FromSeconds(1), Reason = "early" };

        var report = CoverageCalculator.Compute([Start(), late, early, FinalizedAt(TimeSpan.FromMinutes(10))]);

        report.Gaps.Select(g => g.Reason).ShouldBe(["early", "late"]);
    }

    [Fact]
    public void A_journal_without_a_start_event_is_rejected()
    {
        Should.Throw<ArgumentException>(() =>
            CoverageCalculator.Compute([new PauseStarted { TimestampUtc = T0 }]));
    }
}
