using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Captr.Core.Delivery;

/// <summary>
/// The persisted delivery queue (SPEC §7: "delivery runs as a persisted queue so an
/// interrupted transfer resumes after a crash or reboot"). Owns the SQLite file and
/// every state transition; upload progress (session URL + confirmed offset) is
/// persisted after every chunk, which is precisely what makes resume-not-restart
/// possible.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "SPEC §7 literally names this concept 'a persisted queue'; the domain name beats the framework convention.")]
public sealed class DeliveryQueue
{
    private readonly string _connectionString;

    /// <summary>Production queue in local application data.</summary>
    public DeliveryQueue()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captr", "delivery.db"))
    {
    }

    /// <summary>Test seam: explicit database path.</summary>
    public DeliveryQueue(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS delivery (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                output_path TEXT NOT NULL,
                destination_name TEXT NOT NULL,
                state TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc TEXT,
                upload_url TEXT,
                confirmed_offset INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                created_utc TEXT NOT NULL,
                completed_utc TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Adds a transfer in Pending state. One row per destination — a
    /// failure to one destination never affects another.</summary>
    public long Enqueue(string outputPath, string destinationName)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO delivery (output_path, destination_name, state, created_utc)
            VALUES ($path, $destination, 'pending', $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$path", outputPath);
        command.Parameters.AddWithValue("$destination", destinationName);
        command.Parameters.AddWithValue("$now", UtcNowText());
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>The next item due for work: pending (or retrying past its
    /// next-attempt time), oldest first. Also re-arms items left 'in-progress' by a
    /// crashed host — in-progress with no live owner IS the crash-recovery case.</summary>
    public DeliveryItem? NextDue(DateTimeOffset nowUtc)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, output_path, destination_name, state, attempts, next_attempt_utc,
                   upload_url, confirmed_offset, last_error, created_utc
            FROM delivery
            WHERE state IN ('pending', 'in-progress')
              AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now)
            ORDER BY id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    /// <summary>Marks an item as being worked on right now.</summary>
    public void MarkInProgress(long id) =>
        Execute("UPDATE delivery SET state = 'in-progress' WHERE id = $id", ("$id", id));

    /// <summary>Persists chunk progress — called after EVERY confirmed chunk
    /// (SPEC §7), so a crash resumes from here, not from zero.</summary>
    public void RecordProgress(long id, string uploadUrl, long confirmedOffset) =>
        Execute(
            "UPDATE delivery SET upload_url = $url, confirmed_offset = $offset WHERE id = $id",
            ("$url", uploadUrl), ("$offset", confirmedOffset), ("$id", id));

    /// <summary>Transfer confirmed and verified — terminal success.</summary>
    public void Complete(long id) =>
        Execute(
            "UPDATE delivery SET state = 'completed', completed_utc = $now, last_error = NULL WHERE id = $id",
            ("$now", UtcNowText()), ("$id", id));

    /// <summary>
    /// Records a failure with SPEC §7's classification: transient schedules a
    /// backed-off retry; auth-expired pauses until re-authentication; permanent
    /// parks in manual-retry. The server's message is stored VERBATIM.
    /// </summary>
    public void Fail(long id, FailureKind kind, string serverMessage, DateTimeOffset nowUtc, TimeSpan? retryAfter)
    {
        (string state, string? nextAttempt) = kind switch
        {
            FailureKind.Transient => ("pending", NextAttemptText(id, nowUtc, retryAfter)),
            FailureKind.AuthExpired => ("paused-auth", null),
            _ => ("manual-retry", null),
        };
        Execute(
            """
            UPDATE delivery
            SET state = $state, attempts = attempts + 1, next_attempt_utc = $next, last_error = $error
            WHERE id = $id
            """,
            ("$state", state), ("$next", (object?)nextAttempt ?? DBNull.Value), ("$error", serverMessage), ("$id", id));
    }

    /// <summary>Manual retry (UI/CLI): puts a parked item back in the queue.</summary>
    public void Retry(long id) =>
        Execute(
            "UPDATE delivery SET state = 'pending', next_attempt_utc = NULL WHERE id = $id AND state != 'completed'",
            ("$id", id));

    /// <summary>Re-arms paused-auth items after a successful re-authentication.</summary>
    public void ResumeAuthPaused() =>
        Execute("UPDATE delivery SET state = 'pending', next_attempt_utc = NULL WHERE state = 'paused-auth'");

    /// <summary>Everything in the queue, newest first (delivery view, CLI list).</summary>
    public IReadOnlyList<DeliveryItem> List()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, output_path, destination_name, state, attempts, next_attempt_utc,
                   upload_url, confirmed_offset, last_error, created_utc
            FROM delivery ORDER BY id DESC;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var items = new List<DeliveryItem>();
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    /// <summary>True when every enqueued transfer for this output has completed —
    /// one precondition for local cleanup (SPEC §7: delete only after every enabled
    /// destination confirmed).</summary>
    public bool AllCompletedFor(string outputPath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM delivery WHERE output_path = $path AND state != 'completed'";
        command.Parameters.AddWithValue("$path", outputPath);
        return (long)command.ExecuteScalar()! == 0;
    }

    /// <summary>The exponential backoff with jitter (SPEC §7), bounded so a flaky
    /// destination retries forever-ish but never in a tight loop.</summary>
    internal static TimeSpan Backoff(int attempts, TimeSpan? serverRetryAfter)
    {
        if (serverRetryAfter is { } fromServer)
        {
            return fromServer; // The server's own delay always wins (SPEC §7).
        }

        double seconds = Math.Min(TimeSpan.FromMinutes(30).TotalSeconds, 5 * Math.Pow(2, Math.Min(attempts, 10)));
        return TimeSpan.FromSeconds(seconds * (0.8 + (Random.Shared.NextDouble() * 0.4)));
    }

    private string? NextAttemptText(long id, DateTimeOffset nowUtc, TimeSpan? retryAfter)
    {
        int attempts = CurrentAttempts(id);
        return (nowUtc + Backoff(attempts, retryAfter)).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private int CurrentAttempts(long id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT attempts FROM delivery WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static DeliveryItem ReadItem(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        OutputPath: reader.GetString(1),
        DestinationName: reader.GetString(2),
        State: reader.GetString(3),
        Attempts: reader.GetInt32(4),
        NextAttemptUtc: reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        UploadUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
        ConfirmedOffset: reader.GetInt64(7),
        LastError: reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedUtc: DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string UtcNowText() => DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>One transfer as the queue knows it.</summary>
public sealed record DeliveryItem(
    long Id,
    string OutputPath,
    string DestinationName,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? UploadUrl,
    long ConfirmedOffset,
    string? LastError,
    DateTimeOffset CreatedUtc);

/// <summary>SPEC §7's failure taxonomy.</summary>
public enum FailureKind
{
    /// <summary>Network/5xx/429 — retries automatically with backoff.</summary>
    Transient,

    /// <summary>Credentials expired — pauses until re-authentication.</summary>
    AuthExpired,

    /// <summary>Permission/quota/policy — parks for manual retry with the server's
    /// message shown verbatim.</summary>
    Permanent,
}
