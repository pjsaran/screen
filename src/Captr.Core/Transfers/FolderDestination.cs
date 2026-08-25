using Captr.Core.Naming;
using Captr.Core.Sessions;

namespace Captr.Core.Transfers;

/// <summary>
/// Transfer to a local or network folder (SPEC §7): check free space, copy under a
/// temporary name with progress and cancellation, verify by RE-READING and
/// comparing size and hash, then rename into place atomically — suffixing on
/// collision, never overwriting. Owns the verify step; a copy that was not read
/// back is a copy that was not verified.
/// </summary>
public static class FolderDestination
{
    /// <summary>Copies, verifies, and lands one file. Returns the final path.</summary>
    public static async Task<string> TransferAsync(
        string sourcePath, string targetFolder, string desiredFileName,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetFolder);

        long sourceBytes = new FileInfo(sourcePath).Length;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetFolder))!);
        if (drive.AvailableFreeSpace < sourceBytes + (64L << 20))
        {
            throw new IOException(
                $"Not enough free space on {drive.Name} for {sourceBytes / 1_000_000.0:F0} MB " +
                "plus margin. The local recording is untouched.");
        }

        // Land under the collision-resolved FINAL name + .partial, so a crashed
        // copy is recognisable and the final rename is a same-volume atomic move.
        string finalPath = OutputNamer.ResolveCollision(targetFolder, desiredFileName);
        string partialPath = finalPath + ".partial";

        string sourceHash;
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            sourceHash = await CopyWithHashAsync(source, target, sourceBytes, progress, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Verify by re-reading the DESTINATION (SPEC §7): size and hash must match
        // what left the source — this is what catches a lying network filesystem.
        var landed = new FileInfo(partialPath);
        if (landed.Length != sourceBytes)
        {
            File.Delete(partialPath);
            throw new IOException($"Verification failed: copied size {landed.Length} != source size {sourceBytes}.");
        }

        string landedHash = await FinalizationPipeline.HashFileAsync(partialPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(landedHash, sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partialPath);
            throw new IOException("Verification failed: the copied file's hash differs from the source.");
        }

        File.Move(partialPath, finalPath);
        return finalPath;
    }

    /// <summary>Copies while hashing the source stream in one pass.</summary>
    private static async Task<string> CopyWithHashAsync(
        Stream source, Stream target, long totalBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            progress?.Report(totalBytes == 0 ? 1 : copied / (double)totalBytes);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
