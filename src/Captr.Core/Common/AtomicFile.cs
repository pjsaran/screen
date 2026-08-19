namespace Captr.Core.Common;

/// <summary>
/// Writes files so that a reader can never observe a torn (half-written) state, and a
/// crash mid-write can never destroy the previous content. Owns the
/// write-to-temp-then-rename pattern used by the heartbeat and the settings store.
/// If this class is wrong, a crash at the worst moment corrupts settings or leaves a
/// reader parsing half a heartbeat — which is why its atomicity is pinned by a
/// dedicated concurrent stress test instead of being assumed (SPEC §6).
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Atomically replaces <paramref name="destinationPath"/> with
    /// <paramref name="content"/> (UTF-8, no BOM).
    /// </summary>
    /// <remarks>
    /// How it works, and why each step exists:
    /// <list type="number">
    /// <item>Write to a temporary file <c>name.tmp</c> in the SAME directory — a rename
    /// is only atomic within one volume, so the temp file must live beside the
    /// destination, never in %TEMP%.</item>
    /// <item>Flush to physical disk before the rename. Without this, the rename can
    /// land on disk before the data does, and a power cut leaves a correctly-named
    /// file with garbage inside.</item>
    /// <item>Rename over the destination with <see cref="File.Move(string,string,bool)"/>,
    /// which maps to Win32 MoveFileEx(MOVEFILE_REPLACE_EXISTING) — on NTFS a reader
    /// opening the path sees either the old file or the new one, never a mixture.</item>
    /// </list>
    /// </remarks>
    public static void Write(string destinationPath, string content)
    {
        string tempPath = destinationPath + ".tmp";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // MEASURED, not assumed (SPEC §6 demands the atomicity be pinned by a test):
        // File.Move(..., overwrite: true) — MoveFileEx(REPLACE_EXISTING) — FAILS with
        // access-denied whenever ANY reader holds the destination open, even with
        // full sharing (1130 failures out of 2000 in the stress test). File.Replace —
        // the Win32 ReplaceFile API — is designed for replace-while-open and scored
        // 0 failures out of 2000 under identical contention. Replace requires the
        // destination to exist, so the very first write uses Move.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, destinationPath);
                }

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                // First-creation race or an external tool (AV scanner, editor)
                // holding a handle without sharing — brief backoff, then retry.
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * (attempt + 1)));
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * (attempt + 1)));
            }
        }
    }

    /// <summary>
    /// Reads a file previously written by <see cref="Write"/>, returning
    /// <see langword="null"/> if it does not exist yet.
    /// </summary>
    public static string? ReadOrNull(string path)
    {
        // FileShare.Delete is the load-bearing flag: without it, our open handle
        // would block the writer's rename (Windows requires delete access on the
        // destination to replace it) and the writer would stall or fail. With it,
        // reads and replaces coexist freely.
        const FileShare shareWithReplacer = FileShare.ReadWrite | FileShare.Delete;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, shareWithReplacer);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) when (attempt < 5)
            {
                // Empirically, ReplaceFile leaves a hair-thin window where the
                // destination NAME is absent (observed by the stress test). A file
                // that is genuinely missing is still missing after these retries;
                // one that is being replaced reappears immediately.
                Thread.Sleep(2);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 10)
            {
                // Mid-replace the destination is briefly held exclusively; back off
                // so the retries span the window instead of all landing inside it.
                // Worst case this costs ~20 ms — nothing for a 1 Hz heartbeat reader.
                Thread.Sleep(2);
            }
        }
    }
}
