using System.Threading.Channels;

using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Supervision;
using Captr.Core.WindowsEvents;

using Serilog;

namespace Captr.Core.Sessions;

/// <summary>
/// One recording session from start to finalised output — the keystone that wires
/// display selection, encoder selection, the supervisor, the disk guard, the
/// heartbeat, segment tracking, and Windows-event reactions together. Owns all
/// session state (immutably where possible) and the command loop that serialises
/// every state change: pause, resume, stop, suspend, topology change, frame-rate
/// degradation. If it fails, the recording stops — so its loop records-and-continues
/// rather than throwing (SPEC §12).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The shutdown block is created and released entirely within RunAsync's lifetime; a session is not a disposable resource its callers hold.")]
public sealed class RecordingSession
{
    private readonly SessionContext _context;
    private readonly SessionJournal _journal;
    private readonly EncoderSupervisor _supervisor;
    private readonly DiskGuard _diskGuard;
    private readonly ILogger _log;

    private readonly Channel<SessionCommand> _commands = Channel.CreateUnbounded<SessionCommand>();
    private RecordingPlan _plan;
    private int _arrangementGroup = 1;
    private volatile SessionState _state = SessionState.Starting;

    /// <summary>Settings handed to <see cref="ApplyDegradation"/>, consumed by the
    /// command loop so the change lands between encoder runs rather than under one.</summary>
    private Settings.CaptrSettings? _pendingDegradation;

    /// <summary>Held only while Windows is waiting for us to finish (SPEC §6).</summary>
    private ShutdownBlock? _shutdownBlock;

    /// <summary>The event window whose ShutdownBlocked flag we must clear once
    /// finalisation is done, so Windows can carry on shutting down.</summary>
    private MessageOnlyWindow? _shutdownWindow;

    private RecordingSession(SessionContext context, SessionJournal journal, DiskGuard diskGuard, ILogger log)
    {
        _context = context;
        _journal = journal;
        _diskGuard = diskGuard;
        _plan = context.InitialPlan;
        CurrentQualityPreset = context.QualityPresetName;
        CurrentExcludedDisplayIds = context.ExcludedDisplayIds;
        _log = log.ForContext<RecordingSession>();
        _supervisor = new EncoderSupervisor(context.FfmpegPath, journal, log);
        _supervisor.SustainedSlowEncoding += () => Post(SessionCommand.ReduceFrameRate);
    }

    /// <summary>Current lifecycle state, for status queries and the heartbeat.</summary>
    public SessionState State => _state;

    /// <summary>Latest encoder progress, for status queries.</summary>
    public EncoderProgress? LatestProgress => _supervisor.LatestProgress;

    /// <summary>The session's identity and immutable start facts.</summary>
    public SessionContext Context => _context;

    /// <summary>
    /// What the session is ACTUALLY running at right now — which can be below the
    /// saved settings, because sustained slow encoding lowers the frame rate by
    /// itself. The settings lock (SPEC §8) compares against these, not against the
    /// settings file: otherwise, after an automatic reduction from 15 to 10 fps, a
    /// user setting 12 would look like a "decrease" and quietly raise the load.
    /// </summary>
    public int CurrentFrameRate => _plan.FrameRate;

    /// <inheritdoc cref="CurrentFrameRate"/>
    public string CurrentQualityPreset { get; private set; }

    /// <inheritdoc cref="CurrentFrameRate"/>
    public IReadOnlyList<string> CurrentExcludedDisplayIds { get; private set; }

    /// <summary>Everything a session needs at birth; produced by
    /// <see cref="SessionPlanner"/> which validates settings, resolves displays,
    /// selects the encoder, and preflights the disk BEFORE any state is created.</summary>
    public sealed record SessionContext(
        Guid SessionId,
        string WorkingFolder,
        string FfmpegPath,
        string FfprobePath,
        RecordingPlan InitialPlan,
        IReadOnlyList<string>? SoftwareFallbackArguments,
        long MeasuredBytesPerHour,
        IReadOnlyList<string> ExcludedDisplayIds,
        string QualityPresetName);

    /// <summary>Creates the session: journal, ballast, initial state. The caller
    /// (host) then invokes <see cref="RunAsync"/> exactly once.</summary>
    public static RecordingSession Create(
        SessionContext context, SessionStarted startEvent, ILogger log)
    {
        SessionJournal journal = SessionJournal.CreateNew(context.WorkingFolder, startEvent);
        var diskGuard = new DiskGuard(context.WorkingFolder, context.MeasuredBytesPerHour);
        diskGuard.ReserveBallast();
        return new RecordingSession(context, journal, diskGuard, log);
    }

