namespace Captr.Core.Sessions;

/// <summary>
/// Disk-space protection for a session (SPEC §6): refuse to start without measured
/// headroom, hold a ballast file so a clean finalisation always has room, express
/// warnings in minutes of recording remaining (never bytes), and call for a clean
/// stop before the disk actually fills. Owns the thresholds; if it is wrong the
/// session ends in a disk-full crash instead of a clean stop — and "a clean stop
/// beats a disk-full crash" is the whole point.
/// </summary>
public sealed class DiskGuard
{
    /// <summary>Minimum recording time the free space must cover to START, on top of
    /// the ballast. Generous because the estimate comes from an 8 s trial of
    /// screen content that may get busier.</summary>
    public static readonly TimeSpan StartHeadroom = TimeSpan.FromMinutes(30);

    /// <summary>Ballast reserved at session start and released when space becomes
    /// critical, guaranteeing room to close segments, write the integrity record,
    /// and finalise cleanly. 512 MB comfortably covers all of that.</summary>
    public const long BallastBytes = 512L * 1024 * 1024;

    /// <summary>Below this many minutes remaining: warn (well ahead, SPEC §6).</summary>
    public static readonly TimeSpan WarnThreshold = TimeSpan.FromMinutes(30);

    /// <summary>Below this: release ballast, close the segment, finalise, stop.</summary>
    public static readonly TimeSpan CriticalThreshold = TimeSpan.FromMinutes(5);

    private readonly string _workingFolder;
    private readonly long _bytesPerHour;
    private string? _ballastPath;

    /// <param name="bytesPerHour">The measured rate from the size estimator's real
    /// trial (SPEC §6: "measure the actual encoding rate with a trial encode").</param>
    public DiskGuard(string workingFolder, long bytesPerHour)
    {
        _workingFolder = workingFolder;
        _bytesPerHour = Math.Max(1, bytesPerHour);
    }

    /// <summary>Recording time the given free space allows at the measured rate.</summary>
    public TimeSpan MinutesRemaining(long freeBytes) =>
        TimeSpan.FromHours(Math.Max(0, freeBytes - BallastBytes) / (double)_bytesPerHour);

    /// <summary>
    /// The pre-start gate: refuses with a concrete message stating what is needed
    /// when free space cannot cover the ballast plus the start headroom.
    /// </summary>
    public PreflightResult Preflight(long freeBytes)
    {
        long neededBytes = BallastBytes + (long)(_bytesPerHour * StartHeadroom.TotalHours);
        if (freeBytes >= neededBytes)
        {
            return new PreflightResult(true, null);
        }

        double neededGb = neededBytes / 1_000_000_000.0;
        double freeGb = freeBytes / 1_000_000_000.0;
        return new PreflightResult(false,
            $"Not enough disk space to start recording safely: {freeGb:F1} GB free, but at the measured " +
            $"rate ({_bytesPerHour / 1_000_000_000.0:F1} GB/hour) at least {neededGb:F1} GB is needed for " +
            $"{StartHeadroom.TotalMinutes:F0} minutes of recording plus the finalisation reserve. " +
            "Free some space or move the working folder to another drive.");
    }

    /// <summary>Reserves the ballast file. Call once at session start.</summary>
    public void ReserveBallast()
    {
        _ballastPath = Path.Combine(_workingFolder, "ballast.bin");
        Directory.CreateDirectory(_workingFolder);
        using var stream = new FileStream(_ballastPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(BallastBytes);
    }

    /// <summary>Releases the ballast — the emergency room for a clean finalisation.</summary>
    public void ReleaseBallast()
    {
        if (_ballastPath is not null && File.Exists(_ballastPath))
        {
            File.Delete(_ballastPath);
            _ballastPath = null;
        }
    }

    /// <summary>Classifies the current free space. The session engine polls this and
    /// acts: Warning surfaces minutes remaining plus the one-click mitigations;
    /// Critical releases ballast and stops cleanly (SPEC §6).</summary>
    public DiskVerdict Check(long freeBytes)
    {
        TimeSpan remaining = MinutesRemaining(freeBytes);
        if (remaining <= CriticalThreshold)
        {
            return new DiskVerdict(DiskState.Critical, remaining);
        }

        return remaining <= WarnThreshold
            ? new DiskVerdict(DiskState.Warning, remaining)
            : new DiskVerdict(DiskState.Ok, remaining);
    }

    /// <summary>Free bytes on the volume that holds the working folder.</summary>
    public long FreeBytesOnVolume() => new DriveInfo(Path.GetPathRoot(_workingFolder)!).AvailableFreeSpace;
}

/// <summary>Pre-start gate result; the message states exactly what is needed.</summary>
public sealed record PreflightResult(bool CanStart, string? RefusalMessage);

/// <summary>Current disk situation, expressed in recording time (SPEC §6: minutes,
/// not bytes).</summary>
public sealed record DiskVerdict(DiskState State, TimeSpan RecordingTimeRemaining);

public enum DiskState
{
    Ok,
    Warning,
    Critical,
}
