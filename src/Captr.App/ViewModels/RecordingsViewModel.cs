using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;

using Captr.App.Services;
using Captr.Core.Ipc;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Transfers;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The recordings page: past sessions with their length, size, and the actions
/// people actually take — play, open the folder, send to destinations again, delete.
/// Deletion happens in the view because it requires the typed confirmation dialog;
/// the view model only executes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list keeps itself current.</b> Recordings are read straight from the
/// working folder by <see cref="RecordingCatalog"/> — no host, no IPC — so opening
/// the page shows them immediately, and a <see cref="FileSystemWatcher"/> on that
/// folder brings in anything that appears while the page is open. Refresh is still
/// there, but nobody should ever have to press it.
/// </para>
/// <para>
/// The watcher is only alive while the page is on screen (see
/// <see cref="StartWatching"/>/<see cref="StopWatching"/>); a watcher left running
/// holds a directory handle and a background thread for the life of the process.
/// </para>
/// </remarks>
public sealed partial class RecordingsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long to wait after a folder change before rescanning. A finishing
    /// recording writes several files in a burst; one rescan after the burst is
    /// enough, and rescanning per file would fight the writer for the same handles.</summary>
    private static readonly TimeSpan RescanDelay = TimeSpan.FromMilliseconds(700);

    private readonly HostConnection _host;
    private readonly SettingsStore _settings = new();
    private readonly Lock _watcherGate = new();

    /// <summary>
    /// One queue for the life of the page. The list is rescanned on every page entry
    /// and again whenever the folder watcher fires, so constructing one each time was
    /// pure overhead.
    /// </summary>
    private readonly TransferQueue? _queue = TryOpenQueue();

    /// <summary>False until the first scan has finished, so the "no recordings yet"
    /// message is not shown while the folder is still being read.</summary>
    private bool _loaded;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _pendingRescan;

    public ObservableCollection<RecordingRow> Recordings { get; } = [];

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _workingFolder = "";

    /// <summary>True once there is at least one recording to show. The table (header
    /// strip and all) is hidden until then, so an empty page is the empty-state
    /// message alone rather than a set of column headings over nothing.</summary>
    [ObservableProperty]
    private bool _hasAny;

    public RecordingsViewModel(HostConnection host) => _host = host;

    /// <summary>Rescans the working folder. Cheap enough to call freely.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        CaptrSettings settings = _settings.Load();
        string folder = settings.WorkingFolder;
        WorkingFolder = folder;

        string[] destinations = [.. settings.Destinations.Where(d => d.Enabled).Select(d => d.Name)];

        (IReadOnlyList<RecordingSummary> summaries, Dictionary<string, string[]> outstanding) =
            await Task.Run(() =>
            {
                IReadOnlyList<RecordingSummary> scanned = RecordingCatalog.Scan(folder);
                return (scanned, FindOutstandingDestinations(scanned, destinations));
            }).ConfigureAwait(true);

        _loaded = true;

        // Rebuild in place so the selection and scroll position survive a rescan
        // triggered by the watcher while the user is reading the list.
        var byFolder = Recordings.ToDictionary(r => r.Folder, StringComparer.OrdinalIgnoreCase);
        Recordings.Clear();
        foreach (RecordingSummary summary in summaries)
        {
            string[] pending = outstanding.GetValueOrDefault(summary.Folder, []);
            Recordings.Add(byFolder.TryGetValue(summary.Folder, out RecordingRow? existing)
                ? existing.WithUpdatedFacts(summary, pending, destinations.Length)
                : new RecordingRow(summary, pending, destinations.Length));
        }

        HasAny = Recordings.Count > 0;
        Message = Recordings.Count == 0 && _loaded
            ? "No recordings yet.\nThey appear here as soon as one finishes — no need to refresh."
            : "";
    }

    /// <summary>
    /// For each recording, which enabled destinations do NOT yet have every one of
    /// its output files. That is exactly the set "send again" would do something
    /// about, so it is also what decides whether the button is offered at all.
    /// </summary>
    /// <remarks>
    /// Read straight from the transfer queue rather than asked of the host, for the
    /// same reason the list itself is: opening a page must not start a recording host.
    /// A queue that cannot be opened is not an error here — every recording simply
    /// looks un-sent, which errs towards offering the action rather than hiding it.
    /// </remarks>
    /// <summary>Opens the transfer queue, or null when it cannot be opened. A queue
    /// that will not open is not a reason to fail the recordings list.</summary>
    private static TransferQueue? TryOpenQueue()
    {
        try
        {
            return new TransferQueue();
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private Dictionary<string, string[]> FindOutstandingDestinations(
        IReadOnlyList<RecordingSummary> summaries, string[] destinations)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (destinations.Length == 0 || _queue is null)
        {
            return result;
        }

        // ONE query for the whole page. Asking per output file meant a query per
        // recording per refresh, which is invisible with three recordings and very
        // visible with three hundred.
        IReadOnlyDictionary<string, IReadOnlyList<string>> completed;
        try
        {
            completed = _queue.CompletedDestinationsByOutput();
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return result;
        }

        foreach (RecordingSummary summary in summaries)
        {
            IReadOnlyList<string> outputs = summary.OutputFiles ?? [];
            if (outputs.Count == 0)
            {
                continue;
            }

            var pending = new List<string>();
            foreach (string destination in destinations)
            {
                bool everywhere = outputs.All(file =>
                    completed.TryGetValue(Path.Combine(summary.Folder, file), out IReadOnlyList<string>? reached)
                    && reached.Contains(destination, StringComparer.OrdinalIgnoreCase));

                if (!everywhere)
                {
                    pending.Add(destination);
                }
            }

            result[summary.Folder] = [.. pending];
        }

        return result;
    }

    /// <summary>Starts watching the working folder so finished recordings appear on
    /// their own. Safe to call when already watching.</summary>
    public void StartWatching()
    {
        lock (_watcherGate)
        {
            if (_watcher is not null)
            {
                return;
            }

            string folder = _settings.Load().WorkingFolder;
            if (!Directory.Exists(folder))
            {
                return;
            }

            _watcher = new FileSystemWatcher(folder)
            {
                // Session folders appear and their journals change; watching both
                // catches "a recording started" and "a recording finished".
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.Changed += OnFolderChanged;
        }
    }

    /// <summary>Stops watching and releases the directory handle.</summary>
    public void StopWatching()
    {
        lock (_watcherGate)
        {
            if (_watcher is null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFolderChanged;
            _watcher.Deleted -= OnFolderChanged;
            _watcher.Renamed -= OnFolderChanged;
            _watcher.Changed -= OnFolderChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _pendingRescan?.Cancel();
    }

    /// <summary>Debounces the burst of events a finishing recording produces into a
    /// single rescan on the UI thread.</summary>
    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        CancellationTokenSource cts = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pendingRescan, cts);
        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RescanDelay, cts.Token).ConfigureAwait(false);
                Application.Current?.Dispatcher.BeginInvoke(() => _ = RefreshAsync());
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later change — the newer timer will do the rescan.
            }
        });
    }

    [RelayCommand]
    private static void OpenFolder(RecordingRow row) =>
        Process.Start(new ProcessStartInfo("explorer.exe", row.Folder) { UseShellExecute = true });

    [RelayCommand]
    private static void Play(RecordingRow row)
    {
        string? output = Directory.EnumerateFiles(row.Folder, "*.mkv")
            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("seg-", StringComparison.Ordinal));
        if (output is not null)
        {
            Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
        }
    }

    /// <summary>
    /// Queues this recording to any enabled destination that does not already have it
    /// (SPEC §9). Used when a destination was added, fixed, or reconfigured after the
    /// recording finished — without it, the only way to send an old recording to a new
    /// destination would be to copy it by hand.
    /// </summary>
    /// <remarks>
    /// The host skips destinations that already hold this recording, so pressing this
    /// twice cannot produce a second copy called "name (2)". The button is disabled
    /// when there is nothing outstanding, which is the other half of the same idea.
    /// </remarks>
    public async Task ResendAsync(RecordingRow row)
    {
        row.ActionResult = "Queueing…";
        row.ActionIsError = false;
        try
        {
            ResendResponse response = await _host.RequestAsync<ResendResponse>(
                IpcKinds.Resend, new ResendRequest(row.Folder), startHostIfNeeded: true, CancellationToken.None);
            row.ActionResult = response.Message;
            row.ActionIsError = response.Queued == 0;
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException)
        {
            row.ActionResult = exception.Message;
            row.ActionIsError = true;
        }
    }

    /// <summary>Executes a deletion AFTER the view has collected the typed
    /// confirmation (SPEC §7: explicit typed confirmation, always).</summary>
    public async Task DeleteConfirmedAsync(RecordingRow row)
    {
        await Task.Run(() => Directory.Delete(row.Folder, recursive: true));
        Recordings.Remove(row);
    }

    public void Dispose() => StopWatching();
}