    // ---- Commands (safe from any thread; the loop serialises them) --------------

    public void RequestStop() => Post(SessionCommand.Stop);

    /// <summary>
    /// Applies a permitted degrading settings change to the LIVE session (SPEC §8):
    /// a lower frame rate, a lower quality preset, or fewer displays. The change
    /// rolls a new segment and is journaled, because none of these are join-
    /// compatible with what came before. Validation belongs to
    /// <see cref="Settings.SettingsChangePolicy"/>; by the time this is called the
    /// change is already known to be a degradation.
    /// </summary>
    public void ApplyDegradation(Settings.CaptrSettings degraded)
    {
        _pendingDegradation = degraded;
        Post(SessionCommand.ApplyDegradation);
    }

    public void RequestPause() => Post(SessionCommand.Pause);

    public void RequestResume() => Post(SessionCommand.Resume);

    /// <summary>Wires the Windows event window's signals into session commands.</summary>
    public void AttachSystemEvents(MessageOnlyWindow events)
    {
        events.SuspendRequested += () => Post(SessionCommand.Suspend);
        events.Resumed += () => Post(SessionCommand.ResumeFromSuspend);
        events.DisplayChanged += () => Post(SessionCommand.TopologyChanged);
        events.TimeChanged += () => Post(SessionCommand.ClockChanged);

        // Windows is shutting down or logging off. SPEC §6: register a shutdown
        // block reason (so Windows waits AND the user can see why), finalise
        // quickly, then release it — the block is disposed when RunAsync finishes.
        events.EndSessionRequested += () =>
        {
            if (_shutdownBlock is null)
            {
                _shutdownBlock = new ShutdownBlock(
                    events.Handle, "Captr is finishing the recording so no footage is lost.");
                events.ShutdownBlocked = true;
                _shutdownWindow = events;
            }

            Post(SessionCommand.Stop);
        };
        events.SessionChanged += kind => Post(kind switch
        {
            // Lock: record it, keep recording — never stop (SPEC §6).
            SessionChangeKind.SessionLock => SessionCommand.NoteLocked,
            SessionChangeKind.SessionUnlock => SessionCommand.NoteUnlocked,
            // RDP/console transitions: capture may fail; the supervisor's stall
            // detection restarts, and honest gaps accumulate until it returns.
            SessionChangeKind.ConsoleDisconnect or SessionChangeKind.RemoteConnect => SessionCommand.NoteConsoleLost,
            _ => SessionCommand.NoteConsoleReturned,
        });
    }

    private void Post(SessionCommand command) => _commands.Writer.TryWrite(command);

    // ---- The session loop -------------------------------------------------------

