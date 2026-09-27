using System.Diagnostics;

namespace Captr.Core.Supervision;

/// <summary>
/// One running FFmpeg encoder process: launch, graceful stop, kill, and the launch
/// details later needed for re-adoption. Owns the process handle and the rules
/// SPEC §4 sets for it: started with no console window, NOT placed in a job object
/// (it must outlive a host crash), stdin piped solely for the graceful-stop
/// <c>'q'</c>, and all output going to files (see the folder README for why pipes
/// are forbidden here).
/// </summary>
public sealed class FfmpegProcess : IDisposable
{
    /// <summary>Name of FFmpeg's own log file in the working folder, produced via
    /// the FFREPORT environment variable.</summary>
    public const string ReportFileName = "ffmpeg-report.log";

    private readonly Process _process;
    private readonly bool _adopted;
    private readonly string _imagePath;

    private FfmpegProcess(Process process, bool adopted, string imagePath)
    {
        _process = process;
        _adopted = adopted;
        _imagePath = imagePath;
    }

    /// <summary>Process id, for the journal's re-adoption record.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Process start time (UTC) — with the PID, defeats PID reuse.</summary>
    public DateTimeOffset StartTimeUtc => new(_process.StartTime.ToUniversalTime());

    /// <summary>Full image path, third leg of the re-adoption match. The path this
    /// process was launched from, not a query of the running process: that query
    /// throws if FFmpeg has already exited (a bad argument ends it at once), which
    /// escaped before the launch was journaled and left an unrecorded orphan.</summary>
    public string ImagePath => _imagePath;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    /// <summary>Launches FFmpeg with the given argument vector.</summary>
    /// <param name="ffmpegPath">Path of the bundled ffmpeg.exe.</param>
    /// <param name="arguments">The complete argument vector.</param>
    /// <param name="workingFolder">Session folder receiving the report log.</param>
    /// <param name="reportLogLevel">FFREPORT log level — 24 (warning) in production;
    /// tests raise it to flood the log deliberately (SPEC §14 flood test).</param>
    public static FfmpegProcess Launch(
        string ffmpegPath, IReadOnlyList<string> arguments, string workingFolder, int reportLogLevel = 24)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            // No console window, or one flashes on every probe (SPEC §4).
            CreateNoWindow = true,
            // stdin stays piped ONLY for the graceful-stop 'q'. stdout/stderr are
            // NOT piped: progress goes to a file via -progress, the log goes to a
            // file via FFREPORT, and an unread pipe would block the encoder the
            // moment its buffer filled after a host crash.
            RedirectStandardInput = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // FFREPORT makes ffmpeg write its own log file — our diagnostics source.
        // Level 24 = warnings and errors. The path goes through ffmpeg's option
        // parser, so backslashes and the drive colon must be escaped.
        string reportPath = Path.Combine(workingFolder, ReportFileName);
        string escapedReportPath = reportPath.Replace("\\", "/").Replace(":", "\\:");
        startInfo.Environment["FFREPORT"] = $"file={escapedReportPath}:level={reportLogLevel}";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"FFmpeg failed to start from {ffmpegPath}.");

        return new FfmpegProcess(process, adopted: false, Path.GetFullPath(ffmpegPath));
    }

    /// <summary>Wraps an already-running process found by
    /// <see cref="ProcessAdoption"/>. An adopted process has no stdin pipe to send
    /// 'q' on; it is asked to stop with Ctrl+C instead — see <see cref="StopAsync"/>.</summary>
    public static FfmpegProcess Adopt(Process process, string imagePath) => new(process, adopted: true, imagePath);

    /// <summary>
    /// Stops the encoder: asks politely, waits the grace period so the current segment
    /// closes cleanly, then kills. A process this host launched is asked with
    /// <c>'q'</c> on its stdin; an ADOPTED one (its stdin belonged to a dead host) is
    /// asked with Ctrl+C on its console (<see cref="ConsoleSignal"/>). Only if that is
    /// impossible, or ignored, is it killed — and the segment in progress is then
    /// repaired by finalisation, the path a crash exercises anyway (SPEC §6).
    /// </summary>
    /// <returns>True when the process ended gracefully, false when it was killed.</returns>
    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            return true;
        }

        if (_adopted)
        {
            ConsoleSignal.TrySendCtrlC(_process.Id);
        }
        else
        {
            try
            {
                await _process.StandardInput.WriteAsync('q').ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The pipe is already broken — fall through to the wait-and-kill.
            }
            catch (InvalidOperationException)
            {
            }
        }

        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceCts.CancelAfter(SupervisionConstants.GracefulStopTimeout);
        try
        {
            await _process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill();
            return false;
        }
    }

    /// <summary>Terminates immediately. Used for stalls, where waiting is the
    /// problem being solved.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);

                // Termination is asynchronous. Returning at once let finalisation or
                // the next launch start while the dying process still held the last
                // segment open.
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Exited between the check and the kill (the desired outcome), or already
            // being torn down by Windows.
        }
    }

    /// <summary>Waits for natural exit (used after a stop request).</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    /// <summary>Releases the process HANDLE only — never kills. Disposing the
    /// supervisor must leave the encoder writing (SPEC §4).</summary>
    public void Dispose() => _process.Dispose();
}
