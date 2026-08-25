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
    /// <summary>
    /// The software tier, best first. libx264 is preferred because it has a true
    /// constant-quality (CRF) mode, so the user's Quality setting means the same
    /// thing it means on a GPU encoder; openh264 (bitrate-only) is the last resort
    /// and the only software encoder an LGPL build carries. Which of these exist is
    /// decided by the SHIPPED binary, never assumed here.
    /// </summary>
    private static readonly string[] SoftwareOrder = ["libx264", "libopenh264"];

    /// <summary>Preference order (SPEC §5). Every name here must have a mapping in
    /// BOTH <see cref="QualityLevels.BuildEncoderArguments"/> and
    /// <see cref="SpeedPresets.BuildSpeedArguments"/>.</summary>
    private static readonly string[] PreferenceOrder =
    [
        "hevc_nvenc", "h264_nvenc",
        "hevc_qsv", "h264_qsv",
        "hevc_amf", "h264_amf",
        .. SoftwareOrder,
    ];

    /// <summary>
    /// Candidates to probe, in order. The hardware list comes straight from
    /// preference order — merely APPEARING in the ffmpeg build proves nothing about
    /// this machine (SPEC §5: "a machine can advertise a vendor encoder with no
    /// matching GPU"), which is why every candidate still faces a trial encode.
    /// Software entries are included only when the build actually has them.
    /// </summary>
    public static IReadOnlyList<string> Candidates(FfmpegCapabilities capabilities)
    {
        var candidates = new List<string>();
        foreach (string name in PreferenceOrder)
        {
            if (IsSoftware(name)
                ? capabilities.SoftwareEncoders.Contains(name)
                : capabilities.HardwareEncoders.Contains(name))
            {
                candidates.Add(name);
            }
        }

        return candidates;
    }

    /// <summary>True for the software (fallback-tier) encoders.</summary>
    public static bool IsSoftware(string encoderName) => SoftwareOrder.Contains(encoderName);

    /// <summary>The best software encoder this build ships, or null when it ships
    /// none — used for SPEC §6's one crash fallback.</summary>
    public static string? SoftwareFallback(FfmpegCapabilities capabilities) =>
        SoftwareOrder.FirstOrDefault(capabilities.SoftwareEncoders.Contains);
}

/// <summary>What the bundled FFmpeg build can do — the machine-readable result of
/// the fetch script's interrogation of the binary (SPEC §2/§11).</summary>
public sealed record FfmpegCapabilities
{
    public required IReadOnlyList<string> HardwareEncoders { get; init; }

    /// <summary>The software encoders the build actually contains: libx264 in the
    /// GPL build, libopenh264 in both flavors. Read from what the fetch script
    /// PROVED was in the binary, so switching build flavor needs no code change.</summary>
    public required IReadOnlyList<string> SoftwareEncoders { get; init; }

    public required string BuildId { get; init; }

    /// <summary>True when any software fallback exists at all.</summary>
    public bool HasSoftwareFallback => SoftwareEncoders.Count > 0;

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

        // optionalEncoders is a name → present map; keep only what is really there.
        var software = new List<string>();
        if (root.TryGetProperty("optionalEncoders", out JsonElement optional))
        {
            foreach (JsonProperty entry in optional.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.True)
                {
                    software.Add(entry.Name);
                }
            }
        }

        var hardware = root.GetProperty("requiredEncoders")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        return new FfmpegCapabilities
        {
            HardwareEncoders = hardware,
            SoftwareEncoders = software,
            BuildId = root.GetProperty("buildId").GetString() ?? "unknown",
        };
    }
}
