using System.Globalization;

namespace Captr.Core.Encoders;

/// <summary>
/// How good the picture has to look, expressed on the familiar CRF scale
/// (0 = mathematically lossless, higher = more compressed). Owns the mapping from
/// that one user choice to each encoder's constant-quality argument. Separate from
/// <see cref="SpeedPresets"/> on purpose: quality is what you want, speed is what
/// you are willing to pay to get it.
/// </summary>
/// <remarks>
/// <para>
/// Hardware encoders take a quantizer rather than a true CRF, but their scales are
/// close enough to CRF that the same number means roughly the same thing — which is
/// why one number can drive all of them. HEVC is given the CRF value as-is; H.264
/// needs a slightly lower quantizer for equivalent quality, so it gets a small
/// offset (see <see cref="H264Offset"/>).
/// </para>
/// <para>
/// Sizes in the descriptions are relative, not promises: the real rate depends
/// entirely on how much the screen moves, which is why the disk preflight measures
/// the actual canvas instead of trusting a table (SPEC §5).
/// </para>
/// </remarks>
public static class QualityLevels
{
    /// <summary>What a fresh install records at.</summary>
    public const string DefaultName = "balanced";

    /// <summary>Every level, best quality first.</summary>
    public static readonly IReadOnlyList<QualityLevel> All =
    [
        new("lossless", "Lossless", 0, "CRF 0, massive size", Step: 0),
        new("maximum", "Maximum", 15, "CRF 15, huge size, sharp text", Step: 1),
        new("high", "High", 18, "CRF 18, large size, crystal clear", Step: 2),
        new("balanced", "Balanced", 23, "CRF 23, medium size, standard quality", Step: 3),
        new("compact", "Compact", 28, "CRF 28, small size, compressed", Step: 4),
    ];

    /// <summary>H.264 needs a lower quantizer than HEVC for the same visual result;
    /// two steps is the usual rule of thumb.</summary>
    private const int H264Offset = -2;

    /// <summary>Bits per pixel per second for the bitrate-driven software encoder,
    /// indexed by <see cref="QualityLevel.Step"/>. openh264 has no constant-quality
    /// mode at all, so quality has to be expressed as a generous bitrate instead.</summary>
    private static readonly double[] SoftwareBitsPerPixel = [0.40, 0.24, 0.16, 0.09, 0.05];

    /// <summary>Finds a level by name (case-insensitive); null when unknown.</summary>
    public static QualityLevel? Find(string name) =>
        All.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The level, or the default when the name is unknown — used where a bad
    /// value must not stop a recording (display strings, status reports).</summary>
    public static QualityLevel FindOrDefault(string name) => Find(name) ?? Find(DefaultName)!;

    /// <summary>
    /// The complete encoder argument list for one encoder at one quality level and
    /// one speed preset — the only place the two choices are combined.
    /// </summary>
    public static IReadOnlyList<string> BuildEncoderArguments(
        string encoderName,
        SpeedPreset speed,
        QualityLevel quality,
        int canvasWidth,
        int canvasHeight,
        int frameRate)
    {
        List<string> arguments = [.. BuildQualityArguments(encoderName, quality, canvasWidth, canvasHeight, frameRate)];
        arguments.AddRange(SpeedPresets.BuildSpeedArguments(encoderName, speed));
        return arguments;
    }

    private static IReadOnlyList<string> BuildQualityArguments(
        string encoderName, QualityLevel quality, int canvasWidth, int canvasHeight, int frameRate)
    {
        switch (encoderName)
        {
            case "hevc_nvenc":
            case "h264_nvenc":
                {
                    // NVENC has a dedicated lossless mode; its constqp path cannot
                    // reach true lossless even at qp 0.
                    if (quality.Crf == 0)
                    {
                        return ["-tune", "lossless"];
                    }

                    int qp = QuantizerFor(encoderName, quality);
                    // tune hq + spatial AQ spend bits on detailed regions, which is
                    // exactly where screen text lives.
                    return ["-rc", "constqp", "-qp", Invariant(qp), "-tune", "hq", "-spatial-aq", "1"];
                }

            case "hevc_qsv":
            case "h264_qsv":
                return ["-global_quality", Invariant(QuantizerFor(encoderName, quality))];

            case "hevc_amf":
            case "h264_amf":
                {
                    string qp = Invariant(QuantizerFor(encoderName, quality));
                    return ["-rc", "cqp", "-qp_i", qp, "-qp_p", qp];
                }

            case "libx264":
                {
                    // x264 has the real thing: true CRF. The user's quality choice
                    // means exactly what it means on the GPU encoders — spend bits
                    // only where the picture changes — which is why libx264 outranks
                    // openh264 in the software tier. CRF 0 IS x264's lossless mode.
                    int crf = QuantizerFor(encoderName, quality);
                    return ["-crf", Invariant(crf)];
                }

            case "libopenh264":
                {
                    long bitsPerSecond = (long)(canvasWidth * (double)canvasHeight * frameRate * SoftwareBitsPerPixel[quality.Step]);
                    return
                    [
                        "-b:v", Invariant(bitsPerSecond),
                        "-maxrate", Invariant((long)(bitsPerSecond * 1.5)),
                        "-bufsize", Invariant(bitsPerSecond * 2),
                    ];
                }

            default:
                throw new ArgumentException(
                    $"Unknown encoder '{encoderName}' — add it to QualityLevels, SpeedPresets, AND EncoderCatalog together.",
                    nameof(encoderName));
        }
    }

    /// <summary>The quantizer this encoder should be given for the level, clamped to
    /// the 0–51 range every H.26x encoder accepts.</summary>
    private static int QuantizerFor(string encoderName, QualityLevel quality)
    {
        // Both spellings of "this is an H.264 encoder": the hardware ones are named
        // h264_*, the software one libx264.
        bool isH264 = encoderName.StartsWith("h264", StringComparison.Ordinal) || encoderName == "libx264";
        int value = isH264 ? quality.Crf + H264Offset : quality.Crf;
        return Math.Clamp(value, 0, 51);
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One selectable quality level.</summary>
/// <param name="Name">Stored/CLI name, e.g. "balanced".</param>
/// <param name="DisplayName">Title-cased name for the UI.</param>
/// <param name="Crf">The constant-quality target; 0 means lossless.</param>
/// <param name="Description">Size and sharpness, in the user's terms.</param>
/// <param name="Step">Position in <see cref="QualityLevels.All"/>, which indexes the
/// software encoder's bits-per-pixel table.</param>
public sealed record QualityLevel(string Name, string DisplayName, int Crf, string Description, int Step);
