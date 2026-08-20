namespace Captr.Core.Encoders;

/// <summary>
/// Every tuned value in the encoding pipeline, with what happens if you change it.
/// These are deliberately constants, not settings (SPEC §8: "everything else is a
/// constant in code with a sensible value, documented but not exposed").
/// </summary>
public static class EncodingConstants
{
    /// <summary>
    /// Segment length in seconds. Shorter = less footage lost on power cut, more
    /// files to manage; longer = the reverse. Five minutes is SPEC §6's chosen
    /// balance. Changing this changes how much a worst-case crash can lose.
    /// </summary>
    public const int SegmentSeconds = 300;

    /// <summary>
    /// Pixel format the canvas is converted to before encoding. yuv420p is the one
    /// format every encoder we probe (NVENC, QSV, AMF, openh264) accepts from system
    /// memory. Changing it will break at least one encoder's trial.
    /// </summary>
    public const string PixelFormat = "yuv420p";

    /// <summary>Capture the mouse cursor. Screen recordings without the pointer are
    /// near-useless for reviewing user actions.</summary>
    public const bool DrawMouse = true;

    /// <summary>
    /// Widest canvas the side-by-side arrangement may produce before we switch to a
    /// grid (SPEC §5: "a grid when the combined width would become unreasonable").
    /// 8192 is a common hardware-encoder dimension ceiling; beyond it encoders start
    /// refusing the canvas outright.
    /// </summary>
    public const int MaxSideBySideWidth = 8192;

    /// <summary>Most displays that still arrange side-by-side; a fourth display
    /// always forms a 2×2 grid, which keeps each display readable.</summary>
    public const int MaxSideBySideCount = 3;

    /// <summary>
    /// GOP ceiling handed to the encoder. Real keyframe placement comes from
    /// -force_key_frames at segment boundaries; this just guarantees the encoder's
    /// own GOP logic never fires FIRST and misaligns a segment cut. Must comfortably
    /// exceed FrameRate × SegmentSeconds.
    /// </summary>
    public static int GopCeiling(int frameRate) => frameRate * SegmentSeconds * 2;

    /// <summary>Name of the machine-readable progress file FFmpeg appends to inside
    /// the session working folder. A FILE, not a pipe — a pipe dies with the host,
    /// and the encoder must outlive a host crash to be re-adopted (SPEC §4).</summary>
    public const string ProgressFileName = "progress.txt";

    /// <summary>Matroska metadata key carrying the session id, cross-checked during
    /// process re-adoption (SPEC §4: "an identifying marker written into the
    /// encoder's own metadata").</summary>
    public const string SessionMetadataKey = "CAPTR_SESSION";

    /// <summary>
    /// Milliseconds of video per Matroska cluster. Smaller clusters are written out
    /// more often, so a segment killed mid-write loses less recoverable footage —
    /// the difference between what survives a host crash and what does not.
    /// </summary>
    /// <remarks>
    /// MEASURED: FFmpeg writes file output through a 512 KB buffer that no
    /// documented option removes (<c>-flush_packets</c>, <c>-avioflags direct</c>,
    /// and <c>-blocksize</c> were all tried and changed nothing). Limiting cluster
    /// duration to 2 s pushed 50% more bytes to disk before a kill in the same
    /// test, because cluster boundaries force writes. The cost is a few extra
    /// cluster headers per segment — nothing against the reliability gain, which is
    /// design priority #1.
    /// </remarks>
    public const int ClusterTimeLimitMilliseconds = 2000;
}
