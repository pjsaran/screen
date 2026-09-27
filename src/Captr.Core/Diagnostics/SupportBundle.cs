using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using Captr.Core.Secrets;
using Captr.Core.Sessions;
using Captr.Core.Settings;

namespace Captr.Core.Diagnostics;

/// <summary>
/// Builds the support bundle (SPEC §9): journal, logs, integrity record, encoder log
/// tail, and system information — and NOTHING else. Owns the two exclusions the
/// spec verifies by test: no video content (media files are never added) and no
/// secrets (every text file is scanned for suspect key names before inclusion, and
/// matching lines are masked).
/// </summary>
/// <remarks>
/// A bundle is sent to someone else, so it also leaves out who and where by
/// default: the Windows user name, the PC name, the profile folder, and the
/// SharePoint tenant, app and site, and network server names from the settings are
/// replaced with placeholders. A README inside says exactly what is included and
/// what was replaced, so the person can check before sending it.
/// </remarks>
public static partial class SupportBundle
{
    /// <summary>Files worth bundling from a session folder — never media.</summary>
    private static readonly string[] SessionFilePatterns =
        [SessionJournal.FileName, IntegrityRecord.FileName, HeartbeatSnapshot.FileName, "ffmpeg-report.log", "progress.txt"];

    /// <summary>
    /// Writes a zip containing diagnostics for every session under
    /// <paramref name="workingRoot"/> plus the application logs.
    /// </summary>
    /// <param name="settings">The current settings, whose SharePoint and network
    /// identifiers are replaced with placeholders; null to skip that part.</param>
    /// <returns>What went in, and what could not be read.</returns>
    public static async Task<SupportBundleContents> CreateAsync(
        string bundlePath, string workingRoot, string logFolder, CancellationToken cancellationToken,
        CaptrSettings? settings = null)
    {
        IReadOnlyList<(Regex Pattern, string Placeholder)> identities = IdentitiesToReplace(settings);
        var included = new List<string>();
        var unreadable = new List<string>();

        // VSTHRD103 pattern-matches the method NAME "Open"; ZipFile.Open and
        // ZipArchiveEntry.Open have no async counterparts and do trivial local I/O.
#pragma warning disable VSTHRD103
        using (var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create))
        {
            await AddTextEntryAsync(archive, "system-info.txt", Pseudonymise(BuildSystemInfo(), identities), cancellationToken)
                .ConfigureAwait(false);
            included.Add("system-info.txt");

            if (Directory.Exists(logFolder))
            {
                foreach (string logFile in Directory.GetFiles(logFolder, "*.log").Concat(Directory.GetFiles(logFolder, "crash-*.txt")))
                {
                    await AddScrubbedFileAsync(archive, logFile, "logs/" + Path.GetFileName(logFile), identities,
                        included, unreadable, cancellationToken).ConfigureAwait(false);
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
                            await AddScrubbedFileAsync(archive, filePath, $"sessions/{sessionName}/{pattern}", identities,
                                included, unreadable, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            await AddTextEntryAsync(archive, "README.txt", DescribeContents(included, unreadable), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SupportBundleContents(bundlePath, included, unreadable);
    }

    /// <summary>The one-paragraph answer to "what am I about to send?" - shown by the
    /// window after a bundle is made, and written inside it as README.txt.</summary>
    public static string Summary =>
        "It holds Captr's logs, crash reports, recording journals and integrity records, and basic system " +
        "details. It holds no video and no secrets, and your Windows user name, PC name, profile folder, and " +
        "SharePoint tenant, app and site names are replaced with placeholders. Open it to check before you send it.";

    private static string DescribeContents(List<string> included, List<string> unreadable) =>
        "Captr support bundle" + Environment.NewLine + Environment.NewLine + Summary + Environment.NewLine +
        Environment.NewLine + "Files:" + Environment.NewLine +
        string.Concat(included.Select(name => "  " + name + Environment.NewLine)) +
        (unreadable.Count == 0
            ? ""
            : Environment.NewLine + "Could not be read, so left out:" + Environment.NewLine +
              string.Concat(unreadable.Select(name => "  " + name + Environment.NewLine)));

    /// <summary>
    /// Adds a text file with secret scrubbing: any line containing a suspect key
    /// name has everything after the name masked. Belt-and-braces — the redaction
    /// policy should have kept secrets out of these files in the first place, but a
    /// diagnostics bundle leaves the machine, so it gets its own gate (SPEC §9).
    /// </summary>
    private static async Task AddScrubbedFileAsync(
        ZipArchive archive, string sourcePath, string entryName, IReadOnlyList<(Regex Pattern, string Placeholder)> identities,
        List<string> included, List<string> unreadable, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            // Shared with the writer: the recorder's log, and a live session's journal,
            // are open for writing while Captr runs. Reading them the default way hit a
            // sharing violation and silently left out today's log - the one that
            // matters - whenever the recorder was running.
            await using var stream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unreadable.Add(entryName + " (" + exception.Message + ")");
            return;
        }

        var scrubbed = new StringBuilder();
        foreach (string line in text.Split('\n'))
        {
            scrubbed.AppendLine(Pseudonymise(ScrubLine(line.TrimEnd('\r')), identities));
        }

        await AddTextEntryAsync(archive, entryName, scrubbed.ToString(), cancellationToken).ConfigureAwait(false);
        included.Add(entryName);
    }

    /// <summary>
    /// Masks the remainder of any line mentioning a secret-suggestive key, and any
    /// bearer token (a JWT - Graph's pre-authenticated upload URLs carry one in
    /// "tempauth") wherever it appears.
    /// </summary>
    /// <remarks>
    /// Whole words, not substrings: matching "key" anywhere masked the rest of every
    /// FFmpeg command line at "-force_key_frames", hiding exactly the arguments a
    /// support engineer needs.
    /// </remarks>
    internal static string ScrubLine(string line)
    {
        line = JsonWebToken().Replace(line, SecretRedaction.Mask);
        foreach (string word in line.Split(' ', '\t', '"', '\'', '=', ':', '&', '?', ',', '{', '}'))
        {
            if (IsSecretWord(word))
            {
                int index = line.IndexOf(word, StringComparison.Ordinal);
                return line[..(index + word.Length)] + " " + SecretRedaction.Mask;
            }
        }

        return line;
    }

    private static bool IsSecretWord(string word)
    {
        string letters = new([.. word.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
        return letters.Length > 2
               && (letters is "key" or "apikey" or "privatekey" or "sig" or "tempauth"
                   || SecretEndings.Any(ending => letters.EndsWith(ending, StringComparison.Ordinal)));
    }

    private static readonly string[] SecretEndings =
        ["secret", "secrets", "password", "passwd", "pwd", "token", "tokens", "credential", "credentials", "authorization"];

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JsonWebToken();

    /// <summary>
    /// Who and where, replaced by placeholders. Whole words only, longest first, so
    /// the profile folder goes before the user name inside it.
    /// </summary>
    private static List<(Regex Pattern, string Placeholder)> IdentitiesToReplace(CaptrSettings? settings)
    {
        var values = new List<(string Value, string Placeholder)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
            (Environment.UserName, "<user>"),
            (Environment.MachineName, "<pc>"),
        };

        foreach (DestinationSettings destination in settings?.Destinations ?? [])
        {
            values.Add((destination.TenantId ?? "", "<tenant-id>"));
            values.Add((destination.ClientId ?? "", "<client-id>"));
            if (Uri.TryCreate(destination.SharePointSiteUrl, UriKind.Absolute, out Uri? site))
            {
                values.Add((site.Host, "<sharepoint-host>"));
            }

            values.Add((ServerOf(destination.FolderPath), "<server>"));
        }

        values.Add((ServerOf(settings?.WorkingFolder), "<server>"));

        return
        [
            .. values
                .Where(pair => pair.Value.Length >= 2)
                .DistinctBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(pair => pair.Value.Length)
                .Select(pair => (new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(pair.Value) + @"(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), pair.Placeholder)),
        ];
    }

    private static string ServerOf(string? path) =>
        path is { Length: > 2 } && path.StartsWith(@"\\", StringComparison.Ordinal)
            ? path[2..].Split('\\')[0]
            : "";

    private static string Pseudonymise(string line, IReadOnlyList<(Regex Pattern, string Placeholder)> identities)
    {
        foreach ((Regex pattern, string placeholder) in identities)
        {
            line = pattern.Replace(line, placeholder);
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
         App version: {typeof(SupportBundle).Assembly
             .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
             .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
             .FirstOrDefault()?.InformationalVersion}
         CLR: {Environment.Version}
         """;
}

/// <summary>What a support bundle holds: the entries written, and the files that
/// could not be read and so were left out (named in its README).</summary>
public sealed record SupportBundleContents(string Path, IReadOnlyList<string> Included, IReadOnlyList<string> Unreadable);
