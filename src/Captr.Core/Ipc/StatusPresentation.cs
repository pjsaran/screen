namespace Captr.Core.Ipc;

/// <summary>
/// Turns the host's status into what a person should SEE. Owns one rule: a recording
/// that ended on its own — failed, or stopped by an internal error — stays visibly
/// failed until someone acknowledges it or starts again.
/// </summary>
/// <remarks>
/// The host reports "idle" once a session has finished, however it finished, and
/// finalisation usually completes between two of the window's once-a-second polls.
/// So a recording stopped by repeated encoder faults, or by a nearly full disk, went
/// from "finalizing" straight to "idle" — exactly like one somebody stopped — and the
/// window, the tray icon, and the tooltip all said "Idle, nothing is recording". The
/// failure UI existed and was unreachable. <see cref="StatusResponse.LastOutcome"/>
/// carries the ending; this presents it.
/// </remarks>
public static class StatusPresentation
{
    /// <summary>The state name every view already knows how to show as a failure.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The status to display: unchanged, except that an idle recorder whose last
    /// recording failed (and has not been dismissed) is shown as <see cref="Failed"/>.
    /// </summary>
    /// <param name="dismissedSessionId">The failed session the user has already
    /// acknowledged, if any.</param>
    public static StatusResponse ForDisplay(StatusResponse status, Guid? dismissedSessionId) =>
        status is { State: "idle", LastOutcome: { Result: "failed" or "faulted" } outcome }
        && outcome.SessionId != dismissedSessionId
            ? status with { State = Failed, SessionId = outcome.SessionId, WorkingFolder = outcome.WorkingFolder }
            : status;

    /// <summary>
    /// The window's view of the recorder across polls: remembers the last ending even
    /// after the recorder has gone (it exits a couple of minutes after its last
    /// recording, taking <see cref="StatusResponse.LastOutcome"/> with it), and which
    /// failure the person has acknowledged. Not thread-safe; one poll loop owns it.
    /// </summary>
    public sealed class Tracker
    {
        private SessionOutcome? _remembered;
        private Guid? _dismissed;
        private StatusResponse? _lastRaw;

        /// <summary>The status to show for the latest poll result.</summary>
        public StatusResponse Present(StatusResponse raw)
        {
            _lastRaw = raw;
            if (raw.State != "idle")
            {
                // Recording again: whatever happened before is history.
                _remembered = null;
            }
            else if (raw.LastOutcome is not null)
            {
                _remembered = raw.LastOutcome;
            }
            else if (_remembered is not null)
            {
                // Idle with no outcome = no recorder running any more.
                raw = raw with { LastOutcome = _remembered };
            }

            return ForDisplay(raw, _dismissed);
        }

        /// <summary>Acknowledges the failure being shown and returns what to show now.</summary>
        public StatusResponse Dismiss()
        {
            _dismissed = _remembered?.SessionId;
            return Present(_lastRaw ?? new StatusResponse("idle", null, null, null, null, null, null, null, null));
        }
    }

    /// <summary>
    /// True while a recording exists in any form - being set up, running, paused,
    /// suspended while the PC sleeps, or being written out. Start is meaningless then.
    /// </summary>
    /// <remarks>
    /// The views listed the states they knew, and "starting", "suspended" and
    /// "completed" fell through to Idle: the tray offered Start while a start was
    /// already under way, and a recording held across sleep read as "Nothing is
    /// recording".
    /// </remarks>
    public static bool IsActive(string state) =>
        state is "starting" or "recording" or "paused" or "suspended" or "stopping" or "finalizing" or "completed";

    /// <summary>True when Stop means something: the recording is running, paused, or
    /// suspended, and no stop has been asked for yet.</summary>
    public static bool CanStop(string state) => state is "recording" or "paused" or "suspended";

    /// <summary>Elapsed time as hh:mm:ss with TOTAL hours, so a recording past a day
    /// reads 25:00:00 instead of wrapping back to 01:00:00.</summary>
    public static string FormatElapsed(TimeSpan? elapsed) =>
        elapsed is { } value
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}")
            : "";

    /// <summary>One sentence for a failure: what happened, with a next step.</summary>
    public static string DescribeFailure(StatusResponse status) =>
        status.LastOutcome?.Reason is { Length: > 0 } reason
            ? reason
            : "The recording stopped on its own. Everything recorded before it stopped is saved — " +
              "open Recordings to check it, or Diagnostics for the cause.";
}
