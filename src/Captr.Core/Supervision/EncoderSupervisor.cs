using Captr.Core.Sessions;

using Serilog;

namespace Captr.Core.Supervision;

/// <summary>
/// The async loop that runs one encoding session: launch FFmpeg, tail its progress
/// and log files, and act on <see cref="SupervisorPolicy"/> verdicts — restart on
/// stall or crash into a new segment, take the single hardware→software fallback,
/// or stop loudly (SPEC §6). Owns the encoder for the session's lifetime; every
/// decision it executes is journaled. If it fails, recording stops silently — the
/// one thing SPEC §13 forbids — so its own loop is wrapped in catch-record-retry
/// (SPEC §12 explicitly blesses that here).
/// </summary>
public sealed class EncoderSupervisor
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _ffmpegPath;
    private readonly SessionJournal _journal;
    private readonly ILogger _log;
    private readonly SupervisorPolicy _policy = new();
    private readonly LogRingBuffer _logTail = new();

    private readonly Lock _progressGate = new();
    private readonly List<string> _progressBlock = [];
    private EncoderProgress? _latestProgress;
    private bool _slowWarningRaised;

    public EncoderSupervisor(string ffmpegPath, SessionJournal journal, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _journal = journal;
        _log = log.ForContext<EncoderSupervisor>();
    }

    /// <summary>Latest progress snapshot, for status queries and the heartbeat.</summary>
    public EncoderProgress? LatestProgress => _latestProgress;

    /// <summary>Bounded tail of the encoder's log, for diagnostics.</summary>
    public IReadOnlyList<string> LogTailSnapshot => _logTail.Snapshot();

    /// <summary>Raised when encoding has been slower than real time for the whole
    /// window — the session engine responds by lowering the frame rate (SPEC §6).</summary>
    public event Action? SustainedSlowEncoding;

    /// <summary>
    /// Runs the encoder until <paramref name="stopToken"/> requests a graceful stop
    /// or the policy says to stop loudly. Returns how the run ended.
    /// </summary>
    /// <param name="spec">Primary and (optional) software-fallback argument vectors.</param>
    /// <param name="adoptedProcess">A re-adopted orphan to supervise instead of
    /// launching, after a host crash (SPEC §4); null for a fresh launch.</param>
    /// <param name="stopToken">Signalled to end the recording gracefully.</param>
    /// <param name="reportLogLevel">FFREPORT level — production default 24 (warning);
    /// the flood test raises it deliberately.</param>
    public async Task<SupervisionOutcome> RunAsync(
        EncoderRunSpec spec,
        FfmpegProcess? adoptedProcess,
        CancellationToken stopToken,
        int reportLogLevel = 24)
    {
        IReadOnlyList<string> currentArguments = spec.PrimaryArguments;
        FfmpegProcess? process = adoptedProcess;
        var pendingGap = new PendingGap();
        var captureOutage = new CaptureOutage();

        string progressPath = Path.Combine(
            spec.WorkingFolder, Captr.Core.Encoders.EncodingConstants.ProgressFileName);
        string reportPath = Path.Combine(spec.WorkingFolder, FfmpegProcess.ReportFileName);

        using var progressTailCts = new CancellationTokenSource();
        var progressReader = new FileTailReader(progressPath, OnProgressLine);
        var reportReader = new FileTailReader(reportPath, _logTail.Add);
        Task progressTail = progressReader.TailAsync(0, progressTailCts.Token);
        Task reportTail = reportReader.TailAsync(0, progressTailCts.Token);

        try
        {
            while (true)
            {
                // Bookmark the log BEFORE this process starts writing to it, so its
                // exit is judged on its OWN output (see LogRingBuffer.SnapshotSince).
                long processLogMark = _logTail.Mark();
                if (process is null)
                {
                    // Hand the readers a clean slate for the incoming process. Both
                    // steps matter: deleting means the only bytes that can ever be
                    // read from offset 0 belong to the NEW process, and Restart means
                    // the rewind happens whatever the file lengths look like. See
                    // FileTailReader.Restart for the failure this prevents.
                    DeleteIfPresent(reportPath);
                    DeleteIfPresent(progressPath);
                    reportReader.Restart();
                    progressReader.Restart();

                    process = LaunchAndJournal(currentArguments, spec.WorkingFolder, reportLogLevel);
                }
                else
                {
                    _policy.OnProcessLaunched(DateTimeOffset.UtcNow);
                }

                (bool stopRequested, bool killedForStall) = await MonitorUntilExitAsync(
                    process, spec.WorkingFolder, pendingGap, captureOutage, stopToken)
                    .ConfigureAwait(false);

                int exitCode = SafeExitCode(process);
                process.Dispose();
                process = null;

                if (stopRequested)
                {
                    return new SupervisionOutcome(SupervisionEndKind.StoppedGracefully, null, _logTail.Snapshot());
                }

                // The encoder is down and we did not ask for it — classify and decide,
                // reading only the lines THIS process wrote.
                IReadOnlyList<string> processLog = _logTail.SnapshotSince(processLogMark);
                ExitKind exitKind = SupervisorPolicy.ClassifyExit(
                    stopWasRequested: false, killedForStall, exitCode, processLog);
                pendingGap.StartUtc ??= DateTimeOffset.UtcNow;

                // The desktop went away (UAC secure desktop, session switch, RDP).
                // Wait before trying again instead of hammering DXGI, and do NOT
                // count it toward the fallback — a different encoder cannot conjure
                // a desktop (SPEC §6).
                if (exitKind == ExitKind.CaptureAccessLost)
                {
                    TimeSpan delay = _policy.NextCaptureRetryDelay();
                    pendingGap.Reason = "capture unavailable";
                    _log.Warning(
                        "Capture access lost (secure desktop, session lock, or remote-session disconnect); retrying in {Delay}",
                        delay);

                    // Journal the START of the outage, not every retry. A disconnected
                    // remote session can last for hours; one note per retry would bury
                    // the journal in thousands of identical lines and say nothing the
                    // closing gap does not already say.
                    if (captureOutage.SinceUtc is null)
                    {
                        captureOutage.SinceUtc = DateTimeOffset.UtcNow;
                        _journal.Append(new SessionNote
                        {
                            TimestampUtc = captureOutage.SinceUtc.Value,
                            Text = "There is nothing to capture right now — the desktop is unavailable " +
                                   "(locked, showing the secure desktop, or the remote session is " +
                                   "disconnected). Recording is still running and picks up by itself the " +
                                   "moment the desktop returns; the time in between is recorded as a gap.",
                        });
                    }

                    try
                    {
                        await Task.Delay(delay, stopToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return new SupervisionOutcome(SupervisionEndKind.StoppedGracefully, null, _logTail.Snapshot());
                    }

                    continue;
                }

                bool countsTowardFallback = exitKind == ExitKind.Fault;
                _journal.Append(new EncoderRestarted
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Reason = killedForStall ? "stall detected" : $"unexpected exit (code {exitCode}, {exitKind})",
                    CountsTowardFallback = countsTowardFallback,
                    LogTail = processLog,
                });
                _log.Warning(
                    "Encoder exited unexpectedly (code {ExitCode}, classified {ExitKind}); restarting into a new segment",
                    exitCode, exitKind);

                if (countsTowardFallback)
                {
                    switch (_policy.OnFault(DateTimeOffset.UtcNow))
                    {
                        case FaultVerdict.FallBackToSoftware when spec.FallbackArguments is not null:
                            _journal.Append(new EncoderFellBack
                            {
                                TimestampUtc = DateTimeOffset.UtcNow,
                                FromEncoder = DescribeEncoder(currentArguments),
                                ToEncoder = DescribeEncoder(spec.FallbackArguments),
                            });
                            _log.Warning(
                                "Falling back from {From} to {To} after repeated faults — this happens at most once",
                                DescribeEncoder(currentArguments), DescribeEncoder(spec.FallbackArguments));
                            currentArguments = spec.FallbackArguments;
                            break;

                        case FaultVerdict.FallBackToSoftware:
                        // No fallback encoder available: repeated faults must stop loudly.
                        case FaultVerdict.StopLoudly:
                            string reason = DescribeStopLoudlyReason(currentArguments, spec.FallbackArguments);
                            _log.Error("Stopping loudly: {Reason}", reason);
                            return new SupervisionOutcome(SupervisionEndKind.FailedLoudly, reason, _logTail.Snapshot());
                    }
                }

                // Loop continues: relaunch with (possibly fallback) arguments.
            }
        }
        finally
        {
            // A hole that never closed: the run ended while capture was still down,
            // so no later frame ever arrived to close it. Journal it now, running to
            // this moment.
            //
            // This is not tidiness. Coverage is computed from journaled gaps
            // (CoverageCalculator), so an unclosed gap makes Captr report a stretch
            // it captured nothing during as fully recorded — presenting a recording
            // as continuous when it is not, which is the one thing SPEC §13 forbids
            // most explicitly. Before capture-loss handling actually worked this path
            // was unreachable; now that a session can genuinely sit waiting for a
            // desktop that never returns, it is not.
            if (pendingGap.StartUtc is { } unclosedGapStart)
            {
                DateTimeOffset endedUtc = DateTimeOffset.UtcNow;
                _journal.Append(new GapRecorded
                {
                    TimestampUtc = endedUtc,
                    GapStartUtc = unclosedGapStart,
                    Duration = endedUtc - unclosedGapStart,
                    Reason = pendingGap.Reason,
                });
                _log.Warning("The run ended with capture still down; a {Seconds:F0}s gap ({Reason}) is journaled",
                    (endedUtc - unclosedGapStart).TotalSeconds, pendingGap.Reason);
                pendingGap.Clear();
            }

            process?.Dispose();
            await progressTailCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(progressTail, reportTail).ConfigureAwait(false);
        }
    }

    /// <summary>Removes a file the encoder is about to rewrite. Best effort: if it
    /// cannot be deleted the reader's shrink detection is still there, and losing a
    /// diagnostic file must never stop a recording.</summary>
    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private FfmpegProcess LaunchAndJournal(IReadOnlyList<string> arguments, string workingFolder, int reportLogLevel)
    {
        FfmpegProcess process = FfmpegProcess.Launch(_ffmpegPath, arguments, workingFolder, reportLogLevel);
        _policy.OnProcessLaunched(DateTimeOffset.UtcNow);
        _journal.Append(new EncoderProcessLaunched
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ProcessId = process.ProcessId,
            ProcessStartTimeUtc = process.StartTimeUtc,
            ImagePath = process.ImagePath,
        });
        _log.Information("Encoder launched: pid {Pid}, {Encoder}", process.ProcessId, DescribeEncoder(arguments));
        return process;
    }

    /// <summary>
    /// Watches one running process: progress freshness, file growth, sustained-slow
    /// warnings, gap closure, stop requests. Returns when the process has exited
    /// (for any reason) or was killed here for a stall.
    /// </summary>
    private async Task<(bool StopRequested, bool KilledForStall)> MonitorUntilExitAsync(
        FfmpegProcess process,
        string workingFolder,
        PendingGap pendingGap,
        CaptureOutage captureOutage,
        CancellationToken stopToken)
    {
        while (true)
        {
            if (stopToken.IsCancellationRequested)
            {
                _journal.Append(new StopRequested { TimestampUtc = DateTimeOffset.UtcNow, Reason = "stop requested" });
                bool graceful = await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _log.Information("Encoder stopped ({How})", graceful ? "gracefully" : "killed after grace period");
                return (StopRequested: true, KilledForStall: false);
            }

            if (process.HasExited)
            {
                return (StopRequested: false, KilledForStall: false);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Feed the policy the freshest observations — but ONLY ones this process
            // actually produced. Right after a relaunch _latestProgress still holds
            // the DEAD process's final snapshot, and handing that to the policy does
            // two harmful things: it sets the stall clock to a moment already in the
            // past, so a fresh, healthy encoder is killed for "stalling" the instant
            // the previous progress is older than the stall timeout — which a
            // capture-loss backoff guarantees — and it announces that frames are
            // flowing again before a single new frame exists, pinning the backoff at
            // one second forever. The same staleness once produced negative gap
            // durations (coverage over 100%, caught by the chaos suite).
            if (_latestProgress is { } progress && progress.ObservedUtc > _policy.LaunchedUtc)
            {
                _policy.OnProgress(progress);
                _policy.OnCaptureRecovered(); // frames are flowing — the desktop is back
                captureOutage.SinceUtc = null;

                // A pending gap (from a previous restart) closes at the first progress
                // of the new process — journal its honest duration, and say WHY it
                // happened: "the desktop was gone" and "the encoder crashed" ask
                // completely different things of whoever reads this later.
                if (pendingGap.StartUtc is { } gapStart && progress.ObservedUtc > gapStart)
                {
                    _journal.Append(new GapRecorded
                    {
                        TimestampUtc = now,
                        GapStartUtc = gapStart,
                        Duration = progress.ObservedUtc - gapStart,
                        Reason = pendingGap.Reason,
                    });
                    _log.Information("Capture resumed after {Seconds:F0}s ({Reason})",
                        (progress.ObservedUtc - gapStart).TotalSeconds, pendingGap.Reason);
                    pendingGap.Clear();
                }
            }

            _policy.OnFileLength(NewestSegmentLength(workingFolder), now);

            if (_policy.DetectStall(now) is { } stallReason)
            {
                _log.Warning("Encoder stalled: {Reason}. Killing and restarting into a new segment", stallReason);
                pendingGap.StartUtc = _latestProgress?.ObservedUtc ?? _policy.LaunchedUtc;
                pendingGap.Reason = "encoder restart";
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return (StopRequested: false, KilledForStall: true);
            }

            if (_policy.IsSustainedSlow(now) && !_slowWarningRaised)
            {
                _slowWarningRaised = true;
                _log.Warning("Encoding has been slower than real time for {Window}s — frames are being dropped",
                    SupervisionConstants.SlowSpeedWindow.TotalSeconds);
                SustainedSlowEncoding?.Invoke();
            }

            try
            {
                await Task.Delay(MonitorInterval, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Loop once more; the stop branch at the top handles it.
            }
        }
    }

    private void OnProgressLine(string line)
    {
        lock (_progressGate)
        {
            _progressBlock.Add(line);
            if (line.StartsWith("progress=", StringComparison.Ordinal))
            {
                _latestProgress = EncoderProgress.Parse(_progressBlock, DateTimeOffset.UtcNow);
                _progressBlock.Clear();
            }
        }
    }

    private static long NewestSegmentLength(string workingFolder)
    {
        try
        {
            // Newest by NAME (they embed a timestamp): LastWriteTime is unreliable
            // for a file another process holds open. So is FileInfo.Length — NTFS
            // directory metadata lags for open files — hence the explicit open with
            // full sharing to read the TRUE current length.
            string? newest = Directory.EnumerateFiles(workingFolder, "seg-*.mkv")
                .OrderDescending(StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null)
            {
                return -1;
            }

            using var stream = new FileStream(
                newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static int SafeExitCode(FfmpegProcess process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return int.MinValue;
        }
    }

    /// <summary>
    /// The sentence shown when the session gives up (SPEC §6: stop and report
    /// loudly). It names the encoder that failed and whether anything was left to
    /// try, because "no software fallback is available" reads as a packaging mistake
    /// when the truth is that the software encoder IS the one that just failed.
    /// </summary>
    private string DescribeStopLoudlyReason(
        IReadOnlyList<string> currentArguments, IReadOnlyList<string>? fallbackArguments)
    {
        string encoder = DescribeEncoder(currentArguments);
        if (_policy.HasFallenBack)
        {
            return $"The software encoder ({encoder}) also failed repeatedly after the fallback. " +
                   "Recording cannot continue reliably.";
        }

        if (fallbackArguments is null && Captr.Core.Encoders.EncoderCatalog.IsSoftware(encoder))
        {
            return $"The software encoder ({encoder}) failed repeatedly and there is no simpler encoder " +
                   "left to fall back to. Recording cannot continue reliably.";
        }

        return $"The encoder ({encoder}) failed repeatedly and this FFmpeg build carries no software " +
               "encoder to fall back to.";
    }

    /// <summary>
    /// A hole in the recording that has started but not yet closed. It closes on the
    /// first frame of the next process — the only moment its honest duration is known.
    /// </summary>
    private sealed class PendingGap
    {
        public DateTimeOffset? StartUtc { get; set; }

        /// <summary>Why the hole exists, in words a reader of the journal can act on.</summary>
        public string Reason { get; set; } = "encoder restart";

        public void Clear()
        {
            StartUtc = null;
            Reason = "encoder restart";
        }
    }

    /// <summary>When the desktop went away, or null while it is there. Held in an
    /// object because it is written from inside the monitor loop and read by the
    /// restart loop, and a <c>ref</c> local cannot cross an <c>await</c>.</summary>
    private sealed class CaptureOutage
    {
        public DateTimeOffset? SinceUtc { get; set; }
    }

    /// <summary>"-c:v hevc_nvenc" → "hevc_nvenc", for logs and the journal.</summary>
    private static string DescribeEncoder(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "-c:v")
            {
                return arguments[i + 1];
            }
        }

        return "unknown";
    }
}

/// <summary>What the supervisor runs: the primary argument vector and, when one
/// exists, the software-fallback vector (SPEC §6: fall back once).</summary>
public sealed record EncoderRunSpec(
    IReadOnlyList<string> PrimaryArguments,
    IReadOnlyList<string>? FallbackArguments,
    string WorkingFolder);

/// <summary>How a supervised run ended.</summary>
public sealed record SupervisionOutcome(
    SupervisionEndKind Kind,
    string? FailureReason,
    IReadOnlyList<string> LogTail);

/// <summary>Terminal states of a supervised run. There is deliberately no silent
/// variant (SPEC §13: recording never stops silently).</summary>
public enum SupervisionEndKind
{
    StoppedGracefully,
    FailedLoudly,
}
