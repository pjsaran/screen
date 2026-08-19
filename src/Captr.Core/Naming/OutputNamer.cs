using System.Globalization;

namespace Captr.Core.Naming;

/// <summary>
/// Builds the final file name of a recording from the user's naming pattern
/// (SPEC §7). Owns token substitution, Windows-name sanitisation, and collision
/// handling. If it is wrong, a recording lands with a broken name — or worse,
/// overwrites an existing one, which SPEC §7 forbids absolutely.
/// </summary>
public static class OutputNamer
{
    /// <summary>The default pattern used when the user has not configured one.</summary>
    public const string DefaultPattern = "{machine} {date} {start}";

    /// <summary>Every supported substitution token (SPEC §7: date, start and end
    /// time, duration, machine, user — plus the optional session label).</summary>
    public static readonly IReadOnlyList<string> SupportedTokens =
        ["{date}", "{start}", "{end}", "{duration}", "{machine}", "{user}", "{label}"];

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

        string name = pattern
            .Replace("{date}", localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{start}", localStart.ToString("HH-mm-ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{end}", localEnd.ToString("HH-mm-ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{duration}", FormatDuration(duration), StringComparison.OrdinalIgnoreCase)
            .Replace("{machine}", context.MachineName, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", context.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("{label}", context.Label ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        return Sanitize(name) + extension;
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
