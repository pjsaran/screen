using System.Globalization;

using Captr.Core.Settings;

using Microsoft.Data.Sqlite;

namespace Captr.Core.Transfers;

/// <summary>
/// The persisted transfer queue (SPEC §7: "delivery runs as a persisted queue so an
/// interrupted transfer resumes after a crash or reboot"). Owns the SQLite file and
/// every state transition; upload progress (session URL + confirmed offset) is
/// persisted after every chunk, which is precisely what makes resume-not-restart
/// possible.
/// </summary>
/// <remarks>
/// <para>The states a row can be in, and what each means to a person:</para>
/// <list type="bullet">
///   <item><c>pending</c> — waiting its turn, or waiting out a backoff.</item>
///   <item><c>in-progress</c> — being sent right now.</item>
///   <item><c>completed</c> — landed and verified. Terminal.</item>
///   <item><c>paused-auth</c> — the credential stopped working; needs a person.</item>
///   <item><c>manual-retry</c> — refused, or out of automatic attempts; needs a person.</item>
///   <item><c>cancelled</c> — a person pressed Stop. Never retried on its own.</item>
/// </list>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "SPEC §7 literally names this concept 'a persisted queue'; the domain name beats the framework convention.")]
public sealed class TransferQueue
{
    /// <summary>States a row can hold. Kept as constants so a typo in one SQL string
    /// cannot silently create a state nothing else recognises.</summary>
    public const string StatePending = "pending";

    /// <inheritdoc cref="StatePending"/>
    public const string StateInProgress = "in-progress";

    /// <inheritdoc cref="StatePending"/>
    public const string StateCompleted = "completed";

    /// <inheritdoc cref="StatePending"/>
    public const string StatePausedAuth = "paused-auth";

    /// <inheritdoc cref="StatePending"/>
    public const string StateManualRetry = "manual-retry";

    /// <inheritdoc cref="StatePending"/>
    public const string StateCancelled = "cancelled";

    private readonly string _connectionString;

    /// <summary>Production queue in local application data.</summary>
    public TransferQueue()
        : this(DefaultDatabasePath())
    {
    }

    /// <summary>
    /// Databases whose schema this process has already brought up to date.
    /// </summary>
    /// <remarks>
    /// Preparing the schema means eight round trips to SQLite: a legacy-table probe,
    /// the CREATE TABLE, and a pragma query per added column. None of that can change
    /// while the process is running, but the UI constructs a queue on every page
    /// refresh — which is every three seconds on the Transfers page. Doing it once per
    /// database turns every construction after the first into opening a connection.
    /// </remarks>
    private static readonly HashSet<string> PreparedDatabases = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock PreparedGate = new();

    /// <summary>Test seam: explicit database path.</summary>
    public TransferQueue(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        lock (PreparedGate)
        {
            if (PreparedDatabases.Contains(databasePath))
            {
                return;
            }

            PrepareSchema();
            PreparedDatabases.Add(databasePath);
        }
    }

