using System.Text.Json;

namespace Captr.Core.Encoders;

/// <summary>
/// The ordered list of encoder candidates (SPEC §5: HEVC hardware, then H.264
/// hardware, then software) filtered by what the bundled FFmpeg build actually
/// contains — read from the <c>capabilities.json</c> that
/// <c>build/fetch-ffmpeg.ps1</c> generated when it interrogated the binary.
/// Owns the candidate order; the prober decides which candidate actually works.
/// </summary>
public static class EncoderCatalog
{
    /// <summary>Preference order (SPEC §5). Every name here must have a mapping in
    /// <see cref="QualityPresets.BuildQualityArguments"/>.</summary>
    private static readonly string[] PreferenceOrder =
    [
        "hevc_nvenc", "h264_nvenc",
        "hevc_qsv", "h264_qsv",
        "hevc_amf", "h264_amf",
        "libopenh264",
    ];

    /// <summary>
    /// Candidates to probe, in order. The hardware list comes straight from
    /// preference order — merely APPEARING in the ffmpeg build proves nothing about
    /// this machine (SPEC §5: "a machine can advertise a vendor encoder with no
    /// matching GPU"), which is why every candidate still faces a trial encode.
    /// The software entry is included only when the build actually has it.
    /// </summary>
    public static IReadOnlyList<string> Candidates(FfmpegCapabilities capabilities)
    {
        var candidates = new List<string>();
        foreach (string name in PreferenceOrder)
        {
            bool isSoftware = name == "libopenh264";
            if (isSoftware ? capabilities.HasOpenH264 : capabilities.HardwareEncoders.Contains(name))
            {
                candidates.Add(name);
            }
        }

        return candidates;
    }

    /// <summary>True for the software (fallback) encoder.</summary>
    public static bool IsSoftware(string encoderName) => encoderName == "libopenh264";
}

/// <summary>What the bundled FFmpeg build can do — the machine-readable result of
/// the fetch script's interrogation of the binary (SPEC §2/§11).</summary>
public sealed record FfmpegCapabilities
{
    public required IReadOnlyList<string> HardwareEncoders { get; init; }
    public required bool HasOpenH264 { get; init; }
    public required string BuildId { get; init; }

    /// <summary>Loads capabilities.json from beside the ffmpeg binary.</summary>
    public static FfmpegCapabilities LoadFrom(string ffmpegPath)
    {
        string capabilitiesPath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "..", "capabilities.json");
        if (!File.Exists(capabilitiesPath))
        {
            // Also try beside the binary (installed layout copies it there).
            capabilitiesPath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "capabilities.json");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(capabilitiesPath));
        JsonElement root = document.RootElement;

        bool hasOpenH264 = root.TryGetProperty("optionalEncoders", out JsonElement optional)
            && optional.TryGetProperty("libopenh264", out JsonElement openh264)
            && openh264.GetBoolean();

        var hardware = root.GetProperty("requiredEncoders")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        return new FfmpegCapabilities
        {
            HardwareEncoders = hardware,
            HasOpenH264 = hasOpenH264,
            BuildId = root.GetProperty("buildId").GetString() ?? "unknown",
        };
    }
}
