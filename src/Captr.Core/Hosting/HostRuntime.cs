using Captr.Core.Ipc;
using Captr.Core.Secrets;
using Captr.Core.Settings;
using Captr.Core.WindowsEvents;

using Serilog;

namespace Captr.Core.Hosting;

/// <summary>
/// The headless host's lifecycle: single instance, logging, recovery scan, IPC
/// server, idle exit. Owns "nothing runs when nothing is recorded" (SPEC §1/§4):
/// the host exists only while there is work, and exits itself a few minutes after
/// the last activity.
/// </summary>
public sealed class HostRuntime
{
    /// <summary>How long the host lingers after its last activity before exiting.
    /// Long enough that back-to-back scheduled recordings reuse one host; short
    /// enough that "nothing running" is true within minutes (SPEC §4).</summary>
    public static readonly TimeSpan IdleExitDelay = TimeSpan.FromMinutes(2);

    /// <summary>How long a new host waits for a previous one in the same Windows
    /// session to finish its idle exit.</summary>
    private static readonly TimeSpan SingleInstanceWait = TimeSpan.FromSeconds(30);

    /// <summary>Exit code when the pipe name is already owned elsewhere — only ever
    /// seen in the host log; the CLI reports the host as unreachable.</summary>
    private const int ExitPipeNameTaken = 4;

    private readonly string _hostVersion;

    public HostRuntime(string hostVersion) => _hostVersion = hostVersion;

    /// <summary>Runs the host until idle. Returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // One host per Windows session. The mutex is the cheap, session-local half of
        // that rule; the pipe below is the authoritative, machine-wide half.
        //
        // A host that finds the mutex taken WAITS for it rather than leaving at once:
        // the usual reason is a previous host in the middle of its idle exit, and a
        // client that spawned this one because the old one had stopped answering
        // would otherwise poll for a minute and report "did not begin answering".
        using var singleInstance = new Mutex(
            initiallyOwned: false, @"Local\CaptrHost" + (Common.CaptrPaths.InstanceSuffix is { } suffix ? "-" + suffix : ""));
        bool acquired;
        try
        {
            acquired = singleInstance.WaitOne(SingleInstanceWait);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // The previous host died holding it; the mutex is ours now.
        }

        ILogger log = CreateLogger();
        if (!acquired)
        {
            log.Warning(
                "Another Captr recorder in this Windows session still held the single-instance lock after {Wait}; " +
                "this one is not needed and is exiting",
                SingleInstanceWait);
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
            return 0;
        }

        log.Information("Recording host starting (version {Version}, pid {Pid})", _hostVersion, Environment.ProcessId);

        try
        {
            var settingsStore = new SettingsStore();
            using var systemEvents = new SystemEventWindow();
            var transferQueue = new Transfers.TransferQueue();
            using var transferWorker = new Transfers.TransferWorker(transferQueue, settingsStore, log);
            var service = new HostService(settingsStore, systemEvents, log, transferQueue, transferWorker);

            // Claim the pipe FIRST — before recovery touches a single session folder.
            //
            // If this user already has a recorder in another Windows session (a
            // scheduled task, a second remote desktop session), that host owns the
            // name and this one must not run: its recovery scan would treat the other
            // host's live recording as a crash, adopt its encoder, and kill it. And if
            // someone else created the name, serving under their security descriptor
            // would hand them the recorder (see IpcServer.Start).
            //
            // Opening the pipe before recovery also means a client never waits in
            // silence while a long crashed session is finalised: status answers at
            // once, and a start request queues behind recovery instead of timing out.
            service.HoldStartsUntilRecoveryCompletes();
            await using var ipcServer = new IpcServer(service, _hostVersion, log);
            try
            {
                ipcServer.Start();
            }
            catch (PipeNameTakenException exception)
            {
                log.Error("{Reason}", exception.Message);
                return ExitPipeNameTaken;
            }

            log.Information("Host ready; listening on the user pipe");

            // SPEC §6: recover interrupted sessions BEFORE starting new work.
            await service.RunStartupRecoveryAsync(cancellationToken).ConfigureAwait(false);

            // SPEC §7: reclaim disk from sessions that are transferred, verified, and
            // past their retention period. Host start is the natural moment — the
            // machine is idle and nothing is recording yet.
            new Transfers.RetentionCleaner(transferQueue, settingsStore, log).Clean(DateTimeOffset.UtcNow);

            // And then the history of those now-deleted recordings. Order matters:
            // the cleaner decides what to delete by asking the queue what completed,
            // so pruning first would make it keep everything for ever. See
            // TransferQueue.Prune.
            int pruned = transferQueue.Prune(DateTimeOffset.UtcNow);
            if (pruned > 0)
            {
                log.Information(
                    "Removed {Count} transfer records older than {Days} days whose recordings are gone",
                    pruned, Transfers.TransferQueue.HistoryWindow.TotalDays);
            }

            // Transfers that stopped on a bad credential are excluded from the due
            // list, so nothing would ever pick them up again on its own. A host start
            // is the other natural moment (besides saving settings) at which the
            // credential may have been fixed since. Re-arming costs one attempt, and
            // one that is still wrong simply pauses again.
            transferQueue.ResumeAuthPaused();

            using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task transferTask = transferWorker.RunAsync(transferCts.Token);

            // Idle loop: exit when nothing has happened for a while and no session
            // is active (SPEC §4: exits after a short idle period).
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (!service.IsBusy && DateTimeOffset.UtcNow - service.LastActivityUtc > IdleExitDelay)
                {
                    log.Information("Host idle for {Idle} — exiting (nothing runs when nothing records)", IdleExitDelay);
                    await transferCts.CancelAsync().ConfigureAwait(false);
                    await transferTask.ConfigureAwait(false);
                    return 0;
                }
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            // SPEC §12: no unhandled exception may reach the host process edge.
            log.Fatal(exception, "Host terminated by an unexpected error");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Rolling file log under local app data, bounded retention (SPEC §12).
    /// The redaction policy is registered here, at construction, once WP9 lands the
    /// secrets module — never at call sites.</summary>
    private static ILogger CreateLogger()
    {
        string logFolder = Common.CaptrPaths.Logs;
        Directory.CreateDirectory(logFolder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            // The redaction policy lives at logger construction (SPEC §7/§12) —
            // any secret-suggestive property is masked no matter who logs it.
            .WithSecretRedaction()
            .Enrich.WithProperty("ProcessRole", "host")
            // Capped as well as rotated: a new file each day or every 20 MB (a busy
            // day rolls on to host-<date>_001.log), and only the newest 14 files are
            // kept — 280 MB at the very most. The library's own default is a 1 GB cap
            // per file, which a supervisor restart loop logging at Debug could reach.
            .WriteTo.File(
                Path.Combine(logFolder, "host-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 20L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return Log.Logger;
    }
}
