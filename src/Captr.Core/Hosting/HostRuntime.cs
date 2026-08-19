using Captr.Core.Ipc;
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

    private readonly string _hostVersion;

    public HostRuntime(string hostVersion) => _hostVersion = hostVersion;

    /// <summary>Runs the host until idle. Returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // Single instance per user: a second host would fight over the pipe.
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\CaptrHost", out bool isFirst);
        if (!isFirst)
        {
            return 0; // A host is already serving this user — nothing to do.
        }

        ILogger log = CreateLogger();
        log.Information("Recording host starting (version {Version}, pid {Pid})", _hostVersion, Environment.ProcessId);

        try
        {
            var settingsStore = new SettingsStore();
            using var systemEvents = new MessageOnlyWindow();
            var service = new HostService(settingsStore, systemEvents, log);

            // SPEC §6: recover interrupted sessions BEFORE accepting new work.
            await service.RunRecoveryScanAsync(cancellationToken).ConfigureAwait(false);

            await using var ipcServer = new IpcServer(service, _hostVersion, log);
            ipcServer.Start();
            log.Information("Host ready; listening on the user pipe");

            // Idle loop: exit when nothing has happened for a while and no session
            // is active (SPEC §4: exits after a short idle period).
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (!service.IsBusy && DateTimeOffset.UtcNow - service.LastActivityUtc > IdleExitDelay)
                {
                    log.Information("Host idle for {Idle} — exiting (nothing runs when nothing records)", IdleExitDelay);
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
        string logFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr", "logs");
        Directory.CreateDirectory(logFolder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("ProcessRole", "host")
            .WriteTo.File(
                Path.Combine(logFolder, "host-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return Log.Logger;
    }
}
