using System.Text;
using System.Text.Json;

namespace Captr.Core.Sessions;

/// <summary>
/// The append-only on-disk journal of one recording session. Owns the journal file's
/// format (one JSON event per line) and its durability guarantee: every append is
/// flushed to the physical disk before the call returns. If this class fails, a crash
/// can silently lose the record of what was recorded — recovery, gap accounting, and
/// the integrity record all depend on it.
/// </summary>
/// <remarks>
/// <para>
/// Durability: the stream is opened with <see cref="FileOptions.WriteThrough"/> AND
/// each append ends with <c>Flush(flushToDisk: true)</c>. The default
/// <see cref="Stream.Flush()"/> only empties .NET's buffer into the OS cache — the
/// data still dies with a power cut. This double belt-and-braces is deliberate and
/// is the difference SPEC §6 calls out between "a journal that survives power loss
/// and one that does not". Do not "optimise" it away.
/// </para>
/// <para>
/// Concurrency: appends are serialised by a lock. The journal is written by exactly
/// one host process; the lock only protects against concurrent appends from
/// different host threads (supervisor, Windows-event handlers).
/// </para>
/// </remarks>
public sealed class SessionJournal : IDisposable
{
    /// <summary>File name of the journal inside a session's working folder.</summary>
    public const string FileName = "journal.ndjson";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly FileStream _stream;
    private readonly Lock _appendLock = new();

    private SessionJournal(FileStream stream) => _stream = stream;

    /// <summary>Creates the journal for a brand-new session and writes its
    /// <see cref="SessionStarted"/> event as the first line.</summary>
    public static SessionJournal CreateNew(string workingFolder, SessionStarted started)
    {
        Directory.CreateDirectory(workingFolder);
        string path = Path.Combine(workingFolder, FileName);
        var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
        var journal = new SessionJournal(stream);
        journal.Append(started);
        return journal;
    }

    /// <summary>Reopens an existing journal for appending — used when recovery or
    /// re-adoption continues a session that a previous host process started.</summary>
    public static SessionJournal OpenExisting(string workingFolder)
    {
        string path = Path.Combine(workingFolder, FileName);
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
        return new SessionJournal(stream);
    }

    /// <summary>
    /// Appends one event and does not return until it is on the physical disk.
    /// </summary>
    public void Append(JournalEvent journalEvent)
    {
        byte[] line = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(journalEvent, SerializerOptions) + "\n");

        lock (_appendLock)
        {
            _stream.Write(line);
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Reads all events from a journal file, tolerating a truncated final line —
    /// after a power cut mid-append, a torn last line is the EXPECTED state, not an
    /// error. Unknown event kinds are skipped rather than fatal, so an old
    /// application version can still read (most of) a newer journal.
    /// </summary>
    public static IReadOnlyList<JournalEvent> ReadAll(string journalPath)
    {
        var events = new List<JournalEvent>();

        // Full sharing: the writing host holds the file open; readers (status
        // queries, recovery scans) must never be locked out. External scanners
        // (antivirus, indexers) can still hold it exclusively for an instant, so a
        // brief retry beats surfacing a transient IOException to a status query.
        FileStream? stream = null;
        for (int attempt = 0; stream is null; attempt++)
        {
            try
            {
                stream = new FileStream(
                    journalPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < 5 && File.Exists(journalPath))
            {
                Thread.Sleep(30);
            }
        }

        using FileStream journalStream = stream;
        using var reader = new StreamReader(journalStream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<JournalEvent>(line, SerializerOptions) is { } journalEvent)
                {
                    events.Add(journalEvent);
                }
            }
            catch (JsonException)
            {
                // Either the torn final line of a crashed session, or an event kind
                // from a newer version. Both are survivable; neither may crash the
                // reader — recovery runs on the worst day (SPEC §14).
            }
        }

        return events;
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        // One event per line: the serializer must never pretty-print.
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Dispose() => _stream.Dispose();
}