    /// <summary>Runs the session to completion and finalises. Returns the
    /// finalisation result (recovery-equivalent output).</summary>
    public async Task<FinalizationResult> RunAsync(CancellationToken hostShutdown)
    {
        using var executionState = new ExecutionStateHolder();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(hostShutdown);

        var tracker = new SegmentTracker(_context.WorkingFolder, _journal, () => _arrangementGroup);
        using var trackerCts = new CancellationTokenSource();
        Task trackerTask = tracker.RunAsync(trackerCts.Token);
        Task heartbeatTask = HeartbeatLoopAsync(trackerCts.Token);

        string? failure = null;
        try
        {
            _state = SessionState.Recording;
            bool running = true;
            while (running)
            {
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                var spec = new EncoderRunSpec(
                    FfmpegArgumentBuilder.Build(_plan with { ArrangementGroup = _arrangementGroup }),
                    _context.SoftwareFallbackArguments,
                    _context.WorkingFolder);

                Task<SupervisionOutcome> runTask = _supervisor.RunAsync(spec, null, runCts.Token);
                SessionCommand? interrupting = await PumpCommandsUntilRunEndsAsync(runTask, runCts).ConfigureAwait(false);
                SupervisionOutcome outcome = await runTask.ConfigureAwait(false);

                if (outcome.Kind == SupervisionEndKind.FailedLoudly)
                {
                    failure = outcome.FailureReason;
                    break;
                }

                switch (interrupting)
                {
                    case SessionCommand.Stop or null:
                        running = false;
                        break;

                    case SessionCommand.Pause:
                        await WaitWhilePausedAsync(sessionCts.Token).ConfigureAwait(false);
                        if (_state == SessionState.Stopping)
                        {
                            running = false;
                        }

                        break;

                    case SessionCommand.Suspend:
                        await WaitForResumeFromSuspendAsync(sessionCts.Token).ConfigureAwait(false);
                        break;

                    case SessionCommand.TopologyChanged:
                        // Debounce the burst Windows sends, then rebuild against the
                        // re-resolved topology under a NEW arrangement group.
                        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                        DrainPending(SessionCommand.TopologyChanged);
                        RebuildForNewTopology();
                        break;

                    case SessionCommand.ReduceFrameRate:
                        ReduceFrameRate();
                        break;

                    case SessionCommand.ApplyDegradation:
                        ApplyPendingDegradation();
                        break;
                }
            }
        }
        finally
        {
            _state = SessionState.Finalizing;
            await trackerCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(trackerTask, heartbeatTask).ConfigureAwait(false);
            executionState.Release();
            _diskGuard.ReleaseBallast();
            _journal.Dispose();
        }

        try
        {
            var pipeline = new FinalizationPipeline(_context.FfmpegPath, _context.FfprobePath, _log);
            FinalizationResult result = await pipeline.RunAsync(_context.WorkingFolder, CancellationToken.None).ConfigureAwait(false);
            _state = failure is null ? SessionState.Completed : SessionState.Failed;

            if (failure is not null)
            {
                _log.Error("Session {SessionId} ended in failure: {Failure}. Footage up to the failure is finalised.",
                    _context.SessionId, failure);
            }

            return result;
        }
        finally
        {
            // Release the shutdown block only NOW — after finalisation. Windows has
            // been waiting on it (WM_QUERYENDSESSION answered FALSE), which is the
            // whole point: the recording is safely closed before the machine goes
            // down (SPEC §6). Order matters: clear the window flag first so a repeat
            // query is answered TRUE, then destroy the reason.
            if (_shutdownWindow is not null)
            {
                _shutdownWindow.ShutdownBlocked = false;
                _shutdownWindow = null;
            }

            _shutdownBlock?.Dispose();
            _shutdownBlock = null;
        }
    }

    /// <summary>Consumes commands while a supervisor run is active. Notes and disk
    /// checks are handled inline; a command that requires the encoder to stop
    /// cancels the run and is returned to the main loop.</summary>
    private async Task<SessionCommand?> PumpCommandsUntilRunEndsAsync(
        Task<SupervisionOutcome> runTask, CancellationTokenSource runCts)
    {
        var diskCheckInterval = TimeSpan.FromSeconds(15);
        DateTimeOffset nextDiskCheck = DateTimeOffset.UtcNow + diskCheckInterval;

        while (!runTask.IsCompleted)
        {
            Task<bool> readTask = _commands.Reader.WaitToReadAsync(CancellationToken.None).AsTask();

            // VSTHRD003 warns about awaiting a task created elsewhere because of
            // JoinableTaskFactory deadlocks — a concern for VS extensions, not this
            // context-free host (no synchronisation context is ever captured here).
#pragma warning disable VSTHRD003
            Task completed = await Task.WhenAny(runTask, readTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
#pragma warning restore VSTHRD003

            if (DateTimeOffset.UtcNow >= nextDiskCheck)
            {
                nextDiskCheck = DateTimeOffset.UtcNow + diskCheckInterval;
                if (!CheckDisk())
                {
                    _state = SessionState.Stopping;
                    await runCts.CancelAsync().ConfigureAwait(false);
                    return SessionCommand.Stop;
                }
            }

            if (completed != readTask || !_commands.Reader.TryRead(out SessionCommand command))
            {
                continue;
            }

            switch (command)
            {
                case SessionCommand.NoteLocked:
                    Note("Workstation locked — recording continues.");
                    break;
                case SessionCommand.NoteUnlocked:
                    Note("Workstation unlocked.");
                    break;
                case SessionCommand.NoteConsoleLost:
                    Note("Console session lost (remote desktop or disconnect) — capture will gap until it returns.");
                    break;
                case SessionCommand.NoteConsoleReturned:
                    Note("Console session returned.");
                    break;
                case SessionCommand.ClockChanged:
                    _journal.Append(new ClockJumped { TimestampUtc = DateTimeOffset.UtcNow, ApparentJump = TimeSpan.Zero });
                    break;
                case SessionCommand.Resume:
                    break; // Not paused — nothing to resume.
                default:
                    // Stop, Pause, Suspend, TopologyChanged, ReduceFrameRate, and
                    // ApplyDegradation all need the encoder stopped first.
                    ApplyPreCancelState(command);
                    await runCts.CancelAsync().ConfigureAwait(false);
                    return command;
            }
        }

        return null;
    }

    private void ApplyPreCancelState(SessionCommand command) =>
        _state = command switch
        {
            SessionCommand.Stop => SessionState.Stopping,
            SessionCommand.Pause => SessionState.Paused,
            SessionCommand.Suspend => SessionState.Suspended,
            _ => _state,
        };

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        _journal.Append(new PauseStarted { TimestampUtc = DateTimeOffset.UtcNow });
        _log.Information("Recording paused — the pause is journaled and will show as a paused gap");

        await foreach (SessionCommand command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (command == SessionCommand.Resume)
            {
                _journal.Append(new PauseEnded { TimestampUtc = DateTimeOffset.UtcNow });
                _state = SessionState.Recording;
                _log.Information("Recording resumed into a new segment");
                return;
            }

            if (command == SessionCommand.Stop)
            {
                _journal.Append(new PauseEnded { TimestampUtc = DateTimeOffset.UtcNow });
                _state = SessionState.Stopping;
                return;
            }
        }
    }

    private async Task WaitForResumeFromSuspendAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset suspendedAt = DateTimeOffset.UtcNow;
        Note("System suspending — segment closed and journal flushed.");

        await foreach (SessionCommand command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (command is SessionCommand.ResumeFromSuspend or SessionCommand.Stop)
            {
                _journal.Append(new GapRecorded
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    GapStartUtc = suspendedAt,
                    Duration = DateTimeOffset.UtcNow - suspendedAt,
                    Reason = "system suspend",
                });
                if (command == SessionCommand.Stop)
                {
                    _state = SessionState.Stopping;
                }
                else
                {
                    _state = SessionState.Recording;
                    _log.Information("Resumed from suspend into a new segment; gap of {Gap} journaled",
                        DateTimeOffset.UtcNow - suspendedAt);
                }

                return;
            }
        }
    }

    private void RebuildForNewTopology()
    {
        // Re-resolve stable identities to fresh indices (SPEC §5/§6): the exclusion
        // set from session start still applies; a display attached mid-session is
        // included by default, exactly as at start.
        IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();
        ResolvedSelection selection = DisplaySelection.Resolve(displays, _context.ExcludedDisplayIds, []);
        List<DisplayInfo> stillWanted = [.. selection.Included];

        if (stillWanted.Count == 0)
        {
            Note("All displays lost after topology change; waiting for the next change.");
            return;
        }

        _arrangementGroup++;
        _plan = _plan with
        {
            Sources = [.. stillWanted.Select(d => new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height))],
        };
        _journal.Append(new TopologyChanged
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            NewDisplays =
            [
                .. stillWanted.Select(d => new RecordedDisplay
                {
                    StableId = d.StableId,
                    WindowsDisplayNumber = d.WindowsDisplayNumber,
                    Width = d.Width,
                    Height = d.Height,
                }),
            ],
            NewArrangementGroup = _arrangementGroup,
        });
        _log.Information("Display topology changed; continuing under arrangement group {Group}", _arrangementGroup);
    }

    /// <summary>
    /// Rebuilds the plan from a user's degrading settings change: fewer frames per
    /// second, a softer quality preset, or fewer displays. Each of these makes the
    /// following segments join-incompatible with the preceding ones, so the change
    /// opens a NEW arrangement group and is journaled (SPEC §8).
    /// </summary>
    private void ApplyPendingDegradation()
    {
        if (_pendingDegradation is not { } degraded)
        {
            return;
        }

        _pendingDegradation = null;
        int oldFps = _plan.FrameRate;
        _arrangementGroup++;

        // Displays: re-resolve against the NEW exclusion set. Removing a display
        // changes the canvas, which is why this rolls a group.
        IReadOnlyList<DisplayInfo> attached = new DisplayEnumerator().Enumerate();
        ResolvedSelection selection = DisplaySelection.Resolve(attached, degraded.ExcludedDisplayIds, []);
        IReadOnlyList<CaptureSource> sources = selection.Included.Count > 0
            ? [.. selection.Included.Select(d => new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height))]
            : _plan.Sources; // Never leave a session with nothing to capture.

        // Quality: rebuild the encoder's arguments for the SAME encoder at the new
        // preset — the proven encoder stays proven.
        ArrangementPlan arrangement = ArrangementPlanner.Plan(sources);
        QualityPreset preset = QualityPresets.Find(degraded.QualityPreset)
            ?? QualityPresets.Find(QualityPresets.DefaultName)!;
        var encoder = new EncoderSettings(
            _plan.Encoder.CodecName,
            QualityPresets.BuildQualityArguments(
                _plan.Encoder.CodecName, preset, degraded.QualityOverride,
                arrangement.CanvasWidth, arrangement.CanvasHeight, degraded.FrameRate));

        _plan = _plan with { FrameRate = degraded.FrameRate, Sources = sources, Encoder = encoder };
        CurrentQualityPreset = preset.Name;
        CurrentExcludedDisplayIds = degraded.ExcludedDisplayIds;

        if (degraded.FrameRate != oldFps)
        {
            _journal.Append(new FrameRateReduced
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                FromFps = oldFps,
                ToFps = degraded.FrameRate,
                Reason = "user lowered the frame rate while recording",
            });
        }

        Note($"Settings degraded while recording: {sources.Count} display(s), {degraded.FrameRate} fps, " +
             $"quality '{preset.Name}'. Continuing under arrangement group {_arrangementGroup}.");
        _log.Information(
            "Applied a degrading settings change: {Displays} display(s), {Fps} fps, quality {Preset}; group {Group}",
            sources.Count, degraded.FrameRate, preset.Name, _arrangementGroup);
    }

    private void ReduceFrameRate()
    {
        int oldFps = _plan.FrameRate;
        int newFps = Math.Max(5, oldFps * 2 / 3);
        if (newFps == oldFps)
        {
            return;
        }

        // A frame-rate change makes segments join-incompatible with their
        // predecessors, so it also opens a new arrangement group.
        _arrangementGroup++;
        _plan = _plan with { FrameRate = newFps };
        _journal.Append(new FrameRateReduced
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            FromFps = oldFps,
            ToFps = newFps,
            Reason = "sustained encoding slower than real time",
        });
        _log.Warning("Frame rate reduced {Old} → {New} fps to keep up; new arrangement group {Group}",
            oldFps, newFps, _arrangementGroup);
    }

    private bool CheckDisk()
    {
        DiskVerdict verdict = _diskGuard.Check(_diskGuard.FreeBytesOnVolume());
        switch (verdict.State)
        {
            case DiskState.Warning:
                _log.Warning("Disk space low: about {Minutes:F0} minutes of recording remaining",
                    verdict.RecordingTimeRemaining.TotalMinutes);
                return true;
            case DiskState.Critical:
                _diskGuard.ReleaseBallast();
                Note($"Disk critically low ({verdict.RecordingTimeRemaining.TotalMinutes:F0} minutes remaining) — stopping cleanly. A clean stop beats a disk-full crash.");
                _log.Error("Disk critically low — stopping the recording cleanly");
                return false;
            default:
                return true;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            new HeartbeatSnapshot
            {
                SessionId = _context.SessionId,
                WrittenUtc = DateTimeOffset.UtcNow,
                State = _state.ToString(),
                HostProcessId = Environment.ProcessId,
                EncodedTime = _supervisor.LatestProgress?.OutTime,
            }.Write(_context.WorkingFolder);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Note(string text) =>
        _journal.Append(new SessionNote { TimestampUtc = DateTimeOffset.UtcNow, Text = text });

    private void DrainPending(SessionCommand kind)
    {
        while (_commands.Reader.TryPeek(out SessionCommand next) && next == kind)
        {
            _commands.Reader.TryRead(out _);
        }
    }

    private enum SessionCommand
    {
        Stop,
        Pause,
        Resume,
        Suspend,
        ResumeFromSuspend,
        TopologyChanged,
        ReduceFrameRate,
        ApplyDegradation,
        ClockChanged,
        NoteLocked,
        NoteUnlocked,
        NoteConsoleLost,
        NoteConsoleReturned,
    }
}

/// <summary>Session lifecycle states surfaced to the UI/CLI.</summary>
public enum SessionState
{
    Starting,
    Recording,
    Paused,
    Suspended,
    Stopping,
    Finalizing,
    Completed,
    Failed,
}