/// <summary>One session row.</summary>
public sealed partial class RecordingRow : ObservableObject
{
    /// <summary>Outcome of the last verify or re-send on this row, shown inline so
    /// the answer appears next to the button that was pressed.</summary>
    [ObservableProperty]
    private string _actionResult = "";

    [ObservableProperty]
    private bool _actionIsError;

    [ObservableProperty]
    private string _duration = "";

    [ObservableProperty]
    private string _size = "";

    [ObservableProperty]
    private bool _isFinalized;

    /// <summary>True when at least one enabled destination is still missing this
    /// recording — which is precisely when "send again" would do something.</summary>
    [ObservableProperty]
    private bool _canResend;

    /// <summary>What "send again" would do, or why it is unavailable.</summary>
    [ObservableProperty]
    private string _resendHint = "";

    public RecordingRow(RecordingSummary summary, string[] outstandingDestinations, int enabledDestinationCount)
    {
        Folder = summary.Folder;
        Started = summary.StartedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);
        WithUpdatedFacts(summary, outstandingDestinations, enabledDestinationCount);
    }

    public string Folder { get; }

    public string Started { get; }

    /// <summary>The session folder's name — what the typed delete confirmation asks
    /// the user to retype.</summary>
    public string Name => Path.GetFileName(Folder);

    /// <summary>Refreshes the facts that change as a recording progresses, without
    /// replacing the row object (which would lose any inline result being shown).</summary>
    public RecordingRow WithUpdatedFacts(
        RecordingSummary summary, string[] outstandingDestinations, int enabledDestinationCount)
    {
        Duration = summary.RecordedSpan == TimeSpan.Zero
            ? "—"
            : summary.RecordedSpan.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        Size = Captr.Core.Common.ByteSize.Format(summary.TotalBytes);
        IsFinalized = summary.Finalized;

        CanResend = summary.Finalized && outstandingDestinations.Length > 0;
        ResendHint = DescribeResendState(summary.Finalized, outstandingDestinations, enabledDestinationCount);
        return this;
    }

    /// <summary>
    /// Why "send again" is offered — or, more usefully, why it is not. A disabled
    /// button with no explanation is the thing that makes people click it repeatedly
    /// and conclude the application is broken.
    /// </summary>
    private static string DescribeResendState(
        bool finalized, string[] outstanding, int enabledDestinationCount)
    {
        if (!finalized)
        {
            return "This recording has not been finalised yet, so there is nothing to send.";
        }

        if (enabledDestinationCount == 0)
        {
            return "No destinations are enabled. Add one in Settings, then send this recording to it.";
        }

        return outstanding.Length == 0
            ? "Every enabled destination already has this recording."
            : "Send to: " + string.Join(", ", outstanding);
    }
}
