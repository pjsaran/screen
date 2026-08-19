using System.Text.Json;

using Captr.Core.Common;

namespace Captr.Core.Sessions;

/// <summary>
/// The once-a-second "I am alive and here is where I'm up to" snapshot a recording
/// host writes into the session's working folder (SPEC §6). Owns the snapshot file's
/// shape and its torn-read-free write. Readers (the UI, recovery) use it to tell a
/// live session from a dead one and to show progress without touching the journal.
/// If it fails, recovery may treat a live session as crashed — or the reverse.
/// </summary>
/// <remarks>
/// Written via <see cref="AtomicFile"/> (write-temp-then-rename) so a reader never
/// sees a half-written file. Unlike the journal, the heartbeat is NOT flushed to
/// physical disk — it is a liveness signal, rewritten every second; losing the last
/// one in a power cut costs nothing because the journal holds the durable record.
/// </remarks>
public sealed record HeartbeatSnapshot
{
    /// <summary>File name of the heartbeat inside a session's working folder.</summary>
    public const string FileName = "heartbeat.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public required Guid SessionId { get; init; }
    public required DateTimeOffset WrittenUtc { get; init; }
    public required string State { get; init; }
    public required int HostProcessId { get; init; }

    /// <summary>The segment file currently being written, if any.</summary>
    public string? CurrentSegment { get; init; }

    /// <summary>Encoder output position within the current segment.</summary>
    public TimeSpan? EncodedTime { get; init; }

    /// <summary>Writes this snapshot atomically into the working folder.</summary>
    public void Write(string workingFolder)
    {
        string path = Path.Combine(workingFolder, FileName);
        AtomicFile.Write(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>Reads the latest snapshot, or <see langword="null"/> when none exists.
    /// Never throws on a malformed file — the caller treats that as "no heartbeat".</summary>
    public static HeartbeatSnapshot? ReadOrNull(string workingFolder)
    {
        string? json = AtomicFile.ReadOrNull(Path.Combine(workingFolder, FileName));
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HeartbeatSnapshot>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
