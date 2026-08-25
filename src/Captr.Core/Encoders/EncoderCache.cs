using System.Globalization;
using System.Text.Json;

using Captr.Core.Common;

using Vortice.DXGI;

namespace Captr.Core.Encoders;

/// <summary>
/// Remembers which encoder won the trial, and how fast it wrote, so every recording
/// does not repeat the probing (SPEC §5: "cache the winner against GPU identity,
/// driver version, and canvas dimensions, and invalidate the cache when any of those
/// change"). Owns the fingerprint that defines "any of those": a stale cache after a
/// GPU or driver change would silently use an encoder that no longer works.
/// </summary>
/// <remarks>
/// The measured rate is cached alongside the winner because the two trials it used
/// to take — proving the encoder, then measuring the size — were together the whole
/// reason starting a recording took several seconds. Caching both makes a repeat
/// start immediate. The rate is only valid for the same frame rate and quality, so
/// those are part of the fingerprint (see <see cref="BuildFingerprint"/>).
/// </remarks>
public sealed class EncoderCache
{
    // The capture method is stored by NAME ("Gdi", not 1) so someone reading the
    // cache file can see what it says; a cache written before capture methods
    // existed simply lacks the property and deserialises to the old behaviour,
    // Desktop Duplication.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

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

    /// <summary>The cached result for this fingerprint, or null when the fingerprint
    /// changed (new GPU, new driver, new canvas, new settings, new app or ffmpeg
    /// build).</summary>
    public CachedEncoder? TryGet(string fingerprint)
    {
        string? json = AtomicFile.ReadOrNull(_path);
        if (json is null)
        {
            return null;
        }

        try
        {
            CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(json, SerializerOptions);
            return entry?.Fingerprint == fingerprint
                ? new CachedEncoder(entry.EncoderName, entry.BytesPerHour, entry.CaptureMethod)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores the winner, its measured rate, and the capture method it was
    /// proven WITH — a pass under GDI capture says nothing about Desktop Duplication,
    /// so the two must be remembered together.</summary>
    public void Store(string fingerprint, string encoderName, long bytesPerHour,
        CaptureMethod captureMethod = CaptureMethod.DesktopDuplication)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, JsonSerializer.Serialize(
            new CacheEntry(fingerprint, encoderName, bytesPerHour, DateTimeOffset.UtcNow, captureMethod), SerializerOptions));
    }

    /// <summary>
    /// Forgets the cached winner so the next start re-probes from scratch. Called when
    /// a recording that trusted the cache fails immediately — the cheap way to keep
    /// "start instantly" honest: the real recording IS the confirmation trial, and a
    /// failed one costs a re-probe rather than a wrong encoder for hours.
    /// </summary>
    public void Invalidate()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A cache we cannot delete is not worth failing a recording over; the
            // next fingerprint change clears it anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Builds the machine fingerprint: every GPU's vendor/device/subsystem/revision
    /// ids plus its driver version (from DXGI's CheckInterfaceSupport — the user-mode
    /// driver build number, which changes on every driver update), the canvas, the
    /// encoding settings, and the application + ffmpeg build. Changing ANY component
    /// invalidates the cache.
    /// </summary>
    public static string BuildFingerprint(
        int canvasWidth,
        int canvasHeight,
        string appVersion,
        string ffmpegBuildId,
        int frameRate,
        string speedPreset,
        string quality)
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
        parts.Add(frameRate.ToString(CultureInfo.InvariantCulture) + "fps");
        parts.Add(speedPreset);
        parts.Add(quality);
        parts.Add(appVersion);
        parts.Add(ffmpegBuildId);
        return string.Join('|', parts);
    }

    private sealed record CacheEntry(
        string Fingerprint,
        string EncoderName,
        long BytesPerHour,
        DateTimeOffset CachedUtc,
        CaptureMethod CaptureMethod = CaptureMethod.DesktopDuplication);
}

/// <summary>A previously proven encoder, the rate it was measured writing at, and
/// the capture method the proof used.</summary>
public sealed record CachedEncoder(
    string EncoderName, long BytesPerHour, CaptureMethod CaptureMethod = CaptureMethod.DesktopDuplication);
