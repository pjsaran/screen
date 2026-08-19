namespace Captr.Core.Displays;

/// <summary>
/// One attached monitor, as Captr sees it: a stable identity for persistence plus
/// the volatile DXGI coordinates capture needs right now. Owns the distinction
/// between the two — if callers persist the wrong one, a cable swap silently records
/// the wrong screen (SPEC §5).
/// </summary>
public sealed record DisplayInfo
{
    /// <summary>
    /// EDID-derived monitor device path, e.g.
    /// <c>\\?\DISPLAY#GSM5B08#5&amp;238e694&amp;0&amp;UID4352#{e6f07b5f-…}</c>.
    /// The ONLY value that may be persisted (SPEC §5: survives reconnection;
    /// DXGI indices reorder).
    /// </summary>
    public required string StableId { get; init; }

    /// <summary>The number Windows Display Settings shows for this monitor — the
    /// number users recognise. Anything else confuses them immediately (SPEC §5).</summary>
    public required int WindowsDisplayNumber { get; init; }

    /// <summary>Human-readable monitor name from EDID, e.g. "LG ULTRAWIDE".</summary>
    public required string FriendlyName { get; init; }

    /// <summary>DXGI output index — what <c>ddagrab=output_idx=</c> takes. Valid for
    /// THIS topology only; re-resolved from <see cref="StableId"/> at every start and
    /// after every topology change. Never persist it.</summary>
    public required int DxgiOutputIndex { get; init; }

    /// <summary>DXGI adapter (GPU) index owning this output.</summary>
    public required int DxgiAdapterIndex { get; init; }

    /// <summary>Native (physical-pixel) resolution.</summary>
    public required int Width { get; init; }

    /// <summary>Native (physical-pixel) resolution.</summary>
    public required int Height { get; init; }

    /// <summary>Position in the virtual desktop, physical pixels.</summary>
    public required int VirtualX { get; init; }

    /// <summary>Position in the virtual desktop, physical pixels.</summary>
    public required int VirtualY { get; init; }

    /// <summary>DPI scale factor (1.0 = 100%, 1.5 = 150%…).</summary>
    public required double DpiScale { get; init; }

    /// <summary>Refresh rate in Hz.</summary>
    public required double RefreshRateHz { get; init; }
}
