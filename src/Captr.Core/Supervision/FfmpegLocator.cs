using System.Collections.Concurrent;
using System.Text.Json;

namespace Captr.Core.Supervision;

/// <summary>
/// Finds the bundled FFmpeg binaries and proves they are the pinned build before
/// anything runs them. Owns the search order and the integrity check; if it fails,
/// nothing records. The application NEVER uses an ffmpeg from PATH — only the pinned
/// build we ship (SPEC §2: exact build identifier).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it looks.</b> Beside the application (the installed layout), and — only
/// inside a Captr source checkout, recognised by <c>Captr.slnx</c> — in that
/// checkout's <c>tools\ffmpeg\bin</c>. The development search once walked up eight
/// parent folders unconditionally, so from <c>C:\Program Files\Captr\</c> it reached
/// <c>C:\tools\ffmpeg\bin\ffmpeg.exe</c> — a path any standard user may create. With
/// the bundled binary missing (antivirus quarantines ffmpeg often enough), every
/// user's recording would have run that planted program instead.
/// </para>
/// <para>
/// <b>What it checks.</b> <c>capabilities.json</c> beside the binaries records the
/// <see cref="PeImageDigest"/> of each, computed by <c>build/fetch-ffmpeg.ps1</c>
/// straight after the pinned download's SHA-256 was verified. The digest ignores the
/// Authenticode signature, so it still matches after release signing, and any other
/// change to the file does not. A binary that does not match — or has no record to
/// match — is refused. The check runs once per file per process.
/// </para>
/// </remarks>
public static class FfmpegLocator
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc)> Verified =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full path to the verified ffmpeg.exe. Throws with instructions when it
    /// is missing (<see cref="FileNotFoundException"/>) or not the pinned build
    /// (<see cref="FfmpegIntegrityException"/>).</summary>
    public static string FindFfmpeg() => Find(AppContext.BaseDirectory, "ffmpeg.exe");

    /// <summary>Full path to the verified ffprobe.exe; see <see cref="FindFfmpeg"/>.</summary>
    public static string FindFfprobe() => Find(AppContext.BaseDirectory, "ffprobe.exe");

    internal static string Find(string baseDirectory, string binaryName)
    {
        foreach (string candidate in CandidatePaths(baseDirectory, binaryName))
        {
            if (File.Exists(candidate))
            {
                Verify(candidate);
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"{binaryName} was not found. In an installation it lives in the 'ffmpeg' folder " +
            "beside Captr.App.exe — reinstall Captr to restore it (antivirus software sometimes removes it; " +
            "if so, ask for Captr's folder to be allowed). In a development tree run 'pwsh build/fetch-ffmpeg.ps1' " +
            "once to fetch the pinned, checksum-verified build into tools/ffmpeg/bin.");
    }

    internal static IEnumerable<string> CandidatePaths(string baseDirectory, string binaryName)
    {
        // 1. Installed layout: <install dir>\ffmpeg\ffmpeg.exe
        yield return Path.Combine(baseDirectory, "ffmpeg", binaryName);

        // 2. Development layout, and ONLY inside a source checkout: from bin\Release\…
        //    up to the folder holding Captr.slnx, then its tools\ffmpeg\bin.
        for (DirectoryInfo? directory = new(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Captr.slnx")))
            {
                yield return Path.Combine(directory.FullName, "tools", "ffmpeg", "bin", binaryName);
                yield break;
            }
        }
    }

    /// <summary>Throws <see cref="FfmpegIntegrityException"/> unless the file's
    /// signature-independent digest matches the one recorded for it.</summary>
    internal static void Verify(string binaryPath)
    {
        var file = new FileInfo(binaryPath);
        (long Length, DateTime WrittenUtc) stamp = (file.Length, file.LastWriteTimeUtc);
        if (Verified.TryGetValue(file.FullName, out var known) && known == stamp)
        {
            return;
        }

        string? expected = ExpectedDigest(binaryPath);
        if (expected is null)
        {
            throw new FfmpegIntegrityException(
                $"{file.Name} cannot be verified: there is no record of the pinned build beside it " +
                $"({Path.GetDirectoryName(binaryPath)}). Reinstall Captr; in a development tree run " +
                "'pwsh build/fetch-ffmpeg.ps1', which records it.");
        }

        string actual = PeImageDigest.Compute(binaryPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new FfmpegIntegrityException(
                $"{binaryPath} is not the FFmpeg build Captr ships — it has been changed or damaged — so Captr " +
                "will not run it. Reinstall Captr to restore it.");
        }

        Verified[file.FullName] = stamp;
    }

    /// <summary>The recorded digest for this binary, from the capabilities.json beside
    /// it (installed layout) or one folder up (tools\ffmpeg in a development tree).</summary>
    private static string? ExpectedDigest(string binaryPath)
    {
        string folder = Path.GetDirectoryName(binaryPath)!;
        foreach (string capabilities in new[]
                 {
                     Path.Combine(folder, "capabilities.json"),
                     Path.Combine(folder, "..", "capabilities.json"),
                 })
        {
            if (!File.Exists(capabilities))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(capabilities));
                if (document.RootElement.TryGetProperty("binaryDigests", out JsonElement digests)
                    && digests.TryGetProperty(Path.GetFileName(binaryPath), out JsonElement digest))
                {
                    return digest.GetString();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>The FFmpeg found is not the pinned build Captr ships (or cannot be shown
/// to be). Nothing runs it.</summary>
public sealed class FfmpegIntegrityException(string message) : IOException(message);
