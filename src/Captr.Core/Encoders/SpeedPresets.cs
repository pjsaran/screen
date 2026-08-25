namespace Captr.Core.Encoders;

/// <summary>
/// How hard the encoder is allowed to work per frame — the classic
/// ultrafast…fast scale. Owns the mapping from that one user choice to each
/// encoder's own speed knob, which is spelled differently everywhere
/// (NVENC has p1–p7, QSV has named presets, AMF has three quality modes).
/// </summary>
/// <remarks>
/// <para>
/// The trade-off runs the opposite way from what people expect on first reading:
/// a FASTER preset spends less CPU/GPU time per frame and therefore has to spend
/// more BITS to hit the same quality, so faster presets produce LARGER files. A
/// slower preset costs more CPU and yields smaller files at the same visual
/// quality. Quality itself is set separately, by <see cref="QualityLevels"/>.
/// </para>
/// <para>
/// The names are the familiar x264 vocabulary because that is what users search
/// for. When the selected encoder IS libx264 (the GPL build's software tier) they
/// pass through verbatim; every other encoder gets them translated to its own
/// equivalent below.
/// </para>
/// </remarks>
public static class SpeedPresets
{
    /// <summary>What a fresh install encodes at: the balanced middle of the scale.</summary>
    public const string DefaultName = "veryfast";

    /// <summary>Every preset, fastest (cheapest, largest files) first.</summary>
    public static readonly IReadOnlyList<SpeedPreset> All =
    [
        new("ultrafast", "Ultrafast", "Lowest CPU, largest files", Step: 0),
        new("superfast", "Superfast", "Very low CPU, large files", Step: 1),
        new("veryfast", "Veryfast", "Recommended, balanced CPU", Step: 2),
        new("faster", "Faster", "Higher CPU, smaller files", Step: 3),
        new("fast", "Fast", "Max CPU, smallest files", Step: 4),
    ];

    /// <summary>Finds a preset by name (case-insensitive); null when unknown.</summary>
    public static SpeedPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset, or the default when the name is unknown — used where a
    /// bad value must not stop a recording (display strings, status reports).</summary>
    public static SpeedPreset FindOrDefault(string name) => Find(name) ?? Find(DefaultName)!;

    // ---- Per-encoder translation ------------------------------------------------
    // Indexed by SpeedPreset.Step (0 = ultrafast … 4 = fast).

    /// <summary>NVENC's p1 (fastest) … p7 (slowest) scale.</summary>
    private static readonly string[] NvencPresets = ["p1", "p2", "p4", "p5", "p6"];

    /// <summary>Quick Sync's named presets. It has seven; these five are spread
    /// across the usable range (veryslow is disproportionately slow for little gain
    /// on screen content, so the scale stops at slower).</summary>
    private static readonly string[] QsvPresets = ["veryfast", "faster", "fast", "slow", "slower"];

    /// <summary>AMF exposes only three modes, so the five presets collapse onto them.</summary>
    private static readonly string[] AmfQualities = ["speed", "speed", "balanced", "quality", "quality"];

    /// <summary>The encoder-specific speed arguments for this preset.</summary>
    /// <param name="encoderName">An encoder from <see cref="EncoderCatalog"/>.</param>
    public static IReadOnlyList<string> BuildSpeedArguments(string encoderName, SpeedPreset preset) =>
        encoderName switch
        {
            "hevc_nvenc" or "h264_nvenc" => ["-preset", NvencPresets[preset.Step]],
            "hevc_qsv" or "h264_qsv" => ["-preset", QsvPresets[preset.Step]],
            "hevc_amf" or "h264_amf" => ["-quality", AmfQualities[preset.Step]],

            // The preset NAMES are x264's own vocabulary, so libx264 takes them
            // verbatim — no translation table needed.
            "libx264" => ["-preset", preset.Name],

            // openh264 has no speed/complexity scale worth exposing: its one
            // relevant knob (complexity) is not settable through FFmpeg's wrapper.
            // The last-resort fallback therefore ignores the preset, which is fine —
            // it exists to keep recording alive, not to be tuned.
            "libopenh264" => [],

            _ => throw new ArgumentException(
                $"Unknown encoder '{encoderName}' — add it to SpeedPresets, QualityLevels, AND EncoderCatalog together.",
                nameof(encoderName)),
        };
}

/// <summary>One selectable speed preset.</summary>
/// <param name="Name">Stored/CLI name, e.g. "veryfast".</param>
/// <param name="DisplayName">Title-cased name for the UI.</param>
/// <param name="Description">The trade-off, in the user's terms.</param>
/// <param name="Step">Index into the per-encoder translation tables.</param>
public sealed record SpeedPreset(string Name, string DisplayName, string Description, int Step);
