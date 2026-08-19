namespace Captr.Core.Supervision;

/// <summary>
/// The pure decision logic of supervision: is the encoder stalled, was an exit a
/// fault or an external termination, is it time for the single hardware→software
/// fallback, or time to stop loudly. Owns every verdict; the async loop in
/// <see cref="EncoderSupervisor"/> only feeds it observations and executes what it
/// decides — which is what makes the reliability rules of SPEC §6 unit-testable
/// without a single real process.
/// </summary>
public sealed class SupervisorPolicy
{
    private readonly List<DateTimeOffset> _faultTimes = [];
    private DateTimeOffset? _lastProgressUtc;
    private DateTimeOffset _lastGrowthUtc;
    private long _lastFileLength = -1;
    private DateTimeOffset? _slowSince;
    private bool _fellBack;

    /// <summary>Record a progress observation.</summary>
    public void OnProgress(EncoderProgress progress)
    {
        _lastProgressUtc = progress.ObservedUtc;

        if (progress.Speed is { } speed && speed < SupervisionConstants.SlowSpeedThreshold)
        {
            _slowSince ??= progress.ObservedUtc;
        }
        else
        {
            _slowSince = null;
        }
    }

    /// <summary>Record the newest segment file's length, for growth-stall detection.</summary>
    public void OnFileLength(long length, DateTimeOffset nowUtc)
    {
        if (length != _lastFileLength)
        {
            _lastFileLength = length;
            _lastGrowthUtc = nowUtc;
        }
    }

    /// <summary>Reset per-process observations after a (re)launch.</summary>
    public void OnProcessLaunched(DateTimeOffset nowUtc)
    {
        _lastProgressUtc = null;
        _lastFileLength = -1;
        _lastGrowthUtc = nowUtc;
        _slowSince = null;
        LaunchedUtc = nowUtc;
    }

    /// <summary>When the current process was launched (grace period reference).</summary>
    public DateTimeOffset LaunchedUtc { get; private set; }

    /// <summary>
    /// Stall check (SPEC §6: no progress for several seconds; output file not
    /// growing while nominally recording). A freshly launched process gets the
    /// stall timeout as startup grace before its first progress block is due.
    /// </summary>
    public string? DetectStall(DateTimeOffset nowUtc)
    {
        DateTimeOffset progressReference = _lastProgressUtc ?? LaunchedUtc;
        if (nowUtc - progressReference > SupervisionConstants.StallTimeout)
        {
            return $"no progress for {(nowUtc - progressReference).TotalSeconds:F0}s";
        }

        // File growth only counts once a segment exists and progress has started —
        // before that, "not growing" is just "still starting up".
        if (_lastProgressUtc is not null
            && _lastFileLength >= 0
            && nowUtc - _lastGrowthUtc > SupervisionConstants.FileGrowthTimeout)
        {
            return $"output file not growing for {(nowUtc - _lastGrowthUtc).TotalSeconds:F0}s";
        }

        return null;
    }

    /// <summary>True when encoding has been slower than real time for the whole
    /// slow-speed window (SPEC §6: warn, then reduce frame rate).</summary>
    public bool IsSustainedSlow(DateTimeOffset nowUtc) =>
        _slowSince is { } since && nowUtc - since >= SupervisionConstants.SlowSpeedWindow;

    /// <summary>
    /// Classifies an encoder exit (SPEC §6: distinguish external termination from a
    /// fault; only faults count toward fallback). Documented heuristic: we call an
    /// exit a FAULT when we killed it for a stall, or when it exited by itself with
    /// an error status or error lines in its log tail. An exit with a clean log and
    /// a healthy progress stream — the signature of Task Manager's End Task — is
    /// external.
    /// </summary>
    public static ExitKind ClassifyExit(bool stopWasRequested, bool killedForStall, int exitCode, IReadOnlyList<string> logTail)
    {
        if (stopWasRequested)
        {
            return ExitKind.Graceful;
        }

        if (killedForStall)
        {
            return ExitKind.Fault;
        }

        bool logShowsErrors = logTail.Any(static line =>
            line.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Invalid", StringComparison.OrdinalIgnoreCase));

        // Exit code 1 is both "ffmpeg generic error" AND "terminated externally";
        // the log tail is the tiebreaker. Any other nonzero code is a fault.
        if (exitCode == 0 || (exitCode == 1 && !logShowsErrors))
        {
            return ExitKind.External;
        }

        return logShowsErrors || exitCode != 1 ? ExitKind.Fault : ExitKind.External;
    }

    /// <summary>Records a fault and answers what to do next: continue restarting,
    /// take the single fallback, or stop loudly (SPEC §6: one fallback, no second
    /// rung, never a capture-method change).</summary>
    public FaultVerdict OnFault(DateTimeOffset nowUtc)
    {
        _faultTimes.Add(nowUtc);
        _faultTimes.RemoveAll(t => nowUtc - t > SupervisionConstants.FaultWindow);

        if (_faultTimes.Count < SupervisionConstants.FaultThreshold)
        {
            return FaultVerdict.Restart;
        }

        if (!_fellBack)
        {
            _fellBack = true;
            _faultTimes.Clear();
            return FaultVerdict.FallBackToSoftware;
        }

        return FaultVerdict.StopLoudly;
    }

    /// <summary>True once the fallback has been taken.</summary>
    public bool HasFallenBack => _fellBack;
}

/// <summary>How an encoder exit is classified.</summary>
public enum ExitKind
{
    /// <summary>We asked it to stop.</summary>
    Graceful,

    /// <summary>The encoder failed (or we killed it for stalling). Counts toward fallback.</summary>
    Fault,

    /// <summary>Something outside Captr ended it. Restart, but do not count it (SPEC §6).</summary>
    External,
}

/// <summary>What to do after a fault.</summary>
public enum FaultVerdict
{
    Restart,
    FallBackToSoftware,
    StopLoudly,
}
