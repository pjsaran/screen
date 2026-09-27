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

    /// <summary>One sentence for a failure: what happened, with a next step.</summary>
    public static string DescribeFailure(StatusResponse status) =>
        status.LastOutcome?.Reason is { Length: > 0 } reason
            ? reason
            : "The recording stopped on its own. Everything recorded before it stopped is saved — " +
              "open Recordings to check it, or Diagnostics for the cause.";
}
