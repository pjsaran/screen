using Captr.Core.Encoders;

namespace Captr.Core.Settings;

/// <summary>
/// Decides whether a settings change may be applied WHILE a recording is running
/// (SPEC §8: "lock capture and quality settings while recording, permitting only
/// degrading changes — a lower frame rate, a lower quality, or removing a display").
/// Pure decision logic, so every case is unit tested. Owns the definition of
/// "degrading": anything that asks the machine to do LESS work, never more.
/// </summary>
/// <remarks>
/// The asymmetry is deliberate. Asking for more mid-recording — a higher frame
/// rate, better quality, an extra display — means a bigger canvas or bitrate on a
/// machine that is already committed, which is how a recording falls behind and
/// starts dropping frames. Asking for less always succeeds. Everything that is not
/// a capture or quality setting (naming pattern, retention, destinations, hotkeys,
/// cosmetics) is free to change at any time: none of it touches the encoder.
/// </remarks>
public static class SettingsChangePolicy
{
    /// <summary>Evaluates a proposed change against a running session.</summary>
    /// <param name="current">Settings the session is running under.</param>
    /// <param name="proposed">What the user wants.</param>
    public static SettingsChangeVerdict Evaluate(CaptrSettings current, CaptrSettings proposed)
    {
        var rejections = new List<string>();
        bool degrades = false;

        // --- Frame rate: lower is fine, higher is not -----------------------------
        if (proposed.FrameRate > current.FrameRate)
        {
            rejections.Add(
                $"Frame rate cannot be raised from {current.FrameRate} to {proposed.FrameRate} while recording. " +
                "Stop the recording first, or lower it instead.");
        }
        else if (proposed.FrameRate < current.FrameRate)
        {
            degrades = true;
        }

        // --- Quality: a higher quantizer step means a smaller, softer picture -----
        int currentStep = StepOf(current.QualityPreset);
        int proposedStep = StepOf(proposed.QualityPreset);
        if (proposedStep < currentStep)
        {
            rejections.Add(
                $"Quality cannot be raised from '{current.QualityPreset}' to '{proposed.QualityPreset}' while recording. " +
                "Stop the recording first, or choose a lower preset instead.");
        }
        else if (proposedStep > currentStep)
        {
            degrades = true;
        }

        if (proposed.QualityOverride is { } proposedOverride
            && current.QualityOverride is { } currentOverride
            && proposedOverride < currentOverride)
        {
            // A LOWER quantizer number means HIGHER quality — more work.
            rejections.Add(
                $"The quality override cannot be lowered from {currentOverride} to {proposedOverride} while recording " +
                "(a lower number means higher quality).");
        }

        // --- Displays: removing one is degrading, adding one is not ---------------
        var currentExcluded = new HashSet<string>(current.ExcludedDisplayIds, StringComparer.OrdinalIgnoreCase);
        var proposedExcluded = new HashSet<string>(proposed.ExcludedDisplayIds, StringComparer.OrdinalIgnoreCase);

        if (proposedExcluded.Except(currentExcluded, StringComparer.OrdinalIgnoreCase).Any())
        {
            degrades = true; // A display was removed from the recording.
        }

        if (currentExcluded.Except(proposedExcluded, StringComparer.OrdinalIgnoreCase).Any())
        {
            rejections.Add(
                "A display cannot be ADDED to a recording in progress — the canvas would change shape. " +
                "Stop the recording first.");
        }

        // --- The working folder is fixed for the life of a session ----------------
        if (!string.Equals(current.WorkingFolder, proposed.WorkingFolder, StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add(
                "The working folder cannot be changed while recording — the session is writing into the current one.");
        }

        return rejections.Count > 0
            ? new SettingsChangeVerdict(false, false, rejections)
            : new SettingsChangeVerdict(true, degrades, []);
    }

    /// <summary>Quality step of a preset name; an unknown name is treated as the
    /// default so a typo cannot look like a degradation.</summary>
    private static int StepOf(string presetName) =>
        QualityPresets.Find(presetName)?.QuantizerStep
        ?? QualityPresets.Find(QualityPresets.DefaultName)!.QuantizerStep;
}

/// <summary>
/// The answer: may this change be saved now, does it degrade the running session
/// (and therefore require rolling a new segment), and if refused, why.
/// </summary>
public sealed record SettingsChangeVerdict(bool Allowed, bool DegradesRecording, IReadOnlyList<string> Rejections)
{
    /// <summary>All refusals as one user-facing message.</summary>
    public string RejectionMessage => string.Join(Environment.NewLine, Rejections);
}
