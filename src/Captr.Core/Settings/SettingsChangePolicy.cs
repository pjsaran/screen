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

        // --- Quality: a higher CRF means a smaller, softer picture ----------------
        QualityLevel currentQuality = QualityLevels.FindOrDefault(current.Quality);
        QualityLevel proposedQuality = QualityLevels.FindOrDefault(proposed.Quality);
        if (proposedQuality.Crf < currentQuality.Crf)
        {
            rejections.Add(
                $"Quality cannot be raised from '{currentQuality.DisplayName}' to '{proposedQuality.DisplayName}' while recording. " +
                "Stop the recording first, or choose a lower quality instead.");
        }
        else if (proposedQuality.Crf > currentQuality.Crf)
        {
            degrades = true;
        }

        // --- Speed preset: a FASTER preset asks the encoder for LESS work ---------
        // (Step 0 is ultrafast; moving to a lower step is the degrading direction.)
        SpeedPreset currentSpeed = SpeedPresets.FindOrDefault(current.SpeedPreset);
        SpeedPreset proposedSpeed = SpeedPresets.FindOrDefault(proposed.SpeedPreset);
        if (proposedSpeed.Step > currentSpeed.Step)
        {
            rejections.Add(
                $"The speed preset cannot be moved from '{currentSpeed.DisplayName}' to '{proposedSpeed.DisplayName}' while " +
                "recording — a slower preset asks the encoder for more work per frame. " +
                "Stop the recording first, or choose a faster preset instead.");
        }
        else if (proposedSpeed.Step < currentSpeed.Step)
        {
            degrades = true;
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
