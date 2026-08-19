using System.Text.Json;

using Captr.Core.Common;

using Vortice.DXGI;

namespace Captr.Core.Encoders;

/// <summary>
/// Remembers which encoder won the trial so every recording does not repeat the
/// probing (SPEC §5: "cache the winner against GPU identity, driver version, and
/// canvas dimensions, and invalidate the cache when any of those change"). Owns the
/// fingerprint that defines "any of those": a stale cache after a GPU or driver
/// change would silently use an encoder that no longer works.
/// </summary>
public sealed class EncoderCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Production cache in local application data.</summary>
    public EncoderCache()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captr", "encoder-cache.json"))
    {
    }

    /// <summary>Test seam: explicit path.</summary>
    public EncoderCache(string path) => _path = path;

    /// <summary>The cached winner for this fingerprint, or null when the fingerprint
    /// changed (new GPU, new driver, new canvas, new app or ffmpeg build).</summary>
    public string? TryGet(string fingerprint)
    {
        string? json = AtomicFile.ReadOrNull(_path);
        if (json is null)
        {
            return null;
        }

        try
        {
            CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(json, SerializerOptions);
            return entry?.Fingerprint == fingerprint ? entry.EncoderName : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores the winner for this fingerprint.</summary>
    public void Store(string fingerprint, string encoderName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, JsonSerializer.Serialize(
            new CacheEntry(fingerprint, encoderName, DateTimeOffset.UtcNow), SerializerOptions));
    }

    /// <summary>
    /// Builds the machine fingerprint: every GPU's vendor/device/subsystem/revision
    /// ids plus its driver version (from DXGI's CheckInterfaceSupport — the user-mode
    /// driver build number, which changes on every driver update), the canvas, and
    /// the application + ffmpeg build. Changing ANY component invalidates the cache.
    /// </summary>
    public static string BuildFingerprint(int canvasWidth, int canvasHeight, string appVersion, string ffmpegBuildId)
    {
        var parts = new List<string>();

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            using (adapter)
            {
                AdapterDescription1 description = adapter!.Description1;
                // CheckInterfaceSupport(IDXGIDevice) is the documented way to read
                // the installed driver's UMD version without WMI.
                adapter.CheckInterfaceSupport<IDXGIDevice>(out long driverVersion);
                parts.Add(FormattableString.Invariant(
                    $"{description.VendorId:X}-{description.DeviceId:X}-{description.SubsystemId:X}-{description.Revision:X}-{driverVersion:X}"));
            }
        }

        parts.Add(FormattableString.Invariant($"{canvasWidth}x{canvasHeight}"));
        parts.Add(appVersion);
        parts.Add(ffmpegBuildId);
        return string.Join('|', parts);
    }

    private sealed record CacheEntry(string Fingerprint, string EncoderName, DateTimeOffset CachedUtc);
}
