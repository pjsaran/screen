namespace Captr.Core.Supervision;

/// <summary>
/// One parsed block of FFmpeg's machine-readable progress stream (the
/// <c>-progress</c> output): a snapshot of how far the encoder has got. Owns the
/// parsing of that format; consuming this instead of scraping the human log is a
/// SPEC §6 requirement.
/// </summary>
/// <remarks>
/// The stream is a repeating series of <c>key=value</c> lines terminated by a
/// <c>progress=continue</c> (or <c>progress=end</c>) line, e.g.:
/// <code>
/// frame=150
/// fps=15.02
/// total_size=1048576
/// out_time_us=10000000
/// speed=1.01x
/// progress=continue
/// </code>
/// </remarks>
public sealed record EncoderProgress
{
    /// <summary>Frames encoded so far in this encoder run.</summary>
    public long Frame { get; init; }

    /// <summary>Bytes written so far (all segments of this run).</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Encoder output position.</summary>
    public TimeSpan OutTime { get; init; }

    /// <summary>Encoding speed relative to real time (1.0 = keeping up). Null when
    /// FFmpeg reports "N/A" (it does briefly at startup).</summary>
    public double? Speed { get; init; }

    /// <summary>True for the final block (<c>progress=end</c>) of a clean stop.</summary>
    public bool IsEnd { get; init; }

    /// <summary>When the supervisor OBSERVED this block (wall clock, UTC). Used for
    /// stall detection — it is our clock, not FFmpeg's.</summary>
    public required DateTimeOffset ObservedUtc { get; init; }

    /// <summary>
    /// Parses one progress block from its lines. Unknown keys are ignored (FFmpeg
    /// adds keys between versions); malformed values parse as defaults rather than
    /// throwing — a supervisor that crashes on odd progress output is a blind
    /// supervisor.
    /// </summary>
    public static EncoderProgress Parse(IReadOnlyList<string> blockLines, DateTimeOffset observedUtc)
    {
        long frame = 0;
        long totalSize = 0;
        long outTimeMicroseconds = 0;
        double? speed = null;
        bool isEnd = false;

        foreach (string line in blockLines)
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator];
            string value = line[(separator + 1)..].Trim();

            switch (key)
            {
                case "frame":
                    _ = long.TryParse(value, out frame);
                    break;
                case "total_size":
                    _ = long.TryParse(value, out totalSize);
                    break;
                case "out_time_us":
                    _ = long.TryParse(value, out outTimeMicroseconds);
                    break;
                case "speed":
                    // "1.01x" or "N/A"
                    string trimmed = value.TrimEnd('x');
                    speed = double.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                        ? parsed
                        : null;
                    break;
                case "progress":
                    isEnd = value == "end";
                    break;
            }
        }

        return new EncoderProgress
        {
            Frame = frame,
            TotalSizeBytes = totalSize,
            OutTime = TimeSpan.FromMicroseconds(outTimeMicroseconds),
            Speed = speed,
            IsEnd = isEnd,
            ObservedUtc = observedUtc,
        };
    }
}
