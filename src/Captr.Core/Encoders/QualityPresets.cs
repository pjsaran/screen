using System.Globalization;

namespace Captr.Core.Encoders;

/// <summary>
/// The named quality presets (SPEC §5): a small set spanning "visually lossless,
/// large" to "small, layout legible but small text not", named for intent rather
/// than numbers. Owns the mapping from (preset, encoder) to concrete quality
/// arguments. The default keeps small text legible — the primary use case.
/// </summary>
public static class QualityPresets
{
    /// <summary>The preset recording defaults to (SPEC §5: small text must stay
    /// legible at the default).</summary>
    public const string DefaultName = "sharp-text";

    /// <summary>All presets, best quality first.</summary>
    public static readonly IReadOnlyList<QualityPreset> All =
    [
        new("archival", "Archival",
            "Visually lossless. Large files — for recordings that must survive scrutiny.", QuantizerStep: 0),
        new("sharp-text", "Sharp text (default)",
            "Dense small text stays readable at 100% zoom. The right choice for trading screens and terminals.", QuantizerStep: 1),
        new("balanced", "Balanced",
            "Normal text readable; the smallest print softens. About half the size of Sharp text.", QuantizerStep: 2),
        new("compact", "Compact",
            "Layout and large text only — small text will not be readable. Smallest files.", QuantizerStep: 3),
    ];

    /// <summary>Per-encoder quantizer for each preset step. Lower = better quality.
    /// The sharp-text values were chosen by the quality bake-off against dense
    /// numeral content (docs/quality-baseline/), not guessed.</summary>
    private static readonly int[] HevcQp = [18, 23, 28, 34];
    private static readonly int[] H264Qp = [16, 21, 26, 32];

    /// <summary>Bits-per-pixel-per-second for the bitrate-driven software encoder,
    /// per preset step. openh264 has no usable constant-quality mode, so quality is
    /// expressed as generous bitrate instead.</summary>
    private static readonly double[] SoftwareBitsPerPixel = [0.28, 0.16, 0.09, 0.05];

    /// <summary>Finds a preset by name (case-insensitive); null when unknown.</summary>
    public static QualityPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The full quality argument list for one encoder at one preset.
    /// <paramref name="numericOverride"/> is the SPEC §5 escape hatch: a user-chosen
    /// quantizer that replaces the preset's value (ignored for bitrate-driven
    /// software encoding, where a quantizer has no meaning).
    /// </summary>
    public static IReadOnlyList<string> BuildQualityArguments(
        string encoderName, QualityPreset preset, int? numericOverride, int canvasWidth, int canvasHeight, int frameRate)
    {
        int step = preset.QuantizerStep;

        switch (encoderName)
        {
            case "hevc_nvenc":
            case "h264_nvenc":
                {
                    int qp = numericOverride ?? (encoderName == "hevc_nvenc" ? HevcQp[step] : H264Qp[step]);
                    // p5 balances quality and speed; tune hq + spatial AQ protect thin
                    // glyphs from being smoothed away (the bake-off's decisive settings).
                    return ["-rc", "constqp", "-qp", Invariant(qp), "-preset", "p5", "-tune", "hq", "-spatial-aq", "1"];
                }

            case "hevc_qsv":
            case "h264_qsv":
                {
                    int quality = numericOverride ?? (encoderName == "hevc_qsv" ? HevcQp[step] : H264Qp[step]);
                    return ["-global_quality", Invariant(quality), "-preset", "slower"];
                }

            case "hevc_amf":
            case "h264_amf":
                {
                    int qp = numericOverride ?? (encoderName == "hevc_amf" ? HevcQp[step] : H264Qp[step]);
                    return ["-rc", "cqp", "-qp_i", Invariant(qp), "-qp_p", Invariant(qp), "-quality", "quality"];
                }

            case "libopenh264":
                {
                    long bitsPerSecond = (long)(canvasWidth * (double)canvasHeight * frameRate * SoftwareBitsPerPixel[step]);
                    string rate = Invariant(bitsPerSecond);
                    return ["-b:v", rate, "-maxrate", Invariant((long)(bitsPerSecond * 1.5)), "-bufsize", Invariant(bitsPerSecond * 2)];
                }

            default:
                throw new ArgumentException($"Unknown encoder '{encoderName}' — add it to QualityPresets AND EncoderCatalog together.");
        }
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One named preset. <see cref="QuantizerStep"/> indexes the per-encoder
/// quality tables in <see cref="QualityPresets"/>.</summary>
public sealed record QualityPreset(string Name, string DisplayName, string Description, int QuantizerStep);