    /// <summary>Brings an empty or older database up to the current shape. Runs once
    /// per database per process; see <see cref="PreparedDatabases"/>.</summary>
    private void PrepareSchema()
    {
        using SqliteConnection connection = Open();
        RenameLegacyTable(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS transfers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                output_path TEXT NOT NULL,
                destination_name TEXT NOT NULL,
                target_name TEXT,
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

        // Columns added after the first release. A queue written by an older Captr
        // keeps its pending transfers across an upgrade instead of starting empty.
        AddColumnIfMissing(connection, "target_name", "TEXT");
        AddColumnIfMissing(connection, "target_folder", "TEXT");
        AddColumnIfMissing(connection, "bytes_sent", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "total_bytes", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// Where the queue lives, adopting the file an older Captr wrote under its old
    /// name so an upgrade never loses transfers that had not finished yet.
    /// </summary>
    private static string DefaultDatabasePath()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr");
        string current = Path.Combine(folder, "transfers.db");
        string legacy = Path.Combine(folder, "delivery.db");

        if (!File.Exists(current) && File.Exists(legacy))
        {
            try
            {
                File.Move(legacy, current);

                // WAL and shared-memory side files belong to the database; leaving
                // them behind would make SQLite reject the moved file.
                foreach (string suffix in (string[])["-wal", "-shm"])
                {
                    if (File.Exists(legacy + suffix))
                    {
                        File.Move(legacy + suffix, current + suffix, overwrite: true);
                    }
                }
            }
            catch (IOException)
            {
                // A locked legacy file (an old host still running) is not worth
                // failing over: a fresh queue is created and the old one stays put.
            }
        }

        return current;
    }

    /// <summary>
    /// Renames the table an older Captr created. The concept was called "delivery"
    /// before it was called "transfer"; the rows are the same rows.
    /// </summary>
    private static void RenameLegacyTable(SqliteConnection connection)
    {
        using SqliteCommand probe = connection.CreateCommand();
        probe.CommandText =
            """
            SELECT
              (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'delivery'),
              (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'transfers')
            """;
        using (SqliteDataReader reader = probe.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) == 0 || reader.GetInt64(1) != 0)
            {
                return;
            }
        }

        using SqliteCommand rename = connection.CreateCommand();
        rename.CommandText = "ALTER TABLE delivery RENAME TO transfers";
        rename.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds a transfer in Pending state. One row per (output, destination) — which is
    /// what makes a retry re-send to ONLY the destination that failed: if a recording
    /// goes to two places and one fails, the successful row is already 'completed' and
    /// is never touched again.
    /// </summary>
    /// <param name="targetName">
    /// The file name to use AT THIS DESTINATION, from the destination's own naming
    /// pattern. Null means "keep the name the recording already has".
    /// </param>
    /// <param name="targetFolder">
    /// The destination folder with its date tokens already expanded, resolved when
    /// the transfer was queued. Stored rather than recomputed so a transfer retried
    /// after midnight still lands in the folder it was queued for. Null means "use the
    /// destination's configured folder as written".
    /// </param>
    public long Enqueue(
        string outputPath, string destinationName, string? targetName = null, string? targetFolder = null)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO transfers
                (output_path, destination_name, target_name, target_folder, state, total_bytes, created_utc)
            VALUES ($path, $destination, $name, $folder, 'pending', $bytes, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$path", outputPath);
        command.Parameters.AddWithValue("$destination", destinationName);
        command.Parameters.AddWithValue("$name", (object?)targetName ?? DBNull.Value);
        command.Parameters.AddWithValue("$folder", (object?)targetFolder ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", FileLengthOrZero(outputPath));
        command.Parameters.AddWithValue("$now", UtcNowText());
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>The size of the file being sent, so the page can show a percentage.
    /// A missing file is not an error here — the transfer will report it properly.</summary>
    private static long FileLengthOrZero(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Adds a column an older database does not have yet. SQLite has no
    /// "ADD COLUMN IF NOT EXISTS", so the existing columns are read first.
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string columnName, string columnType)
    {
        using (SqliteCommand probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transfers') WHERE name = $column";
            probe.Parameters.AddWithValue("$column", columnName);
            if ((long)probe.ExecuteScalar()! > 0)
            {
                return;
            }
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE transfers ADD COLUMN {columnName} {columnType}";
        alter.ExecuteNonQuery();
    }

    /// <summary>The next item due for work: pending (or retrying past its
    /// next-attempt time), oldest first. Also re-arms items left 'in-progress' by a
    /// crashed host — in-progress with no live owner IS the crash-recovery case.</summary>
    public TransferItem? NextDue(DateTimeOffset nowUtc)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns +
            """
            WHERE state IN ('pending', 'in-progress')
              AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now)
            ORDER BY id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    /// <summary>One transfer by id, or null when it has been removed.</summary>
    public TransferItem? Find(long id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + "WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    /// <summary>Marks an item as being worked on right now, and resets its byte
    /// counter so the progress shown belongs to THIS attempt.</summary>
    public void MarkInProgress(long id) =>
        Execute("UPDATE transfers SET state = 'in-progress', bytes_sent = 0 WHERE id = $id", ("$id", id));

    /// <summary>Persists chunk progress — called after EVERY confirmed chunk
    /// (SPEC §7), so a crash resumes from here, not from zero.</summary>
    public void RecordProgress(long id, string uploadUrl, long confirmedOffset) =>
        Execute(
            "UPDATE transfers SET upload_url = $url, confirmed_offset = $offset, bytes_sent = $offset WHERE id = $id",
            ("$url", uploadUrl), ("$offset", confirmedOffset), ("$id", id));

    /// <summary>
    /// Records how far the current attempt has got, purely so the Transfers page can
    /// show a bar. Unlike <see cref="RecordProgress"/> this carries no resume meaning
    /// — a folder copy starts again from zero — which is why the two are separate.
    /// </summary>
    public void RecordBytesSent(long id, long bytesSent, long totalBytes) =>
        Execute(
            "UPDATE transfers SET bytes_sent = $sent, total_bytes = $total WHERE id = $id",
            ("$sent", bytesSent), ("$total", totalBytes), ("$id", id));

    /// <summary>Transfer confirmed and verified — terminal success.</summary>
    public void Complete(long id) =>
        Execute(
            """
            UPDATE transfers
            SET state = 'completed', completed_utc = $now, last_error = NULL, bytes_sent = total_bytes
            WHERE id = $id
            """,
            ("$now", UtcNowText()), ("$id", id));

    /// <summary>
    /// Records a failure with SPEC §7's classification: transient schedules a
    /// backed-off retry until <paramref name="policy"/> runs out of attempts;
    /// auth-expired pauses until re-authentication; permanent parks in manual-retry.
    /// The server's message is stored VERBATIM.
    /// </summary>
    /// <returns>The attempt number this failure was, so the caller can log it.</returns>
    public int Fail(
        long id, FailureKind kind, string serverMessage, DateTimeOffset nowUtc, TimeSpan? retryAfter,
        RetrySettings? policy = null)
    {
        RetrySettings retries = policy ?? new RetrySettings();
        int attempt = CurrentAttempts(id) + 1;

        // Out of automatic attempts: stop burning the network and say so plainly.
        // The row stays retryable BY HAND — nothing is ever dropped, it just stops
        // trying on its own (SPEC §13 rule 4: the local file is untouched either way).
        bool exhausted = kind == FailureKind.Transient && attempt >= retries.MaxAttempts;

        (string state, string? nextAttempt, string message) = kind switch
        {
            FailureKind.Transient when exhausted => (
                StateManualRetry,
                (string?)null,
                $"Gave up after {attempt} automatic attempts. Last error: {serverMessage}"),
            FailureKind.Transient => (
                StatePending,
                NextAttemptText(nowUtc, attempt, retryAfter, retries),
                serverMessage),
            FailureKind.AuthExpired => (StatePausedAuth, (string?)null, serverMessage),
            _ => (StateManualRetry, (string?)null, serverMessage),
        };

        Execute(
            """
            UPDATE transfers
            SET state = $state, attempts = $attempts, next_attempt_utc = $next, last_error = $error
            WHERE id = $id
            """,
            ("$state", state), ("$attempts", attempt), ("$next", (object?)nextAttempt ?? DBNull.Value),
            ("$error", message), ("$id", id));

        return attempt;
    }

    /// <summary>
    /// Manual retry (UI/CLI): puts a parked, cancelled, or backing-off item back in
    /// the queue immediately, and resets its attempt count — a person choosing to
    /// retry is starting again, not continuing a run that already gave up.
    /// </summary>
    public void Retry(long id) =>
        Execute(
            """
            UPDATE transfers
            SET state = 'pending', next_attempt_utc = NULL, attempts = 0
            WHERE id = $id AND state != 'completed'
            """,
            ("$id", id));

    /// <summary>
    /// Stops a transfer at the user's request. The row stops retrying by itself and
    /// waits — <see cref="Retry"/> is the only thing that brings it back. Used when a
    /// destination is known to be down and the retries are just noise.
    /// </summary>
    public void Cancel(long id) =>
        Execute(
            """
            UPDATE transfers
            SET state = 'cancelled', next_attempt_utc = NULL, last_error = 'Stopped at your request.'
            WHERE id = $id AND state NOT IN ('completed', 'cancelled')
            """,
            ("$id", id));

    /// <summary>Re-arms paused-auth items after a successful re-authentication.</summary>
    public void ResumeAuthPaused() =>
        Execute("UPDATE transfers SET state = 'pending', next_attempt_utc = NULL WHERE state = 'paused-auth'");

    /// <summary>
    /// How much finished history the Transfers page and the CLI show. Anything still
    /// unfinished is listed however old it is — a transfer that has been stuck for
    /// two months is exactly the one somebody needs to see.
    /// </summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// The queue as the UI shows it, newest first: everything unfinished, plus
    /// finished transfers from the last <see cref="HistoryWindow"/>.
    /// </summary>
    /// <remarks>
    /// Without the window this list grew for ever. After a few months of daily
    /// recordings it was thousands of rows of "completed" from recordings that no
    /// longer exist on the machine, which is neither useful nor quick to draw.
    /// <see cref="Prune"/> is what actually keeps the file small; this is only what
    /// gets DISPLAYED.
    /// </remarks>
    public IReadOnlyList<TransferItem> ListRecent(DateTimeOffset nowUtc) =>
        Query(SelectColumns +
              "WHERE state NOT IN ('completed', 'cancelled') OR created_utc >= $since " +
              "ORDER BY id DESC;",
              command => command.Parameters.AddWithValue(
                  "$since", (nowUtc - HistoryWindow).UtcDateTime.ToString("o", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Deletes finished rows that nothing needs any more, and gives the file back the
    /// space they used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two conditions, both required. The row must be older than
    /// <see cref="HistoryWindow"/> AND its recording must be gone from disk.
    /// </para>
    /// <para>
    /// The second condition is not belt-and-braces: <see cref="RetentionCleaner"/>
    /// decides whether a session folder may be deleted by asking whether every
    /// transfer for it completed, so throwing those rows away while the folder is
    /// still there would make the cleaner conclude the recording had never been
    /// transferred anywhere and keep it for ever. History outlives the file it
    /// describes, never the other way round.
    /// </para>
    /// <para>
    /// VACUUM runs only when something was actually deleted. SQLite does not return
    /// freed pages to the filesystem on its own, so without it the file only ever
    /// grows, however few rows are left in it.
    /// </para>
    /// </remarks>
    /// <returns>How many rows were removed.</returns>
    public int Prune(DateTimeOffset nowUtc)
    {
        string cutoff = (nowUtc - HistoryWindow).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        List<(long Id, string OutputPath)> candidates = [];
        using (SqliteConnection connection = Open())
        {
            using SqliteCommand select = connection.CreateCommand();
            select.CommandText =
                "SELECT id, output_path FROM transfers " +
                "WHERE state IN ('completed', 'cancelled') AND created_utc < $cutoff;";
            select.Parameters.AddWithValue("$cutoff", cutoff);
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        long[] removable = [.. candidates.Where(c => !File.Exists(c.OutputPath)).Select(c => c.Id)];
        if (removable.Length == 0)
        {
            return 0;
        }

        using (SqliteConnection connection = Open())
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.CommandText =
                $"DELETE FROM transfers WHERE id IN ({string.Join(",", removable)});";
            delete.ExecuteNonQuery();

            using SqliteCommand vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }

        return removable.Length;
    }

    /// <summary>Runs one SELECT and turns every row into a <see cref="TransferItem"/>.
    /// The list-shaped queries differ only in their WHERE clause.</summary>
    private List<TransferItem> Query(string sql, Action<SqliteCommand> bind)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        using SqliteDataReader reader = command.ExecuteReader();
        var items = new List<TransferItem>();
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    /// <summary>Everything in the queue, newest first — the whole table, however old.
    /// Used by retention and by the tests; the UI uses <see cref="ListRecent"/>.</summary>
    public IReadOnlyList<TransferItem> List()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + "ORDER BY id DESC;";
        using SqliteDataReader reader = command.ExecuteReader();
        var items = new List<TransferItem>();
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
        command.CommandText = "SELECT COUNT(*) FROM transfers WHERE output_path = $path AND state != 'completed'";
        command.Parameters.AddWithValue("$path", outputPath);
        return (long)command.ExecuteScalar()! == 0;
    }

    /// <summary>Destination names this output has already been sent to successfully.
    /// Drives "send again" so it is offered only where it would actually do
    /// something.</summary>
    public IReadOnlyList<string> CompletedDestinationsFor(string outputPath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT destination_name FROM transfers WHERE output_path = $path AND state = 'completed'";
        command.Parameters.AddWithValue("$path", outputPath);
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// Every completed transfer as (file, destinations it reached), for ALL files at
    /// once.
    /// </summary>
    /// <remarks>
    /// The recordings list needs this for every recording it shows. Asking per file
    /// meant one query per output per page refresh — fine with three recordings,
    /// visibly not fine with three hundred. One query answers the whole page.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CompletedDestinationsByOutput()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT output_path, destination_name FROM transfers WHERE state = 'completed'";

        var byOutput = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string outputPath = reader.GetString(0);
            if (!byOutput.TryGetValue(outputPath, out List<string>? destinations))
            {
                destinations = [];
                byOutput[outputPath] = destinations;
            }

            destinations.Add(reader.GetString(1));
        }

        return byOutput.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The exponential backoff with jitter (SPEC §7): each attempt waits about twice
    /// as long as the last, starting at <see cref="RetrySettings.FirstRetrySeconds"/>
    /// and never exceeding <see cref="RetrySettings.MaxRetrySeconds"/>. The ±20 %
    /// jitter stops several transfers to the same dead destination retrying in step.
    /// </summary>
    internal static TimeSpan Backoff(int attempt, TimeSpan? serverRetryAfter, RetrySettings retries)
    {
        if (serverRetryAfter is { } fromServer)
        {
            return fromServer; // The server's own delay always wins (SPEC §7).
        }

        double doublings = Math.Min(Math.Max(attempt - 1, 0), 16);
        double seconds = Math.Min(retries.MaxRetrySeconds, retries.FirstRetrySeconds * Math.Pow(2, doublings));
        return TimeSpan.FromSeconds(seconds * (0.8 + (Random.Shared.NextDouble() * 0.4)));
    }

    private static string NextAttemptText(
        DateTimeOffset nowUtc, int attempt, TimeSpan? retryAfter, RetrySettings retries) =>
        (nowUtc + Backoff(attempt, retryAfter, retries)).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private int CurrentAttempts(long id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT attempts FROM transfers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The column list every read shares, so <see cref="ReadItem"/>'s
    /// ordinals can never drift out of step with one of the queries.</summary>
    private const string SelectColumns =
        """
        SELECT id, output_path, destination_name, target_name, target_folder, state, attempts,
               next_attempt_utc, upload_url, confirmed_offset, bytes_sent, total_bytes,
               last_error, created_utc
        FROM transfers
        """ + "\n";

    private static TransferItem ReadItem(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        OutputPath: reader.GetString(1),
        DestinationName: reader.GetString(2),
        TargetName: reader.IsDBNull(3) ? null : reader.GetString(3),
        TargetFolder: reader.IsDBNull(4) ? null : reader.GetString(4),
        State: reader.GetString(5),
        Attempts: reader.GetInt32(6),
        NextAttemptUtc: reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        UploadUrl: reader.IsDBNull(8) ? null : reader.GetString(8),
        ConfirmedOffset: reader.GetInt64(9),
        BytesSent: reader.GetInt64(10),
        TotalBytes: reader.GetInt64(11),
        LastError: reader.IsDBNull(12) ? null : reader.GetString(12),
        CreatedUtc: DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));

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
/// <param name="TargetName">The name this file takes at its destination, from the
/// destination's naming pattern. Null means "keep the recording's own name".</param>
/// <param name="TargetFolder">The destination folder with its tokens already
/// expanded, fixed when the transfer was queued. Null means "as configured".</param>
/// <param name="BytesSent">How much of the CURRENT attempt has been sent — for the
/// progress bar only.</param>
/// <param name="TotalBytes">The file's size when it was queued; 0 when unknown.</param>
public sealed record TransferItem(
    long Id,
    string OutputPath,
    string DestinationName,
    string? TargetName,
    string? TargetFolder,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? UploadUrl,
    long ConfirmedOffset,
    long BytesSent,
    long TotalBytes,
    string? LastError,
    DateTimeOffset CreatedUtc)
{
    /// <summary>How far this attempt has got, 0…1, or null when the size is unknown
    /// or the state makes progress meaningless.</summary>
    public double? Progress =>
        TotalBytes > 0 && State is TransferQueue.StateInProgress or TransferQueue.StateCompleted
            ? Math.Clamp(BytesSent / (double)TotalBytes, 0, 1)
            : null;
}

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
