using System.Collections.ObjectModel;
using System.Globalization;

using Captr.App.Services;
using Captr.Core.Common;
using Captr.Core.Ipc;
using Captr.Core.Settings;
using Captr.Core.Transfers;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The Transfers page: every transfer with where it is going, how far it has got,
/// which attempt it is on, and the server's verbatim error beside a plain-English
/// explanation (SPEC §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Retry only re-sends what failed.</b> The queue holds one row per (recording,
/// destination) pair — see <see cref="TransferQueue"/> — so a recording going to two
/// places is two independent transfers. If one succeeds and the other does not, the
/// successful one is already marked completed and retrying the failed one cannot
/// touch it. That is why this page lists destinations, not recordings.
/// </para>
/// <para>
/// <b>Retry and Stop are offered only where they mean something.</b> Retry appears
/// on a transfer that has stopped — failed, refused, out of attempts, or stopped by
/// hand. Stop appears on one that is still going — queued, sending, or waiting out a
/// backoff. Neither appears on a completed transfer. Offering Retry on a queued
/// transfer, as this page used to, invites a click that does nothing.
/// </para>
/// </remarks>
public sealed partial class TransfersViewModel : ObservableObject
{
    private readonly HostConnection _host;
    private readonly SettingsStore _settings = new();

    /// <summary>
    /// One queue for the life of the page, not one per refresh. Constructing a
    /// TransferQueue opens a connection and (the first time in the process) loads
    /// native SQLite; this page refreshes every three seconds.
    /// </summary>
    private readonly TransferQueue _queue = new();

    /// <summary>False until the first read of the queue has finished. The empty-state
    /// message is held back until then — announcing "nothing has been transferred yet"
    /// while still looking is worse than showing nothing at all.</summary>
    private bool _loaded;

    public ObservableCollection<TransferRow> Transfers { get; } = [];

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _inProgressCount;

    [ObservableProperty]
    private int _waitingCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private bool _hasFailures;

    [ObservableProperty]
    private bool _hasAny;

    /// <summary>The retry policy in force, shown on the page so "attempt 3 of 5"
    /// means something and the limit is not a mystery number.</summary>
    [ObservableProperty]
    private string _retryPolicyText = "";

    public TransfersViewModel(HostConnection host) => _host = host;

    /// <summary>
    /// Re-reads the queue. The queue is a SQLite database in local application data
    /// and this reads it DIRECTLY rather than asking the host — opening the page must
    /// not start a recording host just to look at a list, and a queue with nothing in
    /// it has no host to ask in the first place. Retrying and stopping still go
    /// through the host, because both change what the transfer worker is doing.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            RetrySettings retries = LoadRetrySettings();
            // Finished transfers older than TransferQueue.HistoryWindow are not shown:
            // the page is for what is happening and what just happened, not an
            // archive of every recording the machine has ever sent.
            IReadOnlyList<TransferItem> items = await Task.Run(
                () => _queue.ListRecent(DateTimeOffset.UtcNow)).ConfigureAwait(true);
            _loaded = true;

            Transfers.Clear();
            foreach (TransferItem item in items)
            {
                Transfers.Add(new TransferRow(item, retries));
            }

            CompletedCount = Transfers.Count(t => t.Severity == TransferSeverity.Done);
            InProgressCount = Transfers.Count(t => t.Severity == TransferSeverity.Active);
            WaitingCount = Transfers.Count(t => t.Severity == TransferSeverity.Waiting);
            FailedCount = Transfers.Count(t => t.Severity == TransferSeverity.Failed);
            HasFailures = FailedCount > 0;
            HasAny = Transfers.Count > 0;

            RetryPolicyText =
                $"Retries automatically up to {retries.MaxAttempts} times, waiting " +
                $"{DescribeSeconds(retries.FirstRetrySeconds)} and then roughly twice as long each time, " +
                $"up to {DescribeSeconds(retries.MaxRetrySeconds)}. Change this in Settings.";

