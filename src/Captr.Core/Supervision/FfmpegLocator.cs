namespace Captr.Core.Supervision;

/// <summary>
/// Finds the bundled FFmpeg binaries. Owns the search order; if it fails, nothing
/// records. The application NEVER uses an ffmpeg from PATH — only the pinned,
/// checksum-verified build we ship (SPEC §2: exact build identifier; a random
/// system ffmpeg may be a GPL build or lack ddagrab).
/// </summary>
public static class FfmpegLocator
{
    /// <summary>Full path to ffmpeg.exe. Throws with instructions when missing.</summary>
    public static string FindFfmpeg() => Find("ffmpeg.exe");

    /// <summary>Full path to ffprobe.exe. Throws with instructions when missing.</summary>
    public static string FindFfprobe() => Find("ffprobe.exe");

    private static string Find(string binaryName)
    {
        foreach (string candidate in CandidatePaths(binaryName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"{binaryName} was not found. In an installation it lives in the 'ffmpeg' folder " +
            "beside Captr.App.exe; in a development tree run 'pwsh build/fetch-ffmpeg.ps1' once to " +
            "fetch the pinned, checksum-verified build into tools/ffmpeg/bin.");
    }

    private static IEnumerable<string> CandidatePaths(string binaryName)
    {
        // 1. Installed layout: <install dir>\ffmpeg\ffmpeg.exe
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", binaryName);

        // 2. Development layout: walk up from bin/Debug/... to the repo root's
        //    tools\ffmpeg\bin (created by build/fetch-ffmpeg.ps1).
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "bin", binaryName);
        }
    }
}
