using System.Text.Json.Serialization;

namespace Captr.Core.Sessions;

/// <summary>
/// Base type for every entry in a session's journal. Owns the polymorphic wire shape
/// of the journal (the <c>"kind"</c> discriminator in each NDJSON line). If this type
/// map is wrong, old journals stop being readable — so kinds are append-only: never
/// rename or remove one, only add.
/// </summary>
/// <remarks>
/// The journal is the authoritative record of what happened during a recording
/// session (SPEC §6). Recovery, gap accounting, and the integrity record are all
/// computed from these events, so each event carries everything a reader needs
/// without consulting other state.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SessionStarted), "session-started")]
[JsonDerivedType(typeof(SegmentOpened), "segment-opened")]
[JsonDerivedType(typeof(SegmentClosed), "segment-closed")]
[JsonDerivedType(typeof(GapRecorded), "gap")]
[JsonDerivedType(typeof(StopRequested), "stop-requested")]
[JsonDerivedType(typeof(EncoderRestarted), "encoder-restarted")]
[JsonDerivedType(typeof(EncoderFellBack), "encoder-fell-back")]
[JsonDerivedType(typeof(FrameRateReduced), "frame-rate-reduced")]
[JsonDerivedType(typeof(PauseStarted), "pause-started")]
[JsonDerivedType(typeof(PauseEnded), "pause-ended")]
[JsonDerivedType(typeof(TopologyChanged), "topology-changed")]
[JsonDerivedType(typeof(ClockJumped), "clock-jumped")]
[JsonDerivedType(typeof(EncoderProcessLaunched), "encoder-process-launched")]
[JsonDerivedType(typeof(SessionNote), "note")]
[JsonDerivedType(typeof(SessionFinalized), "session-finalized")]
public abstract record JournalEvent
{
    /// <summary>When the event happened, always UTC (SPEC §12).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// First event of every journal: the full description of the session. Written once;
/// everything a later reader needs to interpret the session without any other state —
/// including the exact encoder argument vector, so a fault can be reproduced and an
/// interrupted session can be recovered by a newer application version.
/// </summary>
public sealed record SessionStarted : JournalEvent
{
    public required Guid SessionId { get; init; }

    /// <summary>IANA/Windows timezone id of the machine, recorded separately from the
    /// UTC timestamps so a human can later see local wall-clock times (SPEC §6).</summary>
    public required string LocalTimeZoneId { get; init; }

    public required string MachineName { get; init; }
    public required string UserName { get; init; }
    public required string AppVersion { get; init; }
    public required string FfmpegBuildId { get; init; }

    /// <summary>The displays being recorded, by stable identity (never DXGI index —
    /// indices reorder, SPEC §5).</summary>
    public required IReadOnlyList<RecordedDisplay> Displays { get; init; }

    public required int CanvasWidth { get; init; }
    public required int CanvasHeight { get; init; }
    public required int FrameRate { get; init; }
    public required string EncoderName { get; init; }
    public required string QualityPreset { get; init; }

    /// <summary>The exact argument vector handed to the encoder process.</summary>
    public required IReadOnlyList<string> EncoderArguments { get; init; }

    /// <summary>Where the session's working files live.</summary>
    public required string WorkingFolder { get; init; }
}

/// <summary>One display inside <see cref="SessionStarted"/>: identity plus the
/// geometry it had when the session began.</summary>
public sealed record RecordedDisplay
{
    /// <summary>Stable, EDID-derived monitor device path (SPEC §5) — survives
    /// re-cabling and reboots, unlike a DXGI output index.</summary>
    public required string StableId { get; init; }

