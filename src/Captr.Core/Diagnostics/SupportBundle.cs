using System.IO.Compression;
using System.Text;

using Captr.Core.Secrets;
using Captr.Core.Sessions;

namespace Captr.Core.Diagnostics;

/// <summary>
/// Builds the support bundle (SPEC §9): journal, logs, integrity record, encoder log
/// tail, and system information — and NOTHING else. Owns the two exclusions the
/// spec verifies by test: no video content (media files are never added) and no
/// secrets (every text file is scanned for suspect key names before inclusion, and
/// matching lines are masked).
/// </summary>
public static class SupportBundle
{
    /// <summary>Files worth bundling from a session folder — never media.</summary>
    private static readonly string[] SessionFilePatterns =
        [SessionJournal.FileName, IntegrityRecord.FileName, HeartbeatSnapshot.FileName, "ffmpeg-report.log", "progress.txt"];

    /// <summary>
    /// Writes a zip containing diagnostics for every session under
    /// <paramref name="workingRoot"/> plus the application logs.
    /// </summary>
    public static async Task CreateAsync(
        string bundlePath, string workingRoot, string logFolder, CancellationToken cancellationToken)
    {
        // VSTHRD103 pattern-matches the method NAME "Open"; ZipFile.Open and
        // ZipArchiveEntry.Open have no async counterparts and do trivial local I/O.
#pragma warning disable VSTHRD103
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);

        await AddTextEntryAsync(archive, "system-info.txt", BuildSystemInfo(), cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(logFolder))
        {
            foreach (string logFile in Directory.GetFiles(logFolder, "*.log"))
            {
                await AddScrubbedFileAsync(archive, logFile, "logs/" + Path.GetFileName(logFile), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (Directory.Exists(workingRoot))
        {
            foreach (string sessionFolder in Directory.GetDirectories(workingRoot))
            {
                string sessionName = Path.GetFileName(sessionFolder);
                foreach (string pattern in SessionFilePatterns)
                {
                    string filePath = Path.Combine(sessionFolder, pattern);
                    if (File.Exists(filePath))
                    {
                        await AddScrubbedFileAsync(
                            archive, filePath, $"sessions/{sessionName}/{pattern}", cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds a text file with secret scrubbing: any line containing a suspect key
    /// name has everything after the name masked. Belt-and-braces — the redaction
    /// policy should have kept secrets out of these files in the first place, but a
    /// diagnostics bundle leaves the machine, so it gets its own gate (SPEC §9).
    /// </summary>
    private static async Task AddScrubbedFileAsync(
        ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return; // A live file mid-rotation — skip rather than fail the bundle.
        }

        var scrubbed = new StringBuilder();
        foreach (string line in lines)
        {
            scrubbed.AppendLine(ScrubLine(line));
        }

        await AddTextEntryAsync(archive, entryName, scrubbed.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Masks the remainder of any line mentioning a secret-suggestive key.</summary>
    internal static string ScrubLine(string line)
    {
        foreach (string word in line.Split(' ', '\t', '"', '\'', '=', ':'))
        {
            if (word.Length > 2 && SecretRedaction.IsSuspectName(word))
            {
                int index = line.IndexOf(word, StringComparison.Ordinal);
                return line[..(index + word.Length)] + " " + SecretRedaction.Mask;
            }
        }

        return line;
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive, string entryName, string content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
    }
#pragma warning restore VSTHRD103

    private static string BuildSystemInfo() =>
        $"""
         Captr support bundle
         Created (UTC): {DateTimeOffset.UtcNow:O}
         Machine: {Environment.MachineName}
         User: {Environment.UserName}
         OS: {Environment.OSVersion}
         64-bit: {Environment.Is64BitOperatingSystem}
         Processors: {Environment.ProcessorCount}
         App version: {typeof(SupportBundle).Assembly.GetName().Version}
         CLR: {Environment.Version}
         """;
}
