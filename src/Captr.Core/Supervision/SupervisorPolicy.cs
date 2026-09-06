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
    private long _lastReportedTotalBytes;
    private DateTimeOffset? _slowSince;
    private bool _fellBack;
    private int _consecutiveCaptureLosses;

    /// <summary>Record a progress observation.</summary>
    public void OnProgress(EncoderProgress progress)
    {
        _lastProgressUtc = progress.ObservedUtc;
        _lastReportedTotalBytes = progress.TotalSizeBytes;

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

        // File growth only counts as a stall when (a) a segment exists and progress
        // started, (b) nothing reached the disk for the whole timeout, AND (c) the
        // encoder ITSELF claims to have produced far more bytes than the disk shows.
        // Gate (c) is what stops FFmpeg's 512 KB output buffering from masquerading
        // as a stall on low-bitrate (static-screen) content — see the constant's
        // remarks for how that false positive was found.
        if (_lastProgressUtc is not null
            && _lastFileLength >= 0
            && nowUtc - _lastGrowthUtc > SupervisionConstants.FileGrowthTimeout
            && _lastReportedTotalBytes - _lastFileLength > SupervisionConstants.FileGrowthSlackBytes)
        {
            return $"output file not growing for {(nowUtc - _lastGrowthUtc).TotalSeconds:F0}s " +
                   $"while the encoder reports {(_lastReportedTotalBytes - _lastFileLength) / 1024} KB produced";
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

        // Capture access lost is NOT an encoder fault: the desktop was temporarily
        // taken away (UAC's secure desktop, a session switch, an RDP transition).
        // SPEC §6 says tolerate these with backoff rather than restarting
        // aggressively — and they must never push the session toward the software
        // encoder, which would not help in the slightest.
        if (IndicatesCaptureAccessLost(logTail))
        {
            return ExitKind.CaptureAccessLost;
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

    /// <summary>
    /// How long to wait before relaunching after capture access was lost. Backs off
    /// 1s, 2s, 4s… to a ceiling, because the desktop may be unavailable for as long
    /// as a user stares at a UAC prompt, and hammering DXGI for minutes helps
    /// nobody (SPEC §6). Any successful progress resets it.
    /// </summary>
    public TimeSpan NextCaptureRetryDelay()
    {
        TimeSpan delay = TimeSpan.FromSeconds(Math.Min(
            SupervisionConstants.CaptureRetryCeiling.TotalSeconds,
            Math.Pow(2, _consecutiveCaptureLosses)));
        _consecutiveCaptureLosses++;
        return delay;
    }

    /// <summary>Called when the encoder produces progress again — the desktop is
    /// back, so the next loss starts its backoff from the beginning.</summary>
    public void OnCaptureRecovered() => _consecutiveCaptureLosses = 0;

    /// <summary>
    /// Log signatures of "the desktop was taken away from us" — a capture failure,
    /// not encoder trouble. Deliberately NARROW: matching any line that merely
    /// mentions ddagrab or gdigrab would classify genuine capture faults as
    /// transient and quietly disable the fallback ladder, so only these specific
    /// access failures count.
    /// </summary>
    /// <remarks>
    /// MEASURED, not imagined. Every entry below is a verbatim format string taken
    /// out of the ffmpeg.exe we ship — verify with:
    /// <c>grep -c "Failed to capture image" tools/ffmpeg/bin/ffmpeg.exe</c>.
    /// <para>
    /// An earlier version of this list matched <c>ACCESS_LOST</c>,
    /// <c>ACCESS_DENIED</c>, and <c>"Failed to duplicate output"</c>. FFmpeg prints
    /// none of those three, so the whole tolerate-with-backoff path was unreachable
    /// and every lost desktop was booked as an encoder fault instead. On a machine
    /// with no hardware encoder — an AWS WorkSpace — three of them inside five
    /// minutes ended the recording outright. Anything added here MUST be pasted
    /// from the binary, never from memory.
    /// </para>
    /// </remarks>
    private static readonly string[] Signatures =
    [
        // ddagrab / DXGI Desktop Duplication: the secure desktop (UAC) owns the
        // screen, or the duplication could not be handed back after a switch.
        "Desktop duplication access denied",
        "Failed duplicating output",
        "Failed querying IDXGIOutput1",

        // gdigrab / GDI — the capture method used where Desktop Duplication does
        // not exist (AWS WorkSpaces, Citrix, some VMs). When the session is
        // disconnected or locked there is no desktop to copy pixels FROM, so the
        // blit and the device-context calls simply fail.
        "Failed to capture image",
        "Couldn't get window device context",
        "Couldn't get window rectangle",
    ];

    /// <summary>
    /// The signatures above, exposed so a test can assert that every one of them is
    /// still a literal string inside the FFmpeg binary we ship. That test is the
    /// only thing standing between this list and silently rotting again the next
    /// time the pinned build changes its wording.
    /// </summary>
    public static IReadOnlyList<string> CaptureAccessLostSignatures => Signatures;

    private static bool IndicatesCaptureAccessLost(IReadOnlyList<string> logTail) =>
        logTail.Any(static line => Signatures.Any(
            signature => line.Contains(signature, StringComparison.OrdinalIgnoreCase)));
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

    /// <summary>The desktop was momentarily unavailable — UAC's secure desktop, a
    /// session switch, a remote-desktop transition. Restart AFTER a backoff, and
    /// never count it toward the fallback: a different encoder cannot help when the
    /// problem is that there is nothing to capture (SPEC §6).</summary>
    CaptureAccessLost,
}

/// <summary>What to do after a fault.</summary>
public enum FaultVerdict
{
    Restart,
    FallBackToSoftware,
    StopLoudly,
}
