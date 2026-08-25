using System.Globalization;
using System.Text.RegularExpressions;

namespace Captr.Core.Naming;

/// <summary>
/// Builds the final file name of a recording from the user's naming pattern
/// (SPEC §7). Owns token substitution, Windows-name sanitisation, and collision
/// handling. If it is wrong, a recording lands with a broken name — or worse,
/// overwrites an existing one, which SPEC §7 forbids absolutely.
/// </summary>
public static partial class OutputNamer
{
    /// <summary>The default pattern used when the user has not configured one.</summary>
    public const string DefaultPattern = "{machine} {date} {start}";

    /// <summary>Every supported substitution token (SPEC §7: date, start and end
    /// time, duration, machine, user — plus the optional session label).</summary>
    public static readonly IReadOnlyList<string> SupportedTokens =
        ["{date}", "{start}", "{end}", "{duration}", "{machine}", "{user}", "{label}"];

    /// <summary>
    /// The tokens that accept an optional format after a colon —
    /// <c>{date:yyyy_MM_dd}</c>, <c>{start:HH_mm_ss}</c> — together with the format
    /// used when none is given. These are the three whose separators people actually
    /// want to vary; everything else renders exactly one way.
    /// </summary>
    /// <remarks>
    /// <c>{duration}</c> is deliberately absent. Its value is a TimeSpan with a
    /// bespoke compact rendering ("1h05m"), so a format string after the colon would
    /// mean something quite different from the date tokens — one syntax meaning two
    /// things is worse than not offering it at all.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> DefaultTokenFormats =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = "yyyy-MM-dd",
            ["start"] = "HH-mm-ss",
            ["end"] = "HH-mm-ss",
        };

    /// <summary>
    /// Windows device names that are invalid as file names regardless of extension
    /// ("CON.mkv" is still the console). Checked case-insensitively against the stem.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Produces the sanitised file name (without directory) for a recording.
    /// Times are rendered in the session's own timezone — journals store UTC
    /// (SPEC §12), file names are for humans.
    /// </summary>
    public static string BuildFileName(string pattern, NamingContext context, string extension = ".mkv")
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = DefaultPattern;
        }

        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(context.StartUtc, context.TimeZone);
        DateTimeOffset localEnd = TimeZoneInfo.ConvertTime(context.EndUtc, context.TimeZone);
        TimeSpan duration = context.EndUtc - context.StartUtc;

        string name = TokenPattern().Replace(pattern, match => Expand(
            match.Groups["name"].Value,
            match.Groups["format"].Success ? match.Groups["format"].Value : null,
            localStart,
            localEnd,
            duration,
            context));

        return Sanitize(name) + extension;
    }

    /// <summary>
    /// Renders one token. An unknown token is left exactly as written rather than
    /// silently deleted — the user then sees a literal <c>{whoops}</c> in the file
    /// name and knows what to fix. Settings validation catches it long before here.
    /// </summary>
    private static string Expand(
        string name,
        string? format,
        DateTimeOffset localStart,
        DateTimeOffset localEnd,
        TimeSpan duration,
        NamingContext context)
    {
        string lowered = name.ToLowerInvariant();

        if (DefaultTokenFormats.TryGetValue(lowered, out string? defaultFormat))
        {
            DateTimeOffset moment = lowered == "end" ? localEnd : localStart;
            return moment.ToString(
                string.IsNullOrEmpty(format) ? defaultFormat : format, CultureInfo.InvariantCulture);
        }

        return lowered switch
        {
            "duration" => FormatDuration(duration),
            "machine" => context.MachineName,
            "user" => context.UserName,
            "label" => context.Label ?? string.Empty,
            _ => format is null ? $"{{{name}}}" : $"{{{name}:{format}}}",
        };
    }

    /// <summary>
    /// Expands the same tokens inside a DESTINATION FOLDER, so a transfer can land in
    /// <c>\\archive\recordings\{date:yyyy}\{date:MM}</c> and the year and month
    /// folders are created as needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separators in the pattern are left exactly as written — they are the folder
    /// structure the user asked for. What a TOKEN renders to, however, is sanitised
    /// per segment: a format that happened to produce a backslash would otherwise
    /// invent a folder level nobody asked for, and one that produced <c>..</c> could
    /// climb out of the destination entirely. Validation rejects both long before
    /// here; this is the belt to that pair of braces.
    /// </para>
    /// <para>
    /// The moment used is the recording's, not "now": a transfer retried after
    /// midnight must still land in the folder it was queued for, which is why the
    /// expanded path is stored on the queue row rather than recomputed per attempt.
    /// </para>
    /// </remarks>
    public static string ExpandFolderPath(string folderPattern, NamingContext context)
    {
        if (string.IsNullOrWhiteSpace(folderPattern))
        {
            return folderPattern;
        }

        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(context.StartUtc, context.TimeZone);
        DateTimeOffset localEnd = TimeZoneInfo.ConvertTime(context.EndUtc, context.TimeZone);
        TimeSpan duration = context.EndUtc - context.StartUtc;

        return TokenPattern().Replace(folderPattern, match => SanitizeSegment(Expand(
            match.Groups["name"].Value,
            match.Groups["format"].Success ? match.Groups["format"].Value : null,
            localStart,
            localEnd,
            duration,
            context)));
    }

    /// <summary>
    /// The first problem with a destination folder, or null when it is usable. Checks
    /// the tokens exactly as a file name would, then the literal path around them.
    /// </summary>
    public static string? DescribeFolderPathProblem(string folderPattern)
    {
        if (string.IsNullOrWhiteSpace(folderPattern))
        {
            return null;
        }

        if (DescribeTokenProblems(folderPattern) is { } tokenProblem)
        {
            return tokenProblem;
        }

        // What is left after removing the tokens is the literal folder structure. It
        // is checked on its own so an illegal character in the pattern is reported
        // even when every token in it is perfectly fine.
        string literal = TokenPattern().Replace(folderPattern, "");
        char[] invalid = Path.GetInvalidPathChars();
        char[] offending = [.. literal.Where(c => Array.IndexOf(invalid, c) >= 0).Distinct()];
        if (offending.Length > 0)
        {
            return $"The folder contains {string.Join(" ", offending.Select(c => $"'{c}'"))}, " +
                   "which is not allowed in a path.";
        }

        return null;
    }

    /// <summary>
    /// The first problem with a naming pattern, or null when it is usable. Owns the
    /// message the settings page shows, so a bad pattern is caught while the user is
    /// typing rather than after an eight-hour recording has finished.
    /// </summary>
    public static string? DescribePatternProblem(string pattern) => DescribeTokenProblems(pattern);

    /// <summary>The token checks shared by file names and folder paths.</summary>
    private static string? DescribeTokenProblems(string pattern)
    {
        foreach (Match match in TokenPattern().Matches(pattern))
        {
            string name = match.Groups["name"].Value.ToLowerInvariant();

            if (!SupportedTokens.Contains($"{{{name}}}", StringComparer.OrdinalIgnoreCase))
            {
                return $"Unknown token {match.Value} in the naming pattern. " +
                       $"Supported: {string.Join(", ", SupportedTokens)}.";
            }

            if (!match.Groups["format"].Success)
            {
                continue;
            }

            string format = match.Groups["format"].Value;

            if (!DefaultTokenFormats.ContainsKey(name))
            {
                return $"{{{name}}} does not take a format. Only " +
                       $"{string.Join(", ", DefaultTokenFormats.Keys.Select(k => "{" + k + "}"))} do, " +
                       "for example {date:yyyy_MM_dd}.";
            }

            if (format.Length == 0)
            {
                return $"{match.Value} has an empty format. Write {{{name}}} for the default, " +
                       $"or a format such as {{{name}:{DefaultTokenFormats[name]}}}.";
            }

            if (DescribeFormatProblem(name, format) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a candidate format against a known moment to prove it both parses AND
    /// produces a usable file name. Catches an invalid format string, and also one
    /// that is perfectly valid but emits characters Windows forbids —
    /// <c>{start:HH:mm:ss}</c> being the obvious trap, since a colon is exactly what
    /// a time wants and exactly what a file name cannot have.
    /// </summary>
    private static string? DescribeFormatProblem(string name, string format)
    {
        // A fixed, arbitrary moment: only the SHAPE of the output matters here.
        var probe = new DateTimeOffset(2026, 12, 31, 23, 59, 58, TimeSpan.Zero);

        string rendered;
        try
        {
            rendered = probe.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return $"'{format}' is not a valid date/time format for {{{name}}}. " +
                   $"For example {{{name}:{DefaultTokenFormats[name]}}}.";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] offending = [.. rendered.Where(c => Array.IndexOf(invalid, c) >= 0).Distinct()];
        if (offending.Length > 0)
        {
            return $"{{{name}:{format}}} produces '{rendered}', which contains " +
                   $"{string.Join(" ", offending.Select(c => $"'{c}'"))} — not allowed in a file name. " +
                   "Use '-' or '_' instead.";
        }

        return null;
    }

    /// <summary>
    /// Returns a path in <paramref name="directory"/> that does not exist yet,
    /// suffixing ' (2)', ' (3)', … before the extension when needed. Never
    /// returns an existing path — overwriting a recording is forbidden (SPEC §7).
    /// </summary>
    public static string ResolveCollision(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int n = 2; ; n++)
        {
            candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Makes a string safe as a Windows file name stem: illegal characters become
    /// underscores, trailing dots/spaces are trimmed (Windows silently strips them,
    /// which would make the name we report differ from the name on disk), reserved
    /// device names get a prefix, and an empty result falls back to "recording".
    /// </summary>
    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string sanitized = builder.ToString().TrimEnd('.', ' ').TrimStart(' ');

        if (sanitized.Length == 0)
        {
            return "recording";
        }

        // "CON.mkv" would still address the console device; prefix rather than reject
        // so the user's recording is saved regardless of their pattern.
        string stem = sanitized.Contains('.') ? sanitized[..sanitized.IndexOf('.')] : sanitized;
        if (ReservedNames.Contains(stem))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized;
    }

    /// <summary>
    /// Makes ONE rendered token safe to drop inside a path: anything illegal in a
    /// file name becomes an underscore, so a token can never introduce a separator,
    /// a drive letter, or a <c>..</c> that climbs out of the destination folder.
    /// </summary>
    private static string SanitizeSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }

    /// <summary>Matches <c>{name}</c> and <c>{name:format}</c>. The format runs to the
    /// closing brace, so it may contain spaces, dots, and separators.</summary>
    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)(?::(?<format>[^{}]*))?\}")]
    private static partial Regex TokenPattern();

    /// <summary>Formats a duration as e.g. <c>1h05m</c>, <c>12m30s</c>, or <c>45s</c> —
    /// compact, unambiguous, and free of characters illegal in file names.</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m{duration.Seconds:00}s"
                : $"{duration.Seconds}s";
    }
}

/// <summary>Everything <see cref="OutputNamer"/> needs to know about a recording to
/// name it. Immutable; built by finalisation from the journal.</summary>
public sealed record NamingContext
{
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public required string MachineName { get; init; }
    public required string UserName { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Optional session label supplied at start (CLI/UI).</summary>
    public string? Label { get; init; }
}
