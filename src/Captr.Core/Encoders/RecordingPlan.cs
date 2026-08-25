namespace Captr.Core.Encoders;

/// <summary>
/// Everything <see cref="FfmpegArgumentBuilder"/> needs to produce the encoder's
/// argument vector — fully resolved, immutable, and free of I/O. Owns nothing at
/// runtime; it is a value. Built by the session engine from settings + resolved
/// displays + the encoder selection.
/// </summary>
public sealed record RecordingPlan
{
    public required Guid SessionId { get; init; }

    /// <summary>Capture sources in canvas order (left-to-right, then row by row).</summary>
    public required IReadOnlyList<CaptureSource> Sources { get; init; }

    /// <summary>Identical frame rate for every source (SPEC §5: sources are
    /// independently clocked; a shared rate is the only synchronisation).</summary>
    public required int FrameRate { get; init; }

    /// <summary>The encoder chosen by selection (WP5), with its quality arguments
    /// already resolved. The builder never decides quality.</summary>
    public required EncoderSettings Encoder { get; init; }

    /// <summary>Optional text overlay burned into the canvas.</summary>
    public string? OverlayText { get; init; }

    /// <summary>Session working folder — receives segments and the progress file.</summary>
    public required string WorkingFolder { get; init; }

    /// <summary>Join-compatibility group; changes when display topology changes
    /// mid-session (see <c>Sessions/JournalEvent.cs</c>). Part of segment names so
    /// finalisation can group segments without probing them.</summary>
    public int ArrangementGroup { get; init; } = 1;

    /// <summary>How the screen is read. Chosen by encoder selection, exactly like the
    /// encoder itself: Desktop Duplication when it works, GDI when it does not.</summary>
    public CaptureMethod CaptureMethod { get; init; } = CaptureMethod.DesktopDuplication;
}

/// <summary>
/// The two ways Captr can read the screen. This exists because "which encoder works"
/// turned out not to be the only machine-dependent question — on virtual desktops
/// (AWS WorkSpaces, some VMs, some RDP hosts) the display driver cannot create the
/// D3D11 device Desktop Duplication needs, and capture fails before any encoder is
/// even exercised.
/// </summary>
public enum CaptureMethod
{
    /// <summary>The <c>ddagrab</c> filter: the Desktop Duplication API, GPU-side and
    /// cheap. The default, and the right answer on any physical machine.</summary>
    DesktopDuplication,

    /// <summary>The <c>gdigrab</c> input device: plain GDI screen reads. Works on
    /// virtual display drivers where Desktop Duplication cannot, at a real CPU cost —
    /// the compatibility fallback, never the first choice.</summary>
    Gdi,
}

/// <summary>One display as the encoder sees it: which DXGI output to duplicate, its
/// native size, and where it sits on the virtual desktop (what GDI capture addresses
/// instead of an output index). The output index is resolved from stable display
/// identity at start time and NEVER persisted (SPEC §5 — indices reorder).</summary>
public sealed record CaptureSource(int OutputIndex, int Width, int Height, int VirtualX = 0, int VirtualY = 0);

/// <summary>The encoder codec plus its fully-resolved quality argument pairs
/// (e.g. <c>-rc constqp -qp 23 -preset p5</c>), produced by encoder selection.</summary>
public sealed record EncoderSettings(string CodecName, IReadOnlyList<string> QualityArguments);
