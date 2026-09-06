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

    private static EncoderProgress ProgressAt(DateTimeOffset at, double? speed = 1.0, long totalBytes = 0) => new()
    {
        Frame = 1,
        ObservedUtc = at,
        Speed = speed,
        TotalSizeBytes = totalBytes,
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
    public void A_frozen_file_with_the_encoder_far_ahead_is_a_stall()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);

        // Progress keeps arriving and the encoder claims 50 MB produced, but the
        // newest segment file never grows past 1000 bytes for the whole window.
        for (int second = 1; second <= 130; second++)
        {
            policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(second), totalBytes: 50_000_000));
            policy.OnFileLength(1000, T0 + TimeSpan.FromSeconds(second));
        }

        policy.DetectStall(T0 + TimeSpan.FromSeconds(130)).ShouldNotBeNull();
        policy.DetectStall(T0 + TimeSpan.FromSeconds(130))!.ShouldContain("not growing");
    }

    [Fact]
    public void A_frozen_file_is_NOT_a_stall_while_the_bytes_fit_in_ffmpegs_output_buffer()
    {
        // The false positive that once looped healthy encoders to death: static
        // screen content produces so few bytes that FFmpeg's 512 KB output buffer
        // keeps the file at 0 for a long time. The encoder's own byte counter is
        // the tiebreaker (see SupervisionConstants.FileGrowthSlackBytes).
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);
        for (int second = 1; second <= 130; second++)
        {
            policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(second), totalBytes: 300_000));
            policy.OnFileLength(0, T0 + TimeSpan.FromSeconds(second));
        }

        policy.DetectStall(T0 + TimeSpan.FromSeconds(130)).ShouldBeNull();
    }

    [Fact]
    public void File_growth_resets_the_growth_clock()
    {
        var policy = new SupervisorPolicy();
        policy.OnProcessLaunched(T0);
        for (int second = 1; second <= 130; second++)
        {
            policy.OnProgress(ProgressAt(T0 + TimeSpan.FromSeconds(second), totalBytes: 50_000_000));
            policy.OnFileLength(1000 + second, T0 + TimeSpan.FromSeconds(second));
        }

        policy.DetectStall(T0 + TimeSpan.FromSeconds(130)).ShouldBeNull();
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

    [Theory]
    // Desktop Duplication (ddagrab) — the secure desktop owns the screen, or the
    // duplication could not be handed back after a session switch.
    [InlineData("[ddagrab @ 0000021f] Desktop duplication access denied")]
    [InlineData("[ddagrab @ 0000021f] Failed duplicating output: 887a0026")]
    [InlineData("[ddagrab @ 0000021f] Failed querying IDXGIOutput1")]
    // GDI (gdigrab) — the capture method used where Desktop Duplication does not
    // exist, i.e. AWS WorkSpaces. A disconnected or locked session has no desktop
    // to copy pixels from, so the blit and the device-context calls fail.
    [InlineData("[gdigrab @ 0000027a] Failed to capture image (error 6)")]
    [InlineData("[gdigrab @ 0000027a] Couldn't get window device context (error 0)")]
    [InlineData("[gdigrab @ 0000027a] Couldn't get window rectangle (error 1400)")]
    public void Losing_the_desktop_is_its_own_kind_not_an_encoder_fault(string logLine)
    {
        // UAC's secure desktop, a lock, a remote client disconnecting. Counting
        // these as faults marches a healthy session through the fallback to a loud
        // stop just because someone closed their remote-desktop window (SPEC §6).
        //
        // Every line above is FFmpeg's REAL wording, taken out of the shipped
        // binary. CaptureLossSignatureTests keeps them honest; an earlier version of
        // this test invented three strings FFmpeg never prints, so it passed while
        // production could not classify a single lost desktop correctly.
        SupervisorPolicy.ClassifyExit(
            stopWasRequested: false, killedForStall: false, exitCode: 1, logTail: [logLine])
            .ShouldBe(ExitKind.CaptureAccessLost);
    }

    [Fact]
    public void A_gdigrab_capture_loss_survives_the_generic_error_test_that_would_call_it_a_fault()
    {
        // The trap: gdigrab writes "(error 6)" into its message, so the generic
        // "does the log mention an error" heuristic matches it too. Capture loss
        // must be decided FIRST, or the machines that use gdigrab — the ones with no
        // hardware encoder and therefore no fallback left — stop after three
        // disconnects.
        SupervisorPolicy.ClassifyExit(
            stopWasRequested: false, killedForStall: false, exitCode: 1,
            logTail:
            [
                "[gdigrab @ 0000027a] Failed to capture image (error 6)",
                "[out#0/matroska] Error muxing a packet",
                "Conversion failed!",
            ])
            .ShouldBe(ExitKind.CaptureAccessLost);
    }

    [Fact]
    public void Repeated_capture_losses_never_push_the_session_toward_the_fallback()
    {
        // The whole point: a desktop that keeps coming and going costs gaps, never
        // the session. A different encoder cannot conjure a desktop (SPEC §6).
        var policy = new SupervisorPolicy();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            SupervisorPolicy.ClassifyExit(
                stopWasRequested: false, killedForStall: false, exitCode: 1,
                logTail: ["[gdigrab @ 0000027a] Failed to capture image (error 6)"])
                .ShouldBe(ExitKind.CaptureAccessLost);
        }

        policy.HasFallenBack.ShouldBeFalse("capture losses are never faults, so nothing counts down");
    }

    [Fact]
    public void A_genuine_ddagrab_failure_is_still_a_fault_not_a_transient_capture_loss()
    {
        // The classifier must be narrow: if any line MENTIONING ddagrab counted as
        // transient, a real capture fault would retry forever and the fallback
        // ladder would silently never engage.
        SupervisorPolicy.ClassifyExit(
            stopWasRequested: false, killedForStall: false, exitCode: 1,
            logTail: ["[ddagrab @ 0000] Error initializing filter 'ddagrab'"])
            .ShouldBe(ExitKind.Fault);
    }

    [Fact]
    public void Capture_retries_back_off_and_are_capped()
    {
        var policy = new SupervisorPolicy();

        policy.NextCaptureRetryDelay().ShouldBe(TimeSpan.FromSeconds(1));
        policy.NextCaptureRetryDelay().ShouldBe(TimeSpan.FromSeconds(2));
        policy.NextCaptureRetryDelay().ShouldBe(TimeSpan.FromSeconds(4));
        policy.NextCaptureRetryDelay().ShouldBe(TimeSpan.FromSeconds(8));
        policy.NextCaptureRetryDelay().ShouldBe(SupervisionConstants.CaptureRetryCeiling);
        policy.NextCaptureRetryDelay().ShouldBe(SupervisionConstants.CaptureRetryCeiling, "the backoff is capped");
    }

    [Fact]
    public void The_backoff_resets_once_frames_flow_again()
    {
        var policy = new SupervisorPolicy();
        policy.NextCaptureRetryDelay();
        policy.NextCaptureRetryDelay();

        policy.OnCaptureRecovered();

        policy.NextCaptureRetryDelay().ShouldBe(TimeSpan.FromSeconds(1),
            "a later loss starts its own backoff from the beginning");
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
