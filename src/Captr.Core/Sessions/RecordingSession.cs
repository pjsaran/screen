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
/// rather than throwing (SPEC §12), and finalisation runs whatever happened before it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The shutdown block is created on the event window's thread when Windows asks, and released at the end of RunAsync; a session is not a disposable resource its callers hold.")]
public sealed class RecordingSession : IRecordingSession
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

    /// <summary>Undo actions for the event-window subscriptions, run when the session
    /// ends. The window lives as long as the host; a finished session left subscribed
    /// kept answering "wait for me" to every later shutdown.</summary>
    private readonly List<Action> _detachSystemEvents = [];

    /// <summary>Where displays come from: the real enumerator, or a test's list.</summary>
    private readonly Func<IReadOnlyList<DisplayInfo>> _enumerateDisplays;

    /// <summary>Set when a topology change left nothing to record: the next encoder
    /// run waits for a display to come back instead of relaunching a stale plan.</summary>
    private bool _waitingForDisplays;

    /// <summary>Why the session stopped ITSELF (a nearly full disk), for the outcome
    /// the host reports; null when a person, a schedule, or Windows stopped it.</summary>
    private string? _selfStopReason;

    private RecordingSession(
        SessionContext context, SessionJournal journal, DiskGuard diskGuard, ILogger log,
        Func<IReadOnlyList<DisplayInfo>>? enumerateDisplays = null)
    {
        _context = context;
        _journal = journal;
        _diskGuard = diskGuard;
        _enumerateDisplays = enumerateDisplays ?? (() => new DisplayEnumerator().Enumerate());
        _plan = context.InitialPlan;
        CurrentQuality = context.QualityName;
        CurrentSpeedPreset = context.SpeedPresetName;
        CurrentExcludedDisplayIds = context.ExcludedDisplayIds;
        _log = log.ForContext<RecordingSession>();
        _supervisor = new EncoderSupervisor(context.FfmpegPath, journal, log);
        _supervisor.SustainedSlowEncoding += () => Post(SessionCommand.ReduceFrameRate);
    }

    /// <summary>Current lifecycle state, for status queries and the heartbeat.</summary>
    public SessionState State => _state;

    /// <inheritdoc/>
    public string? FailureReason { get; private set; }

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
    public string CurrentQuality { get; private set; }

    /// <inheritdoc cref="CurrentFrameRate"/>
    public string CurrentSpeedPreset { get; private set; }

    /// <inheritdoc cref="CurrentFrameRate"/>
    public IReadOnlyList<string> CurrentExcludedDisplayIds { get; private set; }

    /// <summary>Everything a session needs at birth; produced by
    /// <see cref="SessionPlanner"/> which validates settings, resolves displays,
    /// selects the encoder, and preflights the disk BEFORE any state is created.</summary>
    /// <param name="CacheToInvalidateOnEarlyFailure">
    /// Set only when the encoder came from the cache instead of a fresh trial. If the
    /// recording then fails within its first seconds, the cached winner is the prime
    /// suspect (a driver can break without changing its version), so the cache is
    /// cleared and the next start re-probes properly. Null when the encoder was
    /// trialled during this start — there is nothing stale to blame.
    /// </param>
    public sealed record SessionContext(
        Guid SessionId,
        string WorkingFolder,
        string FfmpegPath,
        string FfprobePath,
        RecordingPlan InitialPlan,
        IReadOnlyList<string>? SoftwareFallbackArguments,
        long MeasuredBytesPerHour,
        IReadOnlyList<string> ExcludedDisplayIds,
        string QualityName,
        string SpeedPresetName,
        EncoderCache? CacheToInvalidateOnEarlyFailure = null,
        string? SoftwareFallbackCodec = null);

    /// <summary>Creates the session: journal, ballast, initial state. The caller
    /// (host) then invokes <see cref="RunAsync"/> exactly once.</summary>
    public static RecordingSession Create(
        SessionContext context, SessionStarted startEvent, ILogger log) =>
        Create(context, startEvent, log, enumerateDisplays: null);

    /// <param name="enumerateDisplays">Test seam: the displays a topology change or a
    /// settings change resolves against. Null means the machine's real displays.</param>
    internal static RecordingSession Create(
        SessionContext context, SessionStarted startEvent, ILogger log,
        Func<IReadOnlyList<DisplayInfo>>? enumerateDisplays)
    {
        SessionJournal journal = SessionJournal.CreateNew(context.WorkingFolder, startEvent);

        // Say up front what this machine will do to an unattended recording. Captr
        // holds the display awake for the whole session, but a screen saver or a
        // workstation lock runs off the input-idle timer, which no execution-state
        // request affects — and the inactivity lock is a security setting Captr has
        // no business overriding. Recorded at the START so that whoever reads the
        // journal after a gap already knows the answer, instead of inferring it.
        IdleLockPolicy idlePolicy = IdleLockPolicy.Read();
        if (idlePolicy.WillInterruptARecording)
        {
            journal.Append(new SessionNote
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Text = idlePolicy.Describe(),
            });
            log.Warning("Unattended recording will be interrupted: {Policy}", idlePolicy.Describe());
        }

        var diskGuard = new DiskGuard(context.WorkingFolder, context.MeasuredBytesPerHour);
        diskGuard.ReserveBallast();
        return new RecordingSession(context, journal, diskGuard, log, enumerateDisplays);
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
    public void AttachSystemEvents(SystemEventWindow events)
    {
        Action suspend = () => Post(SessionCommand.Suspend);
        Action resumed = () => Post(SessionCommand.ResumeFromSuspend);
        Action displayChanged = () => Post(SessionCommand.TopologyChanged);
        Action timeChanged = () => Post(SessionCommand.ClockChanged);

        // Windows is shutting down or logging off. SPEC §6: register a shutdown
        // block reason (so Windows waits AND the user can see why), finalise
        // quickly, then release it — the block is disposed when RunAsync finishes.
        // This handler runs on the window's thread, which is where the reason must
        // be created.
        Action endSession = () =>
        {
            _shutdownBlock ??= new ShutdownBlock(events, "Captr is finishing the recording so no footage is lost.");
            Post(SessionCommand.Stop);
        };
        Action<SessionChangeKind> sessionChanged = kind => Post(kind switch
        {
            // Lock: record it, keep recording — never stop (SPEC §6).
            SessionChangeKind.SessionLock => SessionCommand.NoteLocked,
            SessionChangeKind.SessionUnlock => SessionCommand.NoteUnlocked,

            // DISCONNECT is when the desktop goes away: the console session was
            // handed over, or a remote client (RDP, Citrix, AWS WorkSpaces) closed
            // its window. CONNECT is the opposite — the desktop is back. These two
            // were once mapped the wrong way round, which made the journal of a
            // WorkSpaces session claim the desktop had RETURNED at the exact moment
            // it went away. Capture keeps trying throughout either way; the notes
            // exist so the journal explains the gap afterwards.
            SessionChangeKind.ConsoleDisconnect or SessionChangeKind.RemoteDisconnect =>
                SessionCommand.NoteConsoleLost,
            _ => SessionCommand.NoteConsoleReturned,
        });

        events.SuspendRequested += suspend;
        events.Resumed += resumed;
        events.DisplayChanged += displayChanged;
        events.TimeChanged += timeChanged;
        events.EndSessionRequested += endSession;
        events.SessionChanged += sessionChanged;
        _detachSystemEvents.Add(() =>
        {
            events.SuspendRequested -= suspend;
            events.Resumed -= resumed;
            events.DisplayChanged -= displayChanged;
            events.TimeChanged -= timeChanged;
            events.EndSessionRequested -= endSession;
            events.SessionChanged -= sessionChanged;
        });
    }

    /// <summary>
    /// Releases what Create took — the journal and the ballast — for a
    /// session that will never be run (tests that exercise the command handling
    /// directly). <see cref="RunAsync"/> releases them itself.
    /// </summary>
    internal void ReleaseWithoutRunning()
    {
        DetachSystemEvents();
        _diskGuard.ReleaseBallast();
        _journal.Dispose();
    }

    /// <summary>Stops listening to the event window. Idempotent.</summary>
    internal void DetachSystemEvents()
    {
        foreach (Action detach in _detachSystemEvents)
        {
            detach();
        }

        _detachSystemEvents.Clear();
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

        // Neither helper may take the recording down with it: a heartbeat file held
        // open by a virus scanner, or a transient listing failure, used to fault its
        // task silently — and the await on it below then threw past finalisation.
        Task trackerTask = KeepGoingAsync(() => tracker.RunAsync(trackerCts.Token), "segment tracker");
        Task heartbeatTask = HeartbeatLoopAsync(trackerCts.Token);

        string? failure = null;
        DateTimeOffset recordingStartedUtc = DateTimeOffset.UtcNow;
        try
        {
            _state = SessionState.Recording;
            bool running = true;
            while (running)
            {
                if (_waitingForDisplays && !await WaitForDisplaysAsync(sessionCts.Token).ConfigureAwait(false))
                {
                    break; // Stopped while there was nothing to record.
                }

                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                Task<SupervisionOutcome> runTask = _supervisor.RunAsync(BuildRunSpec(), null, runCts.Token);
                SessionCommand? interrupting;
                try
                {
                    interrupting = await PumpCommandsUntilRunEndsAsync(runTask, runCts).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The command pump failed (the journal became unwritable, a
                    // display query threw). The encoder must NOT be left running
                    // unsupervised: stop it, and finalise what exists.
                    _log.Error(exception, "The session's command loop failed; stopping the encoder and finalising");
                    failure = "Captr hit an internal error while recording and stopped: " + exception.Message;
                    await runCts.CancelAsync().ConfigureAwait(false);
                    interrupting = SessionCommand.Stop;
                }

                SupervisionOutcome outcome = await runTask.ConfigureAwait(false);

                if (outcome.Kind == SupervisionEndKind.FailedLoudly)
                {
                    failure = outcome.FailureReason;
                    ForgetCachedEncoderIfItFailedImmediately(recordingStartedUtc);
                    break;
                }

                if (failure is not null)
                {
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything between encoder runs (a rebuild, a pause, a resume). Whatever
            // it was, what has been recorded is finalised below, not abandoned.
            _log.Error(exception, "The session loop failed; finalising what was recorded");
            failure ??= "Captr hit an internal error while recording and stopped: " + exception.Message;
        }
        finally
        {
            _state = SessionState.Finalizing;
            DetachSystemEvents();
            await trackerCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(trackerTask, heartbeatTask).ConfigureAwait(false);
            executionState.Release();
            BestEffort(_diskGuard.ReleaseBallast, "release the disk reserve");
            BestEffort(_journal.Dispose, "close the journal");
        }

        try
        {
            FailureReason = failure ?? _selfStopReason;
            var pipeline = new FinalizationPipeline(_context.FfmpegPath, _context.FfprobePath, _log);
            FinalizationResult result = await pipeline.RunAsync(_context.WorkingFolder, CancellationToken.None).ConfigureAwait(false);
            failure ??= _selfStopReason;
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
            _shutdownBlock?.Dispose();
            _shutdownBlock = null;
        }
    }

    /// <summary>
    /// The arguments for the next encoder run, built from the CURRENT plan — displays,
    /// frame rate, quality, and arrangement group as they are now.
    /// </summary>
    /// <remarks>
    /// The software fallback used to be built once, at start, and reused for every
    /// run. After a topology change, a settings change, or an automatic frame-rate
    /// reduction, falling back therefore recorded the ORIGINAL displays (an excluded
    /// one included, or a stale output index) at the original rate, into segments
    /// named for arrangement group 1 — which finalisation then joined with a
    /// different canvas. And once the session had fallen back, the next run (after a
    /// pause, say) went straight back to the hardware encoder that had just failed.
    /// Now the fallback is rebuilt every run, and a session that has fallen back
    /// stays on the software encoder with no further rung, exactly as SPEC §6 says.
    /// </remarks>
    internal EncoderRunSpec BuildRunSpec()
    {
        RecordingPlan current = _plan with { ArrangementGroup = _arrangementGroup };
        IReadOnlyList<string> primary = FfmpegArgumentBuilder.Build(current);

        if (_context.SoftwareFallbackCodec is not { } codec)
        {
            // No software rung (the encoder IS software, or the build has none).
            return new EncoderRunSpec(primary, null, _context.WorkingFolder);
        }

        ArrangementPlan arrangement = ArrangementPlanner.Plan(current.Sources);
        IReadOnlyList<string> fallback = FfmpegArgumentBuilder.Build(current with
        {
            Encoder = new EncoderSettings(codec, QualityLevels.BuildEncoderArguments(
                codec, SpeedPresets.FindOrDefault(CurrentSpeedPreset), QualityLevels.FindOrDefault(CurrentQuality),
                arrangement.CanvasWidth, arrangement.CanvasHeight, current.FrameRate)),
        });

        return _supervisor.HasFallenBack
            ? new EncoderRunSpec(fallback, null, _context.WorkingFolder)
            : new EncoderRunSpec(primary, fallback, _context.WorkingFolder);
    }

    /// <summary>
    /// Every display went away: wait for one to come back (or for Stop) rather than
    /// relaunching an encoder at outputs that no longer exist. The wait is journaled
    /// as a gap, like any other time nothing was being recorded.
    /// </summary>
    /// <returns>False when the session was stopped while waiting.</returns>
    private async Task<bool> WaitForDisplaysAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset since = DateTimeOffset.UtcNow;
        await foreach (SessionCommand command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (command == SessionCommand.Stop)
            {
                _state = SessionState.Stopping;
                JournalGap(since, "no display to record");
                return false;
            }

            if (command == SessionCommand.TopologyChanged)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                DrainPending(SessionCommand.TopologyChanged);
                RebuildForNewTopology();
                if (!_waitingForDisplays)
                {
                    JournalGap(since, "no display to record");
                    return true;
                }

                continue;
            }

            HandleWhileIdle(command);
        }

        return false;
    }

    private void JournalGap(DateTimeOffset since, string reason) =>
        _journal.Append(new GapRecorded
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            GapStartUtc = since,
            Duration = DateTimeOffset.UtcNow - since,
            Reason = reason,
        });

    /// <summary>Runs a helper loop, logging (never propagating) anything it throws.</summary>
    private async Task KeepGoingAsync(Func<Task> loop, string what)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Error(exception, "The {What} stopped unexpectedly; the recording continues without it", what);
        }
    }

    private void BestEffort(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning(exception, "Could not {What}; finalisation continues", what);
        }
    }

    /// <summary>
    /// A command that arrives while nothing is encoding — paused, suspended, or
    /// waiting for a display. These used to be read and thrown away, so a display
    /// excluded while the recording was paused was recorded again after Resume, and
    /// the user had been told "saved and applied".
    /// </summary>
    /// <returns>True when the command was dealt with here.</returns>
    internal bool HandleWhileIdle(SessionCommand command)
    {
        switch (command)
        {
            case SessionCommand.ApplyDegradation:
                ApplyPendingDegradation();
                return true;
            case SessionCommand.TopologyChanged:
                DrainPending(SessionCommand.TopologyChanged);
                RebuildForNewTopology();
                return true;
            case SessionCommand.ReduceFrameRate:
                // Nothing is encoding, so nothing is falling behind.
                return true;
            case SessionCommand.ClockChanged or SessionCommand.NoteLocked or SessionCommand.NoteUnlocked
                or SessionCommand.NoteConsoleLost or SessionCommand.NoteConsoleReturned:
                HandleNote(command);
                return true;
            default:
                return false;
        }
    }

    private void HandleNote(SessionCommand command)
    {
        switch (command)
        {
            case SessionCommand.NoteLocked:
                Note("Workstation locked — recording continues.");
                break;
            case SessionCommand.NoteUnlocked:
                Note("Workstation unlocked.");
                break;
            case SessionCommand.NoteConsoleLost:
                Note("The desktop was disconnected (someone closed the remote session, or the console " +
                     "was handed over). Recording keeps running and resumes on its own when the desktop " +
                     "comes back; the time in between is recorded as a gap.");
                break;
            case SessionCommand.NoteConsoleReturned:
                Note("The desktop is back — capture resumes.");
                break;
            case SessionCommand.ClockChanged:
                _journal.Append(new ClockJumped { TimestampUtc = DateTimeOffset.UtcNow, ApparentJump = TimeSpan.Zero });
                break;
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
                case SessionCommand.NoteLocked or SessionCommand.NoteUnlocked or SessionCommand.NoteConsoleLost
                    or SessionCommand.NoteConsoleReturned or SessionCommand.ClockChanged:
                    HandleNote(command);
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

    internal async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
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

            HandleWhileIdle(command);
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

            HandleWhileIdle(command);
        }
    }

    internal void RebuildForNewTopology()
    {
        // Re-resolve stable identities to fresh indices (SPEC §5/§6) against the
        // exclusions AS THEY ARE NOW. This used the set from session start, so a
        // display the user excluded mid-recording came back the next time any
        // monitor was plugged in, switched on, or changed resolution. A display
        // attached mid-session is included by default, exactly as at start.
        IReadOnlyList<DisplayInfo> displays = _enumerateDisplays();
        ResolvedSelection selection = DisplaySelection.Resolve(displays, CurrentExcludedDisplayIds, []);
        List<DisplayInfo> stillWanted = [.. selection.Included];

        if (stillWanted.Count == 0)
        {
            if (!_waitingForDisplays)
            {
                Note("Every display is gone or deselected; recording waits for a display to come back.");
            }

            _waitingForDisplays = true;
            return;
        }

        _waitingForDisplays = false;
        _arrangementGroup++;
        _plan = _plan with
        {
            Sources = [.. stillWanted.Select(d =>
                new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height, d.VirtualX, d.VirtualY))],
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
    /// How soon a failure counts as "the encoder never worked" rather than "something
    /// went wrong later". A cached encoder that fails inside this window is assumed to
    /// be the cause, so the cache is cleared and the next start re-probes from scratch.
    /// </summary>
    private static readonly TimeSpan EarlyFailureWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clears the encoder cache when a session that trusted it died almost at once.
    /// This is the safety net that lets a cache hit skip the confirmation trial and
    /// start recording immediately (see <see cref="Encoders.EncoderSelector"/>): the
    /// recording itself becomes the trial, and a bad cache costs one failed start
    /// rather than a wrong encoder for hours.
    /// </summary>
    private void ForgetCachedEncoderIfItFailedImmediately(DateTimeOffset recordingStartedUtc)
    {
        if (_context.CacheToInvalidateOnEarlyFailure is not { } cache)
        {
            return; // The encoder was trialled during this start; the cache is not to blame.
        }

        TimeSpan ranFor = DateTimeOffset.UtcNow - recordingStartedUtc;
        if (ranFor > EarlyFailureWindow)
        {
            return;
        }

        _log.Warning(
            "The recording failed after only {Seconds:F0}s using the cached encoder {Encoder}; " +
            "clearing the encoder cache so the next start re-probes every candidate",
            ranFor.TotalSeconds, _plan.Encoder.CodecName);
        cache.Invalidate();
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
        IReadOnlyList<DisplayInfo> attached = _enumerateDisplays();
        ResolvedSelection selection = DisplaySelection.Resolve(attached, degraded.ExcludedDisplayIds, []);
        IReadOnlyList<CaptureSource> sources = selection.Included.Count > 0
            ? [.. selection.Included.Select(d =>
                new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height, d.VirtualX, d.VirtualY))]
            : _plan.Sources; // Never leave a session with nothing to capture.

        // Quality and speed: rebuild the encoder's arguments for the SAME encoder at
        // the new settings — the proven encoder stays proven.
        ArrangementPlan arrangement = ArrangementPlanner.Plan(sources);
        QualityLevel quality = QualityLevels.FindOrDefault(degraded.Quality);
        SpeedPreset speed = SpeedPresets.FindOrDefault(degraded.SpeedPreset);
        var encoder = new EncoderSettings(
            _plan.Encoder.CodecName,
            QualityLevels.BuildEncoderArguments(
                _plan.Encoder.CodecName, speed, quality,
                arrangement.CanvasWidth, arrangement.CanvasHeight, degraded.FrameRate));

        _plan = _plan with { FrameRate = degraded.FrameRate, Sources = sources, Encoder = encoder };
        CurrentQuality = quality.Name;
        CurrentSpeedPreset = speed.Name;
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
             $"quality '{quality.Name}', preset '{speed.Name}'. Continuing under arrangement group {_arrangementGroup}.");
        _log.Information(
            "Applied a degrading settings change: {Displays} display(s), {Fps} fps, quality {Quality}, preset {Preset}; group {Group}",
            sources.Count, degraded.FrameRate, quality.Name, speed.Name, _arrangementGroup);
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
        long freeBytes;
        try
        {
            freeBytes = _diskGuard.FreeBytesOnVolume();
        }
        catch (IOException exception)
        {
            // An unmeasurable volume (a share that stopped answering) is not a reason
            // to stop recording; the next check tries again.
            _log.Warning("Free disk space could not be measured: {Reason}", exception.Message);
            return true;
        }

        DiskVerdict verdict = _diskGuard.Check(freeBytes, _diskGuard.RecordedBytes());
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
                _selfStopReason =
                    "The disk holding the working folder was nearly full, so Captr stopped the recording cleanly " +
                    "rather than let it fail. Everything up to that point is saved. Free some space, or choose a " +
                    "working folder on a bigger drive in Settings.";
                return false;
            default:
                return true;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        bool warned = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                new HeartbeatSnapshot
                {
                    SessionId = _context.SessionId,
                    WrittenUtc = DateTimeOffset.UtcNow,
                    State = _state.ToString(),
                    HostProcessId = Environment.ProcessId,
                    EncodedTime = _supervisor.LatestProgress?.OutTime,
                }.Write(_context.WorkingFolder);
                warned = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A missed heartbeat costs nothing: the next second writes another.
                // Letting it escape used to fault this task and, at Stop, abandon
                // finalisation altogether.
                if (!warned)
                {
                    _log.Warning("The heartbeat could not be written (it is retried every second): {Reason}", exception.Message);
                    warned = true;
                }
            }

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

    internal enum SessionCommand
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