            Message = HasAny || !_loaded
                ? ""
                : "Nothing has been transferred yet.\nFinished recordings appear here once at least one " +
                  "destination is set up in Settings.";
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Message = "The transfer queue could not be read: " + exception.Message;
        }
    }

    /// <summary>The retry policy, falling back to the shipped defaults if the
    /// settings file is unreadable — a broken settings file must not blank the page.
    /// </summary>
    private RetrySettings LoadRetrySettings()
    {
        try
        {
            return _settings.Load().Retries;
        }
        catch (Exception exception) when (exception is IOException or SettingsValidationException)
        {
            return new RetrySettings();
        }
    }

    private static string DescribeSeconds(int seconds) =>
        seconds >= 120
            ? string.Create(CultureInfo.CurrentCulture, $"{seconds / 60} minutes")
            : string.Create(CultureInfo.CurrentCulture, $"{seconds} seconds");

    /// <summary>Re-queues ONE transfer — this destination, this recording. Any other
    /// destination for the same recording is untouched.</summary>
    [RelayCommand]
    private Task RetryAsync(TransferRow row) =>
        SendAsync(IpcKinds.RetryTransfer, new RetryTransferRequest(row.Id));

    /// <summary>Stops one transfer. It keeps its place in the list and can be started
    /// again with Retry; the local recording is never touched either way.</summary>
    [RelayCommand]
    private Task StopAsync(TransferRow row) =>
        SendAsync(IpcKinds.CancelTransfer, new CancelTransferRequest(row.Id));

    /// <summary>Re-queues every transfer that needs a human — after fixing the cause
    /// (a permission, a full disk, an expired credential) this is the one click that
    /// puts them all back in flight.</summary>
    [RelayCommand]
    private async Task RetryAllAsync()
    {
        foreach (TransferRow row in Transfers.Where(t => t.Severity == TransferSeverity.Failed).ToList())
        {
            if (!await TrySendAsync(IpcKinds.RetryTransfer, new RetryTransferRequest(row.Id)))
            {
                break;
            }
        }

        await RefreshAsync();
    }

    private async Task SendAsync(string kind, object payload)
    {
        await TrySendAsync(kind, payload);
        await RefreshAsync();
    }

    /// <summary>Sends one host request, reporting a failure on the page rather than
    /// throwing. Returns false when the host could not be reached, so a bulk action
    /// stops instead of repeating the same failure once per row.</summary>
    private async Task<bool> TrySendAsync(string kind, object payload)
    {
        try
        {
            await _host.RequestAsync<StateResponse>(
                kind, payload, startHostIfNeeded: true, CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException)
        {
            Message = exception.Message;
            return false;
        }
    }
}

/// <summary>How much attention a transfer needs — drives both the summary counts and
/// the colour of its chip.</summary>
public enum TransferSeverity
{
    /// <summary>At the destination and verified. Nothing to do.</summary>
    Done,

    /// <summary>Being sent right now.</summary>
    Active,

    /// <summary>Queued, or waiting out a backoff before retrying by itself.</summary>
    Waiting,

    /// <summary>Stopped and will not proceed without a person.</summary>
    Failed,
}

/// <summary>One transfer row, with the plain-English reading of its state.</summary>
public sealed record TransferRow
{
    public TransferRow(TransferItem item, RetrySettings retries)
    {
        Id = item.Id;
        File = item.TargetName ?? Path.GetFileName(item.OutputPath);
        Destination = item.DestinationName;
        Size = MeasureSize(item);
        Started = item.CreatedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        ServerError = item.LastError ?? "";
        Progress = item.Progress ?? 0;
        ShowProgress = item.State == TransferQueue.StateInProgress && item.Progress is > 0;

        (StateLabel, Severity, Explanation) = Describe(item, retries);
        AttemptsText = DescribeAttempts(item, retries);
    }

