using Captr.Core.Ipc;
using Captr.Core.Naming;

namespace Captr.Core.Sessions;

/// <summary>
/// Reads the working folder and reports what recordings are in it. Owns the one
/// definition of "a recording": a subfolder containing a session journal whose first
/// event is a <see cref="SessionStarted"/>.
/// </summary>
/// <remarks>
/// This is deliberately a plain folder scan with no host, no IPC, and no side
/// effects, because all three callers want it that way:
/// <list type="bullet">
///   <item><description>the UI lists recordings the moment the page opens, without
///   starting a recording host just to read a directory (which is what used to make
///   the list appear empty until Refresh was pressed);</description></item>
///   <item><description><c>captr recordings list</c> answers on a machine where
///   nothing is running;</description></item>
///   <item><description>the host answers the same question over IPC.</description></item>
/// </list>
/// One implementation means all three can never disagree about what exists.
/// </remarks>
public static class RecordingCatalog
{
    /// <summary>
    /// Every recording under <paramref name="workingFolder"/>, newest first. A
    /// missing folder is not an error — it just means nothing has been recorded yet.
    /// Folders that cannot be read are skipped rather than failing the whole scan.
    /// </summary>
    public static IReadOnlyList<RecordingSummary> Scan(string workingFolder, CancellationToken cancellationToken = default)
    {
        var summaries = new List<RecordingSummary>();
        if (!Directory.Exists(workingFolder))
        {
            return summaries;
        }

        // Session folder names begin with a sortable timestamp, so ordering by name
        // descending IS newest-first, without reading a single journal.
        foreach (string folder in Directory.GetDirectories(workingFolder).OrderDescending(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDescribe(folder) is { } summary)
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>
    /// Summaries already computed in this process, keyed by the folder and by the
    /// journal's size and last-write time.
    /// </summary>
    /// <remarks>
    /// Describing a recording means parsing its whole journal — every segment roll,
    /// every gap, every settings change. That is cheap for one recording and slow for
    /// a hundred, and the list is rescanned constantly: on every page entry, and again
    /// whenever the folder watcher fires. A FINISHED recording's journal never changes
    /// again, so re-parsing it is pure waste.
    ///
    /// The key includes the journal's length and write time, so a recording still being
    /// written is re-read every time (its journal is growing) while finished ones are
    /// read exactly once.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RecordingSummary> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One folder's summary, or null when it is not a recording.</summary>
    public static RecordingSummary? TryDescribe(string folder)
    {
        string journalPath = Path.Combine(folder, SessionJournal.FileName);

        FileInfo journal = new(journalPath);
        if (!journal.Exists)
        {
            return null;
        }

        string key = FormattableString.Invariant(
            $"{folder}|{journal.Length}|{journal.LastWriteTimeUtc.Ticks}");
        if (Cache.TryGetValue(key, out RecordingSummary? cached))
        {
            return cached;
        }

        try
        {
            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(journalPath);
            if (events.OfType<SessionStarted>().FirstOrDefault() is not { } start)
            {
                return null;
            }

            SessionFinalized? finalized = events.OfType<SessionFinalized>().FirstOrDefault();
            IntegrityRecord? integrity = finalized is null ? null : IntegrityRecord.ReadOrNull(folder);

            var summary = new RecordingSummary(
                folder,
                start.TimestampUtc,
                finalized?.RecordedSpan ?? TimeSpan.Zero,
                MeasureFootage(folder, integrity),
                finalized?.GapCount ?? 0,
                finalized is not null,
                [.. integrity?.Outputs.Select(o => o.FileName) ?? []]);

            // Only a FINISHED recording is worth remembering: an in-progress one grows,
            // and its size and duration have to be re-read to stay honest.
            if (finalized is not null)
            {
                Cache[key] = summary;
            }

            return summary;
        }
        catch (IOException)
        {
            // A session recording RIGHT NOW may have its journal open; skipping it
            // for this scan is better than failing the list.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything <see cref="OutputNamer"/> needs to name files and folders for a
    /// past recording, read back out of its journal. Returns null when the folder is
    /// not a recording. Used when a finished recording is sent somewhere again, so
    /// its date tokens resolve to when it was RECORDED, not to today.
    /// </summary>
    public static NamingContext? ReadNamingContext(string folder)
    {
        string journalPath = Path.Combine(folder, SessionJournal.FileName);
        if (!File.Exists(journalPath))
        {
            return null;
        }

        try
        {
            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(journalPath);
            if (events.OfType<SessionStarted>().FirstOrDefault() is not { } start)
            {
                return null;
            }

            SessionFinalized? finalized = events.OfType<SessionFinalized>().FirstOrDefault();

            return new NamingContext
            {
                StartUtc = start.TimestampUtc,
                EndUtc = finalized?.TimestampUtc ?? events[^1].TimestampUtc,
                MachineName = start.MachineName,
                UserName = start.UserName,
                TimeZone = FindTimeZone(start.LocalTimeZoneId),
                Label = start.Label,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A recording made in a timezone this machine does not know still has
    /// to be named; the local zone is a better answer than a crash.</summary>
    private static TimeZoneInfo FindTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// How much footage this session represents, in bytes.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT "the size of the folder". A finalised session holds the same
    /// footage twice — the segments it was recorded into, and the output they were
    /// joined into — because finalisation never deletes a working file. Summing every
    /// <c>.mkv</c> therefore roughly doubled the number shown to the user, and also
    /// swept in the encoder trial file, which is not footage at all. So: once
    /// finalised, the outputs ARE the recording; before that, the segments are.
    /// </remarks>
    private static long MeasureFootage(string folder, IntegrityRecord? integrity)
    {
        if (integrity is { Outputs.Count: > 0 })
        {
            return integrity.Outputs.Sum(output => output.SizeBytes);
        }

        // Not finalised (or no record): the segments are all there is, and they are
        // what a still-running recording grows.
        return Directory.EnumerateFiles(folder, "seg-*.mkv").Sum(file => new FileInfo(file).Length);
    }
}
