using Captr.Core.Settings;
using Captr.Core.Supervision;
using Captr.Core.WindowsEvents;

namespace Captr.Core.Sessions;

/// <summary>
/// What the host needs from a recording session: its identity, its live state, the
/// commands it accepts, and the run that ends in finalisation. <see cref="RecordingSession"/>
/// is the only production implementation; the interface exists so the host's own
/// rules — one start at a time, a finishing session never blocking the next one —
/// can be tested without launching an encoder or reserving half a gigabyte of ballast.
/// </summary>
public interface IRecordingSession
{
    RecordingSession.SessionContext Context { get; }

    SessionState State { get; }

    EncoderProgress? LatestProgress { get; }

    int CurrentFrameRate { get; }

    string CurrentQuality { get; }

    string CurrentSpeedPreset { get; }

    IReadOnlyList<string> CurrentExcludedDisplayIds { get; }

    /// <summary>Why the session ended in failure, in words for the user; null when it
    /// has not failed.</summary>
    string? FailureReason { get; }

    void RequestStop();

    void RequestPause();

    void RequestResume();

    void ApplyDegradation(CaptrSettings degraded);

    void AttachSystemEvents(MessageOnlyWindow events);

    Task<FinalizationResult> RunAsync(CancellationToken hostShutdown);
}
