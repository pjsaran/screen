using System.Diagnostics;

namespace Captr.Core.Supervision;

/// <summary>
/// After a host crash, finds the encoder process the previous host launched and is
/// hopefully still writing segments (SPEC §4: "if the host dies, FFmpeg keeps
/// writing and is re-adopted when the host restarts"). Owns the identity match; if
/// it matches the wrong process, the host would kill an innocent bystander — which
/// is why the match requires three factors, not a PID.
/// </summary>
public static class ProcessAdoption
{
    /// <summary>
    /// Attempts to find the process described by a journal's
    /// <c>EncoderProcessLaunched</c> event. Returns null when it is gone (the normal
    /// case after a reboot) or when anything about the identity fails to match.
    /// </summary>
    /// <param name="processId">PID recorded at launch.</param>
    /// <param name="processStartTimeUtc">Start time recorded at launch — Windows
    /// reuses PIDs, and PID + start time is unique for the machine's uptime.</param>
    /// <param name="imagePath">Image path recorded at launch — guards against the
    /// astronomically unlucky case of a reused PID with a matching start time.</param>
    public static FfmpegProcess? TryAdopt(int processId, DateTimeOffset processStartTimeUtc, string imagePath)
    {
        Process candidate;
        try
        {
            candidate = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null; // No such PID — the encoder is gone.
        }

        try
        {
            // Start times are compared with one second of slack: the value round-trips
            // through local time and file formats, and sub-second drift is possible.
            var candidateStart = new DateTimeOffset(candidate.StartTime.ToUniversalTime());
            bool startTimeMatches =
                (candidateStart - processStartTimeUtc).Duration() < TimeSpan.FromSeconds(1);

            bool imageMatches = string.Equals(
                candidate.MainModule?.FileName, imagePath, StringComparison.OrdinalIgnoreCase);

            if (startTimeMatches && imageMatches && !candidate.HasExited)
            {
                return FfmpegProcess.Adopt(candidate);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited while we probed it, or belongs to another user (so
            // it is not ours by definition). Either way: no adoption.
        }

        candidate.Dispose();
        return null;
    }
}
