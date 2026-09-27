using Windows.Win32;

namespace Captr.Core.Common;

/// <summary>
/// Free space available to this user in the volume holding a folder — ANY folder:
/// a drive letter, a UNC share, or a volume mounted into a folder. Owns the one
/// question the disk guard, the start preflight, and folder transfers all ask.
/// </summary>
/// <remarks>
/// <see cref="DriveInfo"/> was used before, and it cannot answer this: it accepts
/// only a drive letter, so a UNC path (which the user guide advertises for both
/// destinations and the working folder) threw <see cref="ArgumentException"/> — every
/// transfer to a network share failed, and a recording into one could not start. And
/// for a volume mounted at <c>D:\Recordings</c> it measured drive D rather than the
/// volume actually being written. <c>GetDiskFreeSpaceEx</c> takes the folder itself
/// and gets both right; it also reports the space available to THIS user, honouring
/// disk quotas, which is what "will it fit" actually means.
/// </remarks>
public static class FreeSpace
{
    /// <summary>
    /// Test hook: names a file holding a byte count that replaces the real free space
    /// on every check, so the end-to-end suite can run a recording out of disk without
    /// filling one (which needs administrator rights to do safely). Read each time, so
    /// a test can change it mid-recording. Unset in every real installation.
    /// </summary>
    public const string SimulateVariable = "CAPTR_SIMULATE_FREE_BYTES_FILE";

    /// <summary>
    /// Bytes available to the current user on the volume that holds
    /// <paramref name="folder"/>, or null when that cannot be determined (the share
    /// is unreachable, the path is malformed). The folder need not exist yet; its
    /// nearest existing parent is measured.
    /// </summary>
    public static long? AvailableBytes(string folder)
    {
        if (Simulated() is { } simulated)
        {
            return simulated;
        }

        string? probe;
        try
        {
            probe = Path.GetFullPath(folder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Walk up to something that exists: a destination folder is created by the
        // transfer itself, and a new working folder by the first recording.
        while (probe is not null && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        if (probe is null)
        {
            return null;
        }

        // A UNC name must end in a backslash for this API.
        if (!probe.EndsWith(Path.DirectorySeparatorChar))
        {
            probe += Path.DirectorySeparatorChar;
        }

        return PInvoke.GetDiskFreeSpaceEx(probe, out ulong available, out _, out _)
            ? (long)Math.Min(available, long.MaxValue)
            : null;
    }

    private static long? Simulated()
    {
        if (Environment.GetEnvironmentVariable(SimulateVariable) is not { Length: > 0 } file)
        {
            return null;
        }

        try
        {
            return long.TryParse(File.ReadAllText(file).Trim(), System.Globalization.CultureInfo.InvariantCulture, out long bytes)
                ? bytes
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
