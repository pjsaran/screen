using Captr.Core.Settings;

using Shouldly;

namespace Captr.Core.Tests.Settings;

/// <summary>
/// SPEC §8: while recording, only DEGRADING changes are permitted — a lower frame
/// rate, a lower quality, or removing a display. Everything else waits for the
/// recording to stop.
/// </summary>
public class SettingsChangePolicyTests
{
    private static CaptrSettings Running => CaptrSettings.CreateDefault() with
    {
        FrameRate = 15,
        Quality = "high",
        SpeedPreset = "veryfast",
        ExcludedDisplayIds = [],
        WorkingFolder = @"C:\work",
    };

    [Fact]
    public void An_unchanged_settings_object_is_allowed_and_degrades_nothing()
    {
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(Running, Running);

        verdict.Allowed.ShouldBeTrue();
        verdict.DegradesRecording.ShouldBeFalse();
    }

    [Fact]
    public void Lowering_the_frame_rate_is_a_permitted_degradation()
    {
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(Running, Running with { FrameRate = 10 });

        verdict.Allowed.ShouldBeTrue();
        verdict.DegradesRecording.ShouldBeTrue("a lower frame rate must roll a new segment and be journaled");
    }

    [Fact]
    public void Raising_the_frame_rate_is_refused_with_a_message_that_says_what_to_do()
    {
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(Running, Running with { FrameRate = 30 });

        verdict.Allowed.ShouldBeFalse();
        verdict.RejectionMessage.ShouldContain("Stop the recording first");
    }

    [Fact]
    public void Lowering_quality_is_permitted_and_raising_it_is_refused()
    {
        // A HIGHER CRF is a softer, cheaper picture, so moving to it is degrading.
        SettingsChangePolicy.Evaluate(Running, Running with { Quality = "compact" })
            .DegradesRecording.ShouldBeTrue();

        SettingsChangePolicy.Evaluate(Running, Running with { Quality = "maximum" })
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Choosing_a_faster_preset_is_permitted_and_a_slower_one_is_refused()
    {
        // A FASTER preset asks the encoder for less work per frame, which is the
        // degrading direction even though the files get bigger.
        SettingsChangePolicy.Evaluate(Running, Running with { SpeedPreset = "ultrafast" })
            .DegradesRecording.ShouldBeTrue();

        SettingsChangePolicy.Evaluate(Running, Running with { SpeedPreset = "fast" })
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Removing_a_display_is_a_permitted_degradation()
    {
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(
            Running, Running with { ExcludedDisplayIds = ["display-b"] });

        verdict.Allowed.ShouldBeTrue();
        verdict.DegradesRecording.ShouldBeTrue();
    }

    [Fact]
    public void Adding_a_display_back_is_refused_because_the_canvas_would_change_shape()
    {
        CaptrSettings current = Running with { ExcludedDisplayIds = ["display-b"] };

        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(current, current with { ExcludedDisplayIds = [] });

        verdict.Allowed.ShouldBeFalse();
        verdict.RejectionMessage.ShouldContain("canvas");
    }

    [Fact]
    public void Changing_the_working_folder_mid_recording_is_refused()
    {
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(
            Running, Running with { WorkingFolder = @"D:\elsewhere" });

        verdict.Allowed.ShouldBeFalse();
        verdict.RejectionMessage.ShouldContain("working folder");
    }

    [Fact]
    public void Settings_that_do_not_touch_the_encoder_are_free_to_change_while_recording()
    {
        // Naming, retention, hotkeys, and cosmetics affect nothing the encoder is
        // doing, so they must not be locked.
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(Running, Running with
        {
            OutputPattern = "{machine}-{date}",
            RetentionDays = 30,
            CloseToTray = false,
            Hotkeys = new HotkeySettings { PauseToggle = "Ctrl+Alt+F10" },
        });

        verdict.Allowed.ShouldBeTrue();
        verdict.DegradesRecording.ShouldBeFalse();
    }

    [Fact]
    public void Several_refusals_are_reported_together()
    {
        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(
            Running, Running with { FrameRate = 60, Quality = "maximum", WorkingFolder = @"D:\x" });

        verdict.Rejections.Count.ShouldBe(3);
    }
}
