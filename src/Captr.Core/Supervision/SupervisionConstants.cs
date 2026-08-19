namespace Captr.Core.Supervision;

/// <summary>
/// Every supervision threshold, with the consequence of changing it (SPEC §8:
/// constants in code, documented but not exposed as settings).
/// </summary>
public static class SupervisionConstants
{
    /// <summary>
    /// No progress block for this long ⇒ the encoder is stalled and gets killed and
    /// restarted (SPEC §6: "no progress for several seconds"). Lower = faster
    /// recovery but risks killing an encoder that is merely busy (e.g. a driver
    /// hiccup it would survive); higher = longer invisible holes in the recording.
    /// FFmpeg writes progress roughly twice a second when healthy.
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The newest segment file not growing for this long while progress claims to
    /// advance ⇒ treated as a stall too (SPEC §6: "output file not growing while
    /// nominally recording"). Catches the case where the encoder is alive but the
    /// disk writes stopped (full disk driver stalls, vanished folder).
    /// </summary>
    public static readonly TimeSpan FileGrowthTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Grace period between asking FFmpeg to stop (the 'q' on stdin) and
    /// terminating it. Long enough to flush and close a segment cleanly; short
    /// enough that a wedged encoder cannot block shutdown.</summary>
    public static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Encoding speed below <see cref="SlowSpeedThreshold"/>× real time sustained
    /// for this long ⇒ warn, then reduce frame rate (SPEC §6). Encoding slower than
    /// real time means frames are being dropped continuously.
    /// </summary>
    public static readonly TimeSpan SlowSpeedWindow = TimeSpan.FromSeconds(60);

    /// <inheritdoc cref="SlowSpeedWindow"/>
    public const double SlowSpeedThreshold = 0.9;

    /// <summary>
    /// This many encoder FAULTS (external terminations excluded, SPEC §6) within
    /// <see cref="FaultWindow"/> ⇒ the one permitted fallback, hardware → software.
    /// After the fallback the same rule stops the session loudly instead — there is
    /// no second fallback rung, and never a capture-method change.
    /// </summary>
    public const int FaultThreshold = 3;

    /// <inheritdoc cref="FaultThreshold"/>
    public static readonly TimeSpan FaultWindow = TimeSpan.FromMinutes(5);

    /// <summary>Lines of encoder log kept for diagnostics and journaled on failure.
    /// Bounded so a log-flooding encoder cannot exhaust memory (SPEC §6).</summary>
    public const int LogTailLines = 400;

    /// <summary>How often the tail readers poll their files for new bytes. Lower =
    /// faster stall detection, more disk chatter.</summary>
    public static readonly TimeSpan TailPollInterval = TimeSpan.FromMilliseconds(250);
}
