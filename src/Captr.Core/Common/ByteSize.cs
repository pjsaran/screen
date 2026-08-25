using System.Globalization;

namespace Captr.Core.Common;

/// <summary>
/// Formats a byte count the way a person reads it. Owns the one implementation the
/// UI and the CLI share, so a recording never reads as "0 MB" in one place and
/// "309 KB" in the other.
/// </summary>
public static class ByteSize
{
    /// <summary>
    /// "847 B", "309 KB", "1.4 MB", "12.7 GB". The unit is chosen so the number
    /// always carries information — a fixed unit turned every short recording into
    /// "0 MB", which looks like a failed recording rather than a small one.
    /// </summary>
    /// <remarks>
    /// Decimal units (1 KB = 1000 B), matching how Windows reports file sizes in its
    /// properties dialog and how storage is sold, rather than binary KiB/MiB.
    /// </remarks>
    public static string Format(long bytes) => bytes switch
    {
        < 0 => "—",
        < 1_000 => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
        < 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000.0:F0} KB"),

        // Below 10 MB a whole number hides the difference between 1.2 and 1.9 MB,
        // so small sizes keep one decimal and larger ones drop it.
        < 10_000_000 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000.0:F1} MB"),
        < 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000.0:F0} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000_000.0:F2} GB"),
    };
}
