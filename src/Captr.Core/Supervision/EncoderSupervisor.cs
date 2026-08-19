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
        DateTimeOffset? pendingGapStartUtc = null;

        using var progressTailCts = new CancellationTokenSource();
        Task progressTail = new FileTailReader(
                Path.Combine(spec.WorkingFolder, Captr.Core.Encoders.EncodingConstants.ProgressFileName),
                OnProgressLine)
            .TailAsync(0, progressTailCts.Token);
        Task reportTail = new FileTailReader(
                Path.Combine(spec.WorkingFolder, FfmpegProcess.ReportFileName),
                _logTail.Add)
            .TailAsync(0, progressTailCts.Token);

        try
        {
            while (true)
            {
                if (process is null)
                {
                    process = LaunchAndJournal(currentArguments, spec.WorkingFolder, reportLogLevel);
                }
                else
                {
                    _policy.OnProcessLaunched(DateTimeOffset.UtcNow);
                }

                (bool stopRequested, bool killedForStall) = await MonitorUntilExitAsync(
                    process, spec.WorkingFolder, () => pendingGapStartUtc, start => pendingGapStartUtc = start, stopToken)
                    .ConfigureAwait(false);

                int exitCode = SafeExitCode(process);
                process.Dispose();
                process = null;

                if (stopRequested)
                {
                    return new SupervisionOutcome(SupervisionEndKind.StoppedGracefully, null, _logTail.Snapshot());
                }

                // The encoder is down and we did not ask for it — classify and decide.
                ExitKind exitKind = SupervisorPolicy.ClassifyExit(
                    stopWasRequested: false, killedForStall, exitCode, _logTail.Snapshot());
                pendingGapStartUtc ??= DateTimeOffset.UtcNow;

                bool countsTowardFallback = exitKind == ExitKind.Fault;
                _journal.Append(new EncoderRestarted
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Reason = killedForStall ? "stall detected" : $"unexpected exit (code {exitCode}, {exitKind})",
                    CountsTowardFallback = countsTowardFallback,
                    LogTail = _logTail.Snapshot(),
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
                            string reason = _policy.HasFallenBack
                                ? "The software encoder also failed repeatedly. Recording cannot continue reliably."
                                : "The encoder failed repeatedly and no software fallback is available.";
                            _log.Error("Stopping loudly: {Reason}", reason);
                            return new SupervisionOutcome(SupervisionEndKind.FailedLoudly, reason, _logTail.Snapshot());
                    }
                }

                // Loop continues: relaunch with (possibly fallback) arguments.
            }
        }
        finally
        {
            process?.Dispose();
            await progressTailCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(progressTail, reportTail).ConfigureAwait(false);
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
        Func<DateTimeOffset?> getPendingGapStart,
        Action<DateTimeOffset?> setPendingGapStart,
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

            // Feed the policy the freshest observations.
            if (_latestProgress is { } progress)
            {
                _policy.OnProgress(progress);

                // A pending gap (from a previous restart) closes at the first
                // progress of the new process — journal its honest duration.
                // The observation must POST-DATE the gap: right after a relaunch,
                // _latestProgress still holds the dead process's final snapshot,
                // and closing against it once produced negative gap durations
                // (coverage over 100% — caught by the chaos suite).
                if (getPendingGapStart() is { } gapStart && progress.ObservedUtc > gapStart)
                {
                    _journal.Append(new GapRecorded
                    {
                        TimestampUtc = now,
                        GapStartUtc = gapStart,
                        Duration = progress.ObservedUtc - gapStart,
                        Reason = "encoder restart",
                    });
                    setPendingGapStart(null);
                }
            }

            _policy.OnFileLength(NewestSegmentLength(workingFolder), now);

            if (_policy.DetectStall(now) is { } stallReason)
            {
                _log.Warning("Encoder stalled: {Reason}. Killing and restarting into a new segment", stallReason);
                setPendingGapStart(_latestProgress?.ObservedUtc ?? _policy.LaunchedUtc);
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
