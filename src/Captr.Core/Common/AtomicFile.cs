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
    /// How many times a read retries while a replace is in flight. Together with the
    /// growing back-off in <see cref="ReadOrNull"/> this spans about 150 ms, which
    /// comfortably covers the exclusive window ReplaceFile opens even on a machine
    /// under heavy load. Raising it only makes a genuinely locked file slower to
    /// report; lowering it risks reporting a live session as missing.
    /// </summary>
    private const int ReadRetryAttempts = 25;

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
    /// <param name="keepPreviousVersion">
    /// When true, the version being replaced is kept beside the file as
    /// <c>name.bak</c>. The Win32 ReplaceFile API produces it as part of the SAME
    /// atomic operation, so it costs one rename and no extra I/O.
    /// </param>
    public static void Write(string destinationPath, string content, bool keepPreviousVersion = false)
    {
        string tempPath = destinationPath + ".tmp";
        string? backupPath = keepPreviousVersion ? destinationPath + ".bak" : null;

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
                    File.Replace(tempPath, destinationPath, backupPath);
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
            catch (FileNotFoundException) when (attempt < ReadRetryAttempts)
            {
                // Empirically, ReplaceFile leaves a hair-thin window where the
                // destination NAME is absent (observed by the stress test). A file
                // that is genuinely missing is still missing after these retries;
                // one that is being replaced reappears immediately.
                //
                // This shares the FULL retry budget with the IOException case below
                // rather than getting a short one of its own. A flat 5 × 2 ms was not
                // enough on a machine running a parallel build: the window outlasted
                // it, ReadOrNull returned null, and a live heartbeat was reported as
                // a missing session.
                Backoff(attempt);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (UnauthorizedAccessException) when (attempt < ReadRetryAttempts)
            {
                // Opening a file in the middle of ReplaceFile can come back as
                // access-denied rather than as an IOException. Same cause, same fix.
                Backoff(attempt);
            }
            catch (IOException) when (attempt < ReadRetryAttempts)
            {
                // Mid-replace the destination is briefly held exclusively; back off
                // so the retries span the window instead of all landing inside it.
                // The delay grows because the window is longer on a loaded machine:
                // a flat 2 ms × 10 spent its whole budget inside a single stall
                // under parallel builds, which is how this was found. The worst case
                // is still ~150 ms — nothing for a 1 Hz heartbeat reader, and far
                // better than reporting a healthy session as missing.
                Backoff(attempt);
            }
        }
    }

    /// <summary>
    /// Reads the previous version kept by <c>Write(..., keepPreviousVersion: true)</c>,
    /// or null when there is none. Used to recover a file that has gone missing or
    /// become unreadable.
    /// </summary>
    public static string? ReadPreviousVersionOrNull(string path) => ReadOrNull(path + ".bak");

    /// <summary>The growing back-off shared by every retryable read failure, so the
    /// retries span the replace window instead of all landing inside it.</summary>
    private static void Backoff(int attempt) => Thread.Sleep(Math.Min(2 + attempt, 10));
}