    /// <summary>
    /// The size of the file being sent. The queue records it at enqueue time, but a
    /// row queued by an older Captr has no size stored, so the local file is measured
    /// instead — cheap, and it keeps the column useful across an upgrade.
    /// </summary>
    private static string MeasureSize(TransferItem item)
    {
        if (item.TotalBytes > 0)
        {
            return ByteSize.Format(item.TotalBytes);
        }

        try
        {
            return ByteSize.Format(new FileInfo(item.OutputPath).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "—";
        }
    }

    /// <summary>
    /// The state as a person reads it: a chip label, how much attention it needs, and
    /// what to do about it. The chip label is what the page shows; the explanation
    /// appears underneath only when there is something to act on.
    /// </summary>
    private static (string Label, TransferSeverity Severity, string Explanation) Describe(
        TransferItem item, RetrySettings retries) => item.State switch
        {
            // No explanation for the happy path: "TRANSFERRED" already says it, and a
            // sentence repeating it under every completed row is pure noise.
            TransferQueue.StateCompleted => ("Transferred", TransferSeverity.Done, ""),

            TransferQueue.StateInProgress => ("Sending", TransferSeverity.Active, ""),

            TransferQueue.StatePending when item.NextAttemptUtc is { } next => (
                "Retrying",
                TransferSeverity.Waiting,
                $"Waiting until {next.ToLocalTime():HH:mm:ss} before trying again. This happens by itself."),

            TransferQueue.StatePending => ("Queued", TransferSeverity.Waiting, ""),

            TransferQueue.StatePausedAuth => (
                "Sign-in needed",
                TransferSeverity.Failed,
                "The stored credential no longer works. Open the destination in Settings and enter the " +
                "client secret again, then retry."),

            TransferQueue.StateCancelled => (
                "Stopped",
                TransferSeverity.Failed,
                "You stopped this transfer. It will not try again until you press Retry."),

            TransferQueue.StateManualRetry when item.Attempts >= retries.MaxAttempts => (
                "Gave up",
                TransferSeverity.Failed,
                $"Stopped after {item.Attempts} automatic attempts. Fix the cause, then retry."),

            TransferQueue.StateManualRetry => (
                "Refused",
                TransferSeverity.Failed,
                "The destination refused this transfer — the server's exact words are below. Fix the cause, then retry."),

            _ => (item.State, TransferSeverity.Waiting, ""),
        };

    /// <summary>
    /// "Attempt 3 of 5" rather than a bare count. Which number the retries stop at is
    /// the thing people actually want to know when they are watching something fail,
    /// and a lone "3 attempts" answers the wrong half of the question.
    /// </summary>
    private static string DescribeAttempts(TransferItem item, RetrySettings retries)
    {
        if (item.Attempts == 0)
        {
            return "";
        }

        // A row can hold more attempts than the current limit allows — it was queued
        // before the limit was lowered. "attempt 10 of 5" is nonsense, so those fall
        // back to a plain count.
        if (item.State == TransferQueue.StateCompleted || item.Attempts > retries.MaxAttempts)
        {
            int count = item.State == TransferQueue.StateCompleted ? item.Attempts + 1 : item.Attempts;
            return count == 1
                ? "1 attempt"
                : string.Create(CultureInfo.CurrentCulture, $"{count} attempts");
        }

        return string.Create(CultureInfo.CurrentCulture, $"attempt {item.Attempts} of {retries.MaxAttempts}");
    }

    public long Id { get; }

    /// <summary>The name this copy takes at its destination.</summary>
    public string File { get; }

    /// <summary>Where this copy is going.</summary>
    public string Destination { get; }

    public string Size { get; }

    /// <summary>When the transfer was queued.</summary>
    public string Started { get; }

    /// <summary>Short label for the chip.</summary>
    public string StateLabel { get; }

    public TransferSeverity Severity { get; }

    public string AttemptsText { get; }

    /// <summary>0…1 for the bar; only meaningful while <see cref="ShowProgress"/>.</summary>
    public double Progress { get; }

    /// <summary>A bar is drawn only for a transfer actually moving. A full bar under
    /// every completed row would say nothing and make the table twice as tall.</summary>
    public bool ShowProgress { get; }

    /// <summary>The server's message, verbatim (SPEC §9).</summary>
    public string ServerError { get; }

    /// <summary>What the state means and what to do about it; empty when the state
    /// speaks for itself.</summary>
    public string Explanation { get; }

    /// <summary>Retry is for transfers that have STOPPED. Offering it on a queued or
    /// running transfer would be a button that does nothing.</summary>
    public bool CanRetry => Severity == TransferSeverity.Failed;

    /// <summary>Stop is for transfers still going: queued, sending, or backing off.</summary>
    public bool CanStop => Severity is TransferSeverity.Active or TransferSeverity.Waiting;
}
