namespace Captr.Core.Sessions;

/// <summary>
/// Turns a session's journal events into an honest statement of what was actually
/// recorded (SPEC §6: "account for gaps honestly"). Owns the arithmetic of coverage;
/// it is a pure function of the event list, with no I/O, so every edge case is unit
/// tested on synthetic journals. If it is wrong, Captr lies about what a recording
/// contains — the one thing the spec forbids most explicitly.
/// </summary>
public static class CoverageCalculator
{
    /// <summary>
    /// Computes coverage for the given journal events.
    /// </summary>
    /// <param name="events">All events of one session, in journal order.</param>
    /// <param name="fallbackEndUtc">
    /// End of the observation window for a session with no <see cref="SessionFinalized"/>
    /// event (i.e. a crashed session being recovered): pass the best available evidence
    /// of when recording actually stopped — typically the last heartbeat or the last
    /// segment's modification time. Ignored when the journal contains a terminal event.
    /// </param>
    public static CoverageReport Compute(
        IReadOnlyList<JournalEvent> events,
        DateTimeOffset? fallbackEndUtc = null)
    {
        SessionStarted? start = events.OfType<SessionStarted>().FirstOrDefault();
        if (start is null)
        {
            throw new ArgumentException(
                "Journal contains no session-started event; it is not a session journal.",
                nameof(events));
        }

        DateTimeOffset sessionStart = start.TimestampUtc;
        DateTimeOffset sessionEnd = DetermineSessionEnd(events, fallbackEndUtc, sessionStart);

        var gaps = new List<CoverageGap>();

        // 1. Explicit gaps journaled by the supervisor (encoder restarts, suspend…).
        foreach (GapRecorded gap in events.OfType<GapRecorded>())
        {
            gaps.Add(new CoverageGap(gap.GapStartUtc, gap.Duration, gap.Reason, IsPause: false));
        }

        // 2. Paused spans. A PauseStarted with no matching PauseEnded means the
        //    session died while paused — that pause runs to the session end.
        DateTimeOffset? openPause = null;
        foreach (JournalEvent journalEvent in events)
        {
            switch (journalEvent)
            {
                case PauseStarted pause:
                    openPause ??= pause.TimestampUtc;
                    break;
                case PauseEnded resume when openPause is { } pausedAt:
                    gaps.Add(new CoverageGap(pausedAt, resume.TimestampUtc - pausedAt, "Paused", IsPause: true));
                    openPause = null;
                    break;
            }
        }

        if (openPause is { } stillPausedAt)
        {
            gaps.Add(new CoverageGap(stillPausedAt, sessionEnd - stillPausedAt, "Paused (never resumed)", IsPause: true));
        }

        gaps.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));

        TimeSpan totalSpan = sessionEnd - sessionStart;
        TimeSpan gapTotal = ClampToSpan(gaps, totalSpan);
        TimeSpan recorded = totalSpan - gapTotal;

        return new CoverageReport(
            SessionId: start.SessionId,
            StartUtc: sessionStart,
            EndUtc: sessionEnd,
            TotalSpan: totalSpan,
            RecordedSpan: recorded,
            Gaps: gaps);
    }

    private static DateTimeOffset DetermineSessionEnd(
        IReadOnlyList<JournalEvent> events,
        DateTimeOffset? fallbackEndUtc,
        DateTimeOffset sessionStart)
    {
        if (events.OfType<SessionFinalized>().FirstOrDefault() is { } finalized)
        {
            return finalized.TimestampUtc;
        }

        // Crashed session: prefer the caller's evidence (heartbeat / segment mtime);
        // fall back to the last journaled event so the result is never nonsense.
        DateTimeOffset lastEvent = events[^1].TimestampUtc;
        DateTimeOffset end = fallbackEndUtc ?? lastEvent;
        return end < sessionStart ? lastEvent : end;
    }

    /// <summary>Total gap time, never exceeding the session span even if overlapping
    /// or over-reported gaps were journaled (honesty also means not reporting
    /// negative coverage).</summary>
    private static TimeSpan ClampToSpan(List<CoverageGap> gaps, TimeSpan totalSpan)
    {
        TimeSpan sum = TimeSpan.Zero;
        foreach (CoverageGap gap in gaps)
        {
            sum += gap.Duration;
        }

        return sum > totalSpan ? totalSpan : sum;
    }
}

/// <summary>The honest coverage statement for one session (SPEC §6): total span,
/// what was actually recorded, and every hole with its timestamp, duration, and cause.</summary>
public sealed record CoverageReport(
    Guid SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimeSpan TotalSpan,
    TimeSpan RecordedSpan,
    IReadOnlyList<CoverageGap> Gaps)
{
    /// <summary>Fraction of the session span actually recorded, 0..1. A zero-length
    /// session counts as fully covered (nothing was missed).</summary>
    public double Coverage =>
        TotalSpan <= TimeSpan.Zero ? 1.0 : RecordedSpan.Ticks / (double)TotalSpan.Ticks;

    /// <summary>Number of holes, paused spans included.</summary>
    public int GapCount => Gaps.Count;

    /// <summary>True when any gap is a user pause — such recordings are marked as
    /// containing paused gaps (SPEC §6).</summary>
    public bool ContainsPausedGaps => Gaps.Any(g => g.IsPause);
}

/// <summary>One hole in a recording: when coverage stopped, for how long, and why.
/// <paramref name="IsPause"/> distinguishes an intentional user pause from a fault.</summary>
public sealed record CoverageGap(
    DateTimeOffset StartUtc,
    TimeSpan Duration,
    string Reason,
    bool IsPause);