    /// <summary>The display number Windows Display Settings shows for this monitor.</summary>
    public required int WindowsDisplayNumber { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>The encoder began writing a new segment file.</summary>
public sealed record SegmentOpened : JournalEvent
{
    public required string FileName { get; init; }

    /// <summary>Groups join-compatible segments. Segments recorded either side of a
    /// display topology change cannot be joined without re-encoding (SPEC §6), so each
    /// arrangement gets its own group and finalisation produces one output per group.</summary>
    public required int ArrangementGroup { get; init; }
}

/// <summary>A segment file was completed and hashed. The hash lets a later
/// verification pass prove the file has not been altered on disk (SPEC §6).</summary>
public sealed record SegmentClosed : JournalEvent
{
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>
/// A hole in the recording, stated honestly (SPEC §6: never round a gap away or
/// present a recording as continuous when it isn't).
/// </summary>
public sealed record GapRecorded : JournalEvent
{
    /// <summary>When coverage stopped (UTC). <see cref="JournalEvent.TimestampUtc"/>
    /// is when the gap was *detected*, which can be later.</summary>
    public required DateTimeOffset GapStartUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Human-readable cause: encoder restart, suspend/resume, display lost…</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// We asked the encoder to stop. Written BEFORE the stop is issued so that an
/// encoder exit observed WITHOUT a preceding StopRequested is, by definition, either
/// a fault or an external termination — that distinction feeds the fallback
/// threshold (SPEC §6: only faults count toward falling back to software).
/// </summary>
public sealed record StopRequested : JournalEvent
{
    public required string Reason { get; init; }
}

/// <summary>The supervisor restarted the encoder into a new segment.</summary>
public sealed record EncoderRestarted : JournalEvent
{
    public required string Reason { get; init; }

    /// <summary>True when the exit was a fault (stall, crash); false for an external
    /// termination (e.g. a user ending the process in Task Manager). Only faults
    /// count toward the hardware→software fallback threshold (SPEC §6).</summary>
    public required bool CountsTowardFallback { get; init; }

    /// <summary>Tail of the encoder's log at the moment of failure, for diagnostics.</summary>
    public required IReadOnlyList<string> LogTail { get; init; }
}

/// <summary>The one permitted encoder fallback, hardware → software (SPEC §6).</summary>
public sealed record EncoderFellBack : JournalEvent
{
    public required string FromEncoder { get; init; }
    public required string ToEncoder { get; init; }
}

/// <summary>Frame rate was reduced, either automatically (sustained slower-than-real-time
/// encoding) or by the user while recording (a permitted degrading change, SPEC §8).</summary>
public sealed record FrameRateReduced : JournalEvent
{
    public required int FromFps { get; init; }
    public required int ToFps { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Recording paused by the user. The span until <see cref="PauseEnded"/> is an
/// intentional gap and is reported as paused time, not as a fault gap.</summary>
public sealed record PauseStarted : JournalEvent;

/// <summary>Recording resumed after a pause.</summary>
public sealed record PauseEnded : JournalEvent;

/// <summary>Displays changed mid-session (added/removed/mode change). Starts a new
/// arrangement group — see <see cref="SegmentOpened.ArrangementGroup"/>.</summary>
public sealed record TopologyChanged : JournalEvent
{
    public required IReadOnlyList<RecordedDisplay> NewDisplays { get; init; }
    public required int NewArrangementGroup { get; init; }
}

/// <summary>The system clock jumped (SPEC §6: detect and record; timestamps stay UTC).</summary>
public sealed record ClockJumped : JournalEvent
{
    public required TimeSpan ApparentJump { get; init; }
}

/// <summary>
/// The encoder process was (re)launched. Carries exactly what re-adoption needs to
/// find this process again after a host crash: PID alone is not enough because
/// Windows reuses PIDs, so the process start time and image path are matched too
/// (SPEC §4).
/// </summary>
public sealed record EncoderProcessLaunched : JournalEvent
{
    public required int ProcessId { get; init; }
    public required DateTimeOffset ProcessStartTimeUtc { get; init; }
    public required string ImagePath { get; init; }
}

/// <summary>
/// A noteworthy observation that affects interpretation but not coverage: the
/// workstation locked or unlocked, a remote-desktop transition, UAC secure-desktop
/// denials tolerated, and similar (SPEC §6's Windows-events table asks for several
/// of these to be "recorded").
/// </summary>
public sealed record SessionNote : JournalEvent
{
    public required string Text { get; init; }
}

/// <summary>
/// Terminal event: finalisation completed. A journal WITHOUT this event is, by
/// definition, an interrupted session that recovery must process on next host start
/// (SPEC §6).
/// </summary>
public sealed record SessionFinalized : JournalEvent
{
    public required IReadOnlyList<string> OutputFiles { get; init; }
    public required TimeSpan TotalSpan { get; init; }
    public required TimeSpan RecordedSpan { get; init; }
    public required int GapCount { get; init; }

    /// <summary>Any difference found when reconciling summed segment durations against
    /// the wall-clock span and journal events — recorded, never hidden (SPEC §6).</summary>
    public required string? ReconciliationNote { get; init; }
}
