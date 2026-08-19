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
}

/// <summary>One display as the encoder sees it: which DXGI output to duplicate and
/// its native size. The output index is resolved from stable display identity at
/// start time and NEVER persisted (SPEC §5 — indices reorder).</summary>
public sealed record CaptureSource(int OutputIndex, int Width, int Height);

/// <summary>The encoder codec plus its fully-resolved quality argument pairs
/// (e.g. <c>-rc constqp -qp 23 -preset p5</c>), produced by encoder selection.</summary>
public sealed record EncoderSettings(string CodecName, IReadOnlyList<string> QualityArguments);
