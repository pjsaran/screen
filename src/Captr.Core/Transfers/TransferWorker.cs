using Captr.Core.Settings;

using Serilog;

namespace Captr.Core.Transfers;

/// <summary>
/// The host's background loop draining the transfer queue (SPEC §7). Owns dispatch
/// to the right destination kind and the failure classification on the way back.
/// A transfer failure never touches the local file — the worst outcome here is a
/// row parked for manual retry (SPEC §13 rule 4).
/// </summary>
public sealed class TransferWorker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>The shortest an attempt is ever allowed, however small the file.
    /// Covers connecting, authenticating, and a slow first byte.</summary>
    private static readonly TimeSpan MinimumAttemptTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The throughput an attempt is assumed to beat. Deliberately pessimistic: a
    /// destination slower than this for an entire transfer is stuck, not slow. Used
    /// only to derive a deadline, never to throttle.
    /// </summary>
    private const double AssumedBytesPerSecond = 1_000_000;

    /// <summary>
    /// How often the byte counter is written to the queue while a copy runs. The
    /// page reads it to draw a progress bar; writing on every 1 MB block would mean
    /// thousands of small database writes per transfer for a bar nobody can see move
    /// that finely.
    /// </summary>
    private static readonly TimeSpan ProgressWriteInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often a running transfer checks whether someone pressed Stop.
    /// Two seconds is fast enough to feel immediate and slow enough to be free.</summary>
    private static readonly TimeSpan StopCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long one attempt at this file may take before it is abandoned and retried.
    /// Scales with size so a 5 GB recording is not cut off at the same deadline as a
    /// 5 MB one.
    /// </summary>
    internal static TimeSpan AttemptTimeoutFor(string path)
    {
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return MinimumAttemptTimeout;
        }

        var scaled = TimeSpan.FromSeconds(bytes / AssumedBytesPerSecond);
        return scaled > MinimumAttemptTimeout ? scaled : MinimumAttemptTimeout;
    }

    private readonly TransferQueue _queue;
    private readonly SettingsStore _settingsStore;
    private readonly Func<DestinationSettings, IAccessTokenProvider> _tokenProviderFactory;
    private readonly HttpClient _graphHttp;
    private readonly bool _ownsGraphHttp;
    private readonly ILogger _log;

    public TransferWorker(
        TransferQueue queue,
        SettingsStore settingsStore,
        ILogger log,
        Func<DestinationSettings, IAccessTokenProvider>? tokenProviderFactory = null,
        HttpClient? graphHttp = null)
    {
        _queue = queue;
        _settingsStore = settingsStore;
        _log = log.ForContext<TransferWorker>();
        _tokenProviderFactory = tokenProviderFactory ?? (destination => new MsalTokenProvider(destination));
        // Only a client WE created may be disposed: an injected one belongs to the
        // caller (the tests share one across cases) and closing it would break them.
        _ownsGraphHttp = graphHttp is null;
        _graphHttp = graphHttp ?? new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),

            // Per REQUEST, i.e. per chunk — not per transfer. A chunk is a few hundred
            // kilobytes, so a minute of silence means the connection is gone, and
            // waiting the default 100 s for each of them just multiplies the delay.
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>True while any transfer is pending or running — keeps the host from
    /// idling out mid-transfer.</summary>
    public bool HasPendingWork =>
        _queue.List().Any(i => i.State is TransferQueue.StatePending or TransferQueue.StateInProgress);

    /// <summary>Drains the queue until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TransferItem? item = _queue.NextDue(DateTimeOffset.UtcNow);
            if (item is null)
            {
                try
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            bool attempted;
            try
            {
                attempted = await TransferOneAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // TransferOneAsync lands every outcome in the queue itself; reaching
                // here means even THAT failed (the database is locked or damaged).
                // The loop must survive it: a dead worker used to strand every
                // transfer until the host restarted, with nothing in the log.
                _log.Error(exception, "The transfer worker could not process transfer {Id}; trying again shortly", item.Id);
                attempted = false;
            }

            if (!attempted)
            {
                // Nothing changed on the row, so NextDue would hand it straight back:
                // wait before looking again instead of spinning.
                try
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One attempt at one item; every outcome lands back in the queue.</summary>
    /// <returns>False when no attempt was made (settings unreadable, or the item was
    /// stopped before it could be claimed), so the caller can pause before looking again.</returns>
    /// <remarks>
    /// <para>
    /// The attempt is bounded by <see cref="AttemptTimeoutFor"/>. Without it, a share
    /// that stops responding mid-copy or an upload whose connection dies without a
    /// reset leaves the transfer showing "Sending" forever, with nothing to rescue it
    /// — which is exactly what happens in practice on a flaky network drive.
    /// A timeout is treated as TRANSIENT: the queue backs off and tries again, and a
    /// genuinely dead destination simply keeps failing until the configured attempt
    /// limit parks it for a person.
    /// </para>
    /// <para>
    /// A transfer the user stopped mid-flight is honoured here too: the row is
    /// re-read after the attempt begins, and a cancellation abandons the copy rather
    /// than letting it run to completion against the user's wishes.
    /// </para>
    /// </remarks>
    public async Task<bool> TransferOneAsync(TransferItem item, CancellationToken cancellationToken)
    {
        RetrySettings retries;
        DestinationSettings? destination;
        try
        {
            CaptrSettings settings = _settingsStore.Load();
            retries = settings.Retries;
            destination = settings.Destinations.FirstOrDefault(d => d.Name == item.DestinationName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error(exception, "Settings could not be read; transfers wait until they can be");
            return false;
        }

        // Claimed only if still waiting: a Stop pressed between NextDue and here used
        // to be overwritten, and the transfer ran anyway.
        if (!_queue.MarkInProgress(item.Id))
        {
            return false;
        }

        TimeSpan timeout = AttemptTimeoutFor(item.OutputPath);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(timeout);
        CancellationToken attemptToken = attemptCts.Token;

        // Stop is pressed in another process (the UI), so the only way this loop
        // hears about it is by looking. Without this poll, "Stop" on a five-gigabyte
        // upload would mark the row cancelled and then sit watching the upload finish
        // anyway — which is not what the button says it does.
        using var stopWatch = new Timer(
            _ =>
            {
                if (Find(item.Id)?.State == TransferQueue.StateCancelled)
                {
                    try
                    {
                        attemptCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The attempt finished while this tick was already running.
                        // Timer.Dispose does not wait for a callback in flight, and an
                        // exception escaping a timer callback terminates the process.
                    }
                }
            },
            null, StopCheckInterval, StopCheckInterval);

        try
        {
            if (destination is null || !destination.Enabled)
            {
                _queue.Fail(item.Id, FailureKind.Permanent,
                    $"Destination '{item.DestinationName}' no longer exists or is disabled.",
                    DateTimeOffset.UtcNow, null, retries);
                return true;
            }

            if (!File.Exists(item.OutputPath))
            {
                _queue.Fail(item.Id, FailureKind.Permanent,
                    $"The local file is missing: {item.OutputPath}", DateTimeOffset.UtcNow, null, retries);
                return true;
            }

            // The destination may give this file its own name; when it does not, the
            // recording keeps the name it already has locally.
            string targetName = item.TargetName ?? Path.GetFileName(item.OutputPath);

            switch (destination.Kind)
            {
                case DestinationKind.Folder:
                    string folder = ResolveFolder(item, destination.FolderPath!);
                    if (Naming.OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder: true) is { } unusable)
                    {
                        // Classified as permanent below, with these words.
                        throw new ArgumentException(unusable);
                    }

                    string landed = await FolderDestination.TransferAsync(
                        item.OutputPath, folder, targetName,
                        ProgressReporterFor(item), attemptToken).ConfigureAwait(false);
                    _log.Information("Transferred {File} to {Landed}", item.OutputPath, landed);
                    break;

                case DestinationKind.SharePoint:
                    await UploadToSharePointAsync(item, destination, targetName, attemptToken).ConfigureAwait(false);
                    break;

                default:
                    // Settings validation refuses unimplemented kinds, so reaching
                    // here means a destination changed kind after being queued.
                    _queue.Fail(item.Id, FailureKind.Permanent,
                        $"Captr cannot send to {DestinationKinds.Describe(destination.Kind).DisplayName} yet.",
                        DateTimeOffset.UtcNow, null, retries);
                    return true;
            }

            _queue.Complete(item.Id);
        }
        catch (TransferException exception)
        {
            int attempt = _queue.Fail(
                item.Id, exception.Kind, exception.Message, DateTimeOffset.UtcNow, exception.RetryAfter, retries);
            _log.Warning("Transfer {Id} failed on attempt {Attempt} of {Max} ({Kind}): {Message}",
                item.Id, attempt, retries.MaxAttempts, exception.Kind, exception.Message);
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (Find(item.Id)?.State == TransferQueue.StateCancelled)
            {
                // The user pressed Stop. The row already says so; overwriting it with
                // a failure would replace their decision with an error message.
                _log.Information("Transfer {Id} stopped mid-flight at the user's request", item.Id);
                return true;
            }

            // Our own deadline, not a host shutdown: the transfer made no progress
            // for far longer than the file could justify.
            string message =
                $"The transfer was still running after {timeout.TotalMinutes:F0} minutes and was abandoned. " +
                "If this keeps happening, the destination is probably unreachable or extremely slow.";
            _log.Warning("Transfer {Id} timed out after {Timeout}; re-queued for retry", item.Id, timeout);
            _queue.Fail(item.Id, FailureKind.Transient, message, DateTimeOffset.UtcNow, null, retries);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TaskCanceledException)
        {
            // Network hiccups and file locks are transient by definition.
            int attempt = _queue.Fail(
                item.Id, FailureKind.Transient, exception.Message, DateTimeOffset.UtcNow, null, retries);
            _log.Warning("Transfer {Id} failed transiently on attempt {Attempt} of {Max}: {Message}",
                item.Id, attempt, retries.MaxAttempts, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Everything else, classified - never allowed to escape. Before this, a
            // missing credential, an access-denied share, a malformed Graph reply, or a
            // busy database killed the worker outright: the row stayed "in progress"
            // for ever, the host never went idle, and the next host start picked the
            // same row first and died the same way, blocking every transfer behind it.
            (FailureKind kind, string message) = Classify(exception);
            int attempt = _queue.Fail(item.Id, kind, message, DateTimeOffset.UtcNow, null, retries);
            _log.Warning(exception, "Transfer {Id} failed on attempt {Attempt} of {Max} ({Kind}): {Message}",
                item.Id, attempt, retries.MaxAttempts, kind, message);
        }

        return true;
    }

    /// <summary>
    /// What an unexpected failure means for retrying. A credential problem waits for a
    /// person to fix the credential; a refusal that no retry can change parks for a
    /// person; anything else is retried within the attempt limit, which is the safe
    /// default when the cause is unknown - the local file is untouched either way.
    /// </summary>
    internal static (FailureKind Kind, string Message) Classify(Exception exception) => exception switch
    {
        Secrets.CredentialNotFoundException => (FailureKind.AuthExpired, exception.Message),
        UploadSessionExpiredException => (FailureKind.Transient,
            "SharePoint refused a newly created upload session. The next attempt starts again."),
        System.Security.Cryptography.CryptographicException => (FailureKind.AuthExpired,
            "The stored secret for this destination could not be read: it was saved by a different Windows " +
            "account or on another PC. Open the destination in Settings and enter the secret again."),
        Microsoft.Identity.Client.MsalServiceException msal when msal.StatusCode is 400 or 401 => (FailureKind.AuthExpired,
            "Microsoft sign-in refused the destination's credentials: " + msal.Message),
        Microsoft.Identity.Client.MsalException msal => (FailureKind.Transient,
            "Signing in to Microsoft failed: " + msal.Message),
        UnauthorizedAccessException => (FailureKind.Permanent,
            "Access was refused: " + exception.Message + " Check this Windows account can write to the destination, then press Retry."),
        ArgumentException or NotSupportedException or PathTooLongException => (FailureKind.Permanent,
            "The destination path is not usable: " + exception.Message + " Fix the destination in Settings, then press Retry."),
        _ => (FailureKind.Transient, exception.Message),
    };

    /// <summary>
    /// Writes copy progress back to the queue so the Transfers page can draw a bar,
    /// rate-limited to one write a second. Progress is cosmetic — dropping a report
    /// costs nothing, so this never blocks or throws into the copy.
    /// </summary>
    private ThrottledProgress ProgressReporterFor(TransferItem item) =>
        new ThrottledProgress(
            _queue, item.Id,
            item.TotalBytes > 0 ? item.TotalBytes : FileLengthOrZero(item.OutputPath),
            ProgressWriteInterval);

    /// <summary>
    /// Records copy progress synchronously on the copying thread. Deliberately NOT
    /// <see cref="Progress{T}"/>, which posts each report to the thread pool: reports
    /// would then land out of order and could arrive after the transfer had already
    /// been marked complete.
    /// </summary>
    private sealed class ThrottledProgress(
        TransferQueue queue, long id, long totalBytes, TimeSpan interval) : IProgress<double>
    {
        private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

        public void Report(double fraction)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _lastWrite < interval && fraction < 1)
            {
                return;
            }

            _lastWrite = now;
            try
            {
                queue.RecordBytesSent(id, (long)(totalBytes * fraction), totalBytes);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // A busy database is not a reason to fail a transfer that is working.
            }
        }
    }

    /// <summary>
    /// The folder this transfer actually lands in: the one resolved when it was
    /// queued, or — failing that — the configured folder with its tokens expanded
    /// here.
    /// </summary>
    /// <remarks>
    /// The queued value is preferred because it was resolved against the RECORDING's
    /// own timestamp, which is what a dated folder should follow. The fallback exists
    /// because a row can reach the worker without one: it was queued by an older
    /// Captr, or by a code path with no naming context. Without the fallback a folder
    /// like <c>C:\Videos\{date:yyyy_MM_dd}</c> reaches the file system with the braces
    /// still in it and every attempt fails with "the directory name is invalid" — which
    /// is exactly what it did.
    /// </remarks>
    private static string ResolveFolder(TransferItem item, string configured)
    {
        if (item.TargetFolder is { Length: > 0 } queued)
        {
            return queued;
        }

        if (!configured.Contains('{', StringComparison.Ordinal))
        {
            return configured;
        }

        // Everything the tokens need, from what the queue row itself knows. The
        // recording's own end time is not recorded here, so the moment it was queued
        // stands in for it — a few seconds out, and only for rows that had no
        // resolved folder to begin with.
        var context = new Naming.NamingContext
        {
            StartUtc = item.CreatedUtc,
            EndUtc = item.CreatedUtc,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            TimeZone = TimeZoneInfo.Local,
        };

        return Naming.OutputNamer.ExpandFolderPath(configured, context);
    }

    /// <summary>Re-reads one queue row, tolerating a momentarily busy database. Used
    /// only to notice a Stop, so a missed read simply means noticing on the next
    /// tick.</summary>
    private TransferItem? Find(long id)
    {
        try
        {
            return _queue.Find(id);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

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

    private async Task UploadToSharePointAsync(
        TransferItem item, DestinationSettings destination, string targetName, CancellationToken cancellationToken)
    {
        // Without a drive id, Graph cannot be told WHICH document library to upload
        // into — old destinations saved before the field existed hit this. Permanent,
        // because no number of retries fills in a missing setting.
        if (string.IsNullOrWhiteSpace(destination.SharePointDriveId))
        {
            throw new TransferException(FailureKind.Permanent,
                $"Destination '{destination.Name}' has no Drive ID. Open it in Settings, enter the document " +
                "library's drive id, and press Test connection.");
        }

        var uploader = new GraphUploader(_graphHttp, _tokenProviderFactory(destination));

        string folder = ResolveFolder(item, destination.SharePointFolder ?? "").Trim('/');
        string driveItemPath = $"{folder}/{targetName}".TrimStart('/');

        string? uploadUrl = item.UploadUrl;
        long confirmedOffset = item.ConfirmedOffset;
        bool sessionExpired = false;
        while (true)
        {
            if (uploadUrl is null)
            {
                // After an expired session, the upload may in fact have finished: the
                // final chunk landed and only its reply was lost. Starting again would
                // put a second copy beside it ("name 1.mkv"), so look first.
                if (sessionExpired && await uploader.AlreadyUploadedAsync(
                        destination.SharePointDriveId, driveItemPath, new FileInfo(item.OutputPath).Length,
                        cancellationToken).ConfigureAwait(false))
                {
                    _log.Information("{File} is already in SharePoint destination {Destination}; its last reply was lost",
                        item.OutputPath, destination.Name);
                    return;
                }

                uploadUrl = await uploader.CreateSessionAsync(
                    destination.SharePointDriveId, driveItemPath, cancellationToken).ConfigureAwait(false);
                confirmedOffset = 0;
            }

            string sessionUrl = uploadUrl;
            try
            {
                await uploader.UploadAsync(
                    sessionUrl, item.OutputPath, confirmedOffset,
                    // Persist EVERY confirmed chunk (SPEC §7) — this is what an interrupted
                    // upload resumes from, and it doubles as the progress the page shows.
                    offset => _queue.RecordProgress(item.Id, sessionUrl, offset),
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (UploadSessionExpiredException) when (!sessionExpired)
            {
                // Once per attempt: a brand-new session that is ALSO refused is not an
                // expiry, and falls through to the ordinary failure handling.
                _log.Information("Upload session for transfer {Id} has expired; starting a new one", item.Id);
                _queue.ForgetUploadSession(item.Id);
                sessionExpired = true;
                uploadUrl = null;
            }
        }

        _log.Information("Uploaded {File} to SharePoint destination {Destination}",
            item.OutputPath, destination.Name);
    }
    /// <summary>Releases the HTTP connection pool held for Graph uploads.</summary>
    /// <remarks>The worker used to hold an HttpClient it never disposed. It lives as
    /// long as the host process does, so nothing went wrong in practice - but a type
    /// that owns a disposable and cannot be closed is a trap for the next caller who
    /// creates one per operation.</remarks>
    public void Dispose()
    {
        if (_ownsGraphHttp)
        {
            _graphHttp.Dispose();
        }
    }

}
