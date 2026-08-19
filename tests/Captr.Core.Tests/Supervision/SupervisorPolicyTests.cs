using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Core.Tests.Supervision;

/// <summary>
/// The supervision rules of SPEC §6 as pure logic: stall detection, fault-vs-external
/// classification, the single fallback, and stop-loudly. No processes involved.
/// </summary>
public class SupervisorPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    private static EncoderProgress ProgressAt(DateTimeOffset at, double? speed = 1.0) => new()
    {
        Frame = 1,
        ObservedUtc = at,
        Speed = speed,
    };

    [Fact]
    public void A_freshly_launched_encoder_gets_startup_grace_before_stall_checks()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);

        policy.DetectStall(T0 + TimeSpan.FromSeconds(5)).ShouldBeNull();
        policy.DetectStall(T0 + TimeSpan.FromSeconds(9)).ShouldNotBeNull();
    }

    [Fact]
    public void Fresh_progress_resets_the_stall_clock()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);
        policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(7)));

        policy.DetectStall(T0 + TimeSpan.FromSeconds(12)).ShouldBeNull();
        policy.DetectStall(T0 + TimeSpan.FromSeconds(16)).ShouldNotBeNull();
    }

    [Fact]
    public void A_growing_output_file_is_healthy_but_a_frozen_one_is_a_stall()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);

        // Progress keeps arriving, but the newest segment file stops growing.
        for (int second = 1; second <= 20; second++)
        {
            policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(second)));
            policy.OnFileLength(1000, T0 + TimeSpan.FromSeconds(second));
        }

        policy.DetectStall(T0 + TimeSpan.FromSeconds(20)).ShouldNotBeNull();
        policy.DetectStall(T0 + TimeSpan.FromSeconds(20))!.ShouldContain("not growing");
    }

    [Fact]
    public void File_growth_resets_the_growth_clock()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);
        for (int second = 1; second <= 30; second++)
        {
            policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(second)));
            policy.OnFileLength(1000 + second, T0 + TimeSpan.FromSeconds(second));
        }

        policy.DetectStall(T0 + TimeSpan.FromSeconds(30)).ShouldBeNull();
    }

    [Fact]
    public void Slow_encoding_must_be_sustained_for_the_full_window_to_count()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);
        policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(1), speed: 0.5));

        policy.IsSustainedSlow(T0 + TimeSpan.FromSeconds(30)).ShouldBeFalse();
        policy.IsSustainedSlow(T0 + TimeSpan.FromSeconds(62)).ShouldBeTrue();
    }

    [Fact]
    public void A_speed_recovery_resets_the_slow_window()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);
        policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(1), speed: 0.5));
        policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(30), speed: 1.0));
        policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(31), speed: 0.5));

        policy.IsSustainedSlow(T0 + TimeSpan.FromSeconds(80)).ShouldBeFalse();
    }

    [Fact]
    public void A_requested_stop_is_graceful_whatever_the_exit_code()
    {
        SupervisorPolicy.ClassifyExit(stopWasRequested: true, killedForStall: false, exitCode: 1, logTail: [])
            .ShouldBe(ExitKind.Graceful);
    }

    [Fact]
    public void A_stall_kill_is_a_fault()
    {
        SupervisorPolicy.ClassifyExit(stopWasRequested: false, killedForStall: true, exitCode: -1, logTail: [])
            .ShouldBe(ExitKind.Fault);
    }

    [Fact]
    public void Exit_code_1_with_a_clean_log_is_an_external_termination()
    {
        // Task Manager's End Task produces exit code 1 with no error in the log —
        // must NOT count toward fallback (SPEC §6/§14).
        SupervisorPolicy.ClassifyExit(stopWasRequested: false, killedForStall: false, exitCode: 1,
                logTail: ["[info] frame= 100"])
            .ShouldBe(ExitKind.External);
    }

    [Fact]
    public void Exit_code_1_with_errors_in_the_log_is_a_fault()
    {
        SupervisorPolicy.ClassifyExit(stopWasRequested: false, killedForStall: false, exitCode: 1,
                logTail: ["[hevc_nvenc] Error initializing encoder"])
            .ShouldBe(ExitKind.Fault);
    }

    [Fact]
    public void Faults_below_the_threshold_just_restart()
    {
        var policy = new SupervisorPolicy();

        policy.OnFault(T0).ShouldBe(FaultVerdict.Restart);
        policy.OnFault(T0 + TimeSpan.FromSeconds(10)).ShouldBe(FaultVerdict.Restart);
    }

    [Fact]
    public void Reaching_the_fault_threshold_takes_the_single_fallback_then_stops_loudly()
    {
        var policy = new SupervisorPolicy();
        policy.OnFault(T0);
        policy.OnFault(T0 + TimeSpan.FromSeconds(10));

        policy.OnFault(T0 + TimeSpan.FromSeconds(20)).ShouldBe(FaultVerdict.FallBackToSoftware);
        policy.HasFallenBack.ShouldBeTrue();

        // The same rule after the fallback ends the session — no second rung (SPEC §6).
        policy.OnFault(T0 + TimeSpan.FromSeconds(30));
        policy.OnFault(T0 + TimeSpan.FromSeconds(40));
        policy.OnFault(T0 + TimeSpan.FromSeconds(50)).ShouldBe(FaultVerdict.StopLoudly);
    }

    [Fact]
    public void Old_faults_age_out_of_the_window()
    {
        var policy = new SupervisorPolicy();
        policy.OnFault(T0);
        policy.OnFault(T0 + TimeSpan.FromSeconds(1));

        // The third fault arrives after the first two left the 5-minute window.
        policy.OnFault(T0 + TimeSpan.FromMinutes(10)).ShouldBe(FaultVerdict.Restart);
    }
}
