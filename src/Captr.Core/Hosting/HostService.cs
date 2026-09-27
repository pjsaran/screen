using Captr.Core.Ipc;
using Captr.Core.Naming;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Supervision;
using Captr.Core.Transfers;
using Captr.Core.WindowsEvents;
using Serilog;

namespace Captr.Core.Hosting;

/// <summary>
/// The host's implementation of every IPC operation, and the owner of the single
/// active <see cref="RecordingSession"/>. Owns the idempotency rules SPEC §10
/// demands of scheduled use: starting while recording and stopping while idle both
/// succeed, reporting the actual state — schedulers fire twice more often than
/// anyone expects.
/// </summary>
public sealed class HostService : IHostOperations
{
    private readonly SettingsStore _settingsStore;
    private readonly MessageOnlyWindow? _systemEvents;
    private readonly TransferQueue _transferQueue;
    private readonly TransferWorker? _transferWorker;
    private readonly ILogger _log;
    private readonly Func<CaptrSettings, StartRequest, CancellationToken, Task<IRecordingSession>> _createSession;
    private readonly Lock _gate = new();

    // The newest session, and the task that runs it to the end of finalisation.
    private IRecordingSession? _session;
    private Task? _sessionRun;
    private bool _stopRequested;

    // Older sessions still stopping or finalising after a newer one started. They
    // keep the host busy and are reported by status only when nothing newer is.
    private readonly List<(IRecordingSession Session, Task Run)> _finishing = [];

    // The one start being planned right now; concurrent starts wait for it.
    private Task<StartResponse>? _startInFlight;

    private SessionOutcome? _lastOutcome;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;

    // Completed unless the host is running its startup recovery scan.
    private TaskCompletionSource _startupRecovery = CompletedGate();

    public HostService(
        SettingsStore settingsStore,
        MessageOnlyWindow? systemEvents,
        ILogger log,
        TransferQueue? transferQueue = null,
        TransferWorker? transferWorker = null)
        : this(settingsStore, systemEvents, log, transferQueue, transferWorker, sessionFactory: null)
    {
    }

    /// <param name="sessionFactory">Test seam: plans and creates a session. Production
    /// passes null, which plans with <see cref="SessionPlanner"/> and creates a real
    /// <see cref="RecordingSession"/>.</param>
    internal HostService(
        SettingsStore settingsStore,
        MessageOnlyWindow? systemEvents,
        ILogger log,
        TransferQueue? transferQueue,
        TransferWorker? transferWorker,
        Func<CaptrSettings, StartRequest, CancellationToken, Task<IRecordingSession>>? sessionFactory)
    {
        _settingsStore = settingsStore;
        _systemEvents = systemEvents;
        _log = log.ForContext<HostService>();
        _transferQueue = transferQueue ?? new TransferQueue();
        _transferWorker = transferWorker;
        _createSession = sessionFactory ?? PlanAndCreateSessionAsync;
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private async Task<IRecordingSession> PlanAndCreateSessionAsync(
        CaptrSettings settings, StartRequest request, CancellationToken cancellationToken)
    {
        var planner = new SessionPlanner(_log);
        (RecordingSession.SessionContext context, SessionStarted startEvent) = await planner.PlanAsync(
            settings, request.FrameRate, request.Quality, request.SpeedPreset, request.Label, cancellationToken)
            .ConfigureAwait(false);
        return RecordingSession.Create(context, startEvent, _log);
    }

    /// <summary>When the host last did anything — feeds the idle-exit timer.</summary>
    public DateTimeOffset LastActivityUtc => _lastActivityUtc;

    /// <summary>True while a session is running/finalising or transfers are still
    /// draining — either keeps the host from its idle exit.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                if (_sessionRun is { IsCompleted: false } || _finishing.Count > 0 || _startInFlight is { IsCompleted: false })
                {
                    return true;
                }
            }

            return _transferWorker?.HasPendingWork == true;
        }
    }

    /// <summary>
    /// Starts a recording, or reports the one already running (SPEC §10: starting
    /// while recording is not an error).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One start at a time.</b> Planning takes seconds — displays are resolved and,
    /// on a first run, an encoder is proven by a trial encode — and a scheduled task,
    /// a hotkey, the tray, and the CLI can all ask within that window. The "already
    /// recording?" check and the moment the new session became visible used to sit
    /// under two separate locks with the plan in between, so two requests could both
    /// pass the check: two encoders recorded the same screen, and the first session
    /// was overwritten in the host's bookkeeping, leaving it running where no Stop
    /// could reach it. Now the first request plans and every concurrent one waits
    /// for it and is told about the same session.
    /// </para>
    /// <para>
    /// <b>A finishing recording never blocks the next one.</b> A session that has been
    /// asked to stop (or has failed) can spend minutes joining and hashing its
    /// segments. Starting during that time used to answer "already recording" and
    /// record nothing, so a stop task and a start task scheduled for the same minute
    /// lost the whole second block. The finishing session now carries on in the
    /// background while the new one records.
    /// </para>
    /// </remarks>
    public async Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        Touch();

        // SPEC §6: interrupted sessions are recovered before new work begins.
        await _startupRecovery.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        Task<StartResponse> inFlight;
        bool joined;
        lock (_gate)
        {
            if (_session is not null && _sessionRun is { IsCompleted: false } && !IsFinishing(_session))
            {
                return AlreadyRecording(_session);
            }

            joined = _startInFlight is { IsCompleted: false };
            if (!joined)
            {
                _startInFlight = StartNewSessionAsync(request, cancellationToken);
            }

            inFlight = _startInFlight!;
        }

        StartResponse response = await inFlight.ConfigureAwait(false);
        return joined ? response with { AlreadyRecording = true, Message = "Already starting; " + response.Message } : response;
    }

    /// <summary>State reported when a transfer id matches nothing that can be acted on;
    /// the CLI exits with its error code for it.</summary>
    public const string NotFound = "not-found";

    private static StartResponse AlreadyRecording(IRecordingSession session) =>
        new(AlreadyRecording: true, session.Context.SessionId,
            $"Already recording (state {session.State.ToString().ToLowerInvariant()}); the existing session continues.");

    /// <summary>True once a session is on its way out: asked to stop, or past the
    /// point where it records anything.</summary>
    private bool IsFinishing(IRecordingSession session) =>
        (ReferenceEquals(session, _session) && _stopRequested)
        || session.State is SessionState.Stopping or SessionState.Finalizing or SessionState.Completed or SessionState.Failed;

    private async Task<StartResponse> StartNewSessionAsync(StartRequest request, CancellationToken cancellationToken)
    {
        // Yield so the caller releases the lock before planning begins.
        await Task.Yield();

        CaptrSettings settings = _settingsStore.Load();
        IRecordingSession session = await _createSession(settings, request, cancellationToken).ConfigureAwait(false);
        RecordingSession.SessionContext context = session.Context;
        if (_systemEvents is not null)
        {
            session.AttachSystemEvents(_systemEvents);
        }

        lock (_gate)
        {
            if (_session is not null && _sessionRun is { IsCompleted: false })
            {
                // The previous session is stopping or finalising; let it finish in the
                // background, still counted as work and still reported if it fails.
                _finishing.Add((_session, _sessionRun));
            }

            _session = session;
            _stopRequested = false;
            _lastOutcome = null;
            _sessionRun = Task.Run(() => RunToEndAsync(session), CancellationToken.None);
        }

        _log.Information("Recording started: session {SessionId} into {Folder}",
            context.SessionId, context.WorkingFolder);
        return new StartResponse(false, context.SessionId, $"Recording started (session {context.SessionId:N}).");
    }

    /// <summary>
    /// Runs one session to the end of finalisation and records how it ended. Nothing
    /// escapes: a session that throws used to fault a task nobody observed, so the
    /// recording simply stopped — no log line, no status, nothing transferred.
    /// </summary>
    private async Task RunToEndAsync(IRecordingSession session)
    {
        SessionOutcome outcome;
        try
        {
            FinalizationResult result = await session.RunAsync(CancellationToken.None).ConfigureAwait(false);
            HandleFinalized(result);
            outcome = OutcomeOf(session);
        }
        catch (OperationCanceledException)
        {
            outcome = OutcomeOf(session);
        }
        catch (Exception exception)
        {
            _log.Error(exception,
                "Session {SessionId} ended with an internal error; its footage is recovered on the next host start",
                session.Context.SessionId);
            outcome = new SessionOutcome(
                session.Context.SessionId, "faulted",
                "Captr hit an internal error and stopped this recording: " + exception.Message +
                " Everything recorded so far is kept and is finalised automatically the next time Captr starts.",
                DateTimeOffset.UtcNow, session.Context.WorkingFolder);
        }

        lock (_gate)
        {
            _finishing.RemoveAll(entry => ReferenceEquals(entry.Session, session));

            // Only the newest recording's ending is reported; an older one finishing
            // late must not paint a "failed" banner over a recording in progress
            // unless it really failed.
            if (ReferenceEquals(session, _session) || outcome.Result != "completed")
            {
                _lastOutcome = outcome;
            }
        }

        Touch();
    }

    private static SessionOutcome OutcomeOf(IRecordingSession session) =>
        session.FailureReason is { } reason
            ? new SessionOutcome(session.Context.SessionId, "failed", reason, DateTimeOffset.UtcNow, session.Context.WorkingFolder)
            : new SessionOutcome(session.Context.SessionId, "completed", null, DateTimeOffset.UtcNow, session.Context.WorkingFolder);

    public Task<StopResponse> StopAsync(CancellationToken cancellationToken)
    {
        Touch();
        IRecordingSession? session;
        lock (_gate)
        {
            session = _sessionRun is { IsCompleted: false } ? _session : null;
            if (session is not null)
            {
                _stopRequested = true;
            }
        }

        if (session is null)
        {
            // SPEC §10: stopping while idle succeeds.
            return Task.FromResult(new StopResponse(WasRecording: false, "idle"));
        }

        session.RequestStop();
        return Task.FromResult(new StopResponse(true, "stopping — finalisation continues in the background"));
    }

    public Task<StateResponse> PauseAsync(CancellationToken cancellationToken)
    {
        Touch();
        IRecordingSession? session = RecordingSessionOrNull();
        if (session is null)
        {
            return Task.FromResult(new StateResponse(IdleOrFinishing(),
                "Nothing is recording, so there is nothing to pause."));
        }

        if (session.State == SessionState.Paused)
        {
            return Task.FromResult(new StateResponse("paused", "The recording is already paused."));
        }

        session.RequestPause();
        return Task.FromResult(new StateResponse("paused", "Recording paused. The pause is journaled as a gap."));
    }

    public Task<StateResponse> ResumeAsync(CancellationToken cancellationToken)
    {
        Touch();
        IRecordingSession? session = RecordingSessionOrNull();
        if (session is null)
        {
            return Task.FromResult(new StateResponse(IdleOrFinishing(),
                "Nothing is recording, so there is nothing to resume."));
        }

        if (session.State != SessionState.Paused)
        {
            return Task.FromResult(new StateResponse(session.State.ToString().ToLowerInvariant(),
                "The recording is not paused, so there is nothing to resume."));
        }

        session.RequestResume();
        return Task.FromResult(new StateResponse("recording", "Recording resumed into a new segment."));
    }

    private string IdleOrFinishing() => ActiveSession() is { } finishing
        ? finishing.State.ToString().ToLowerInvariant()
        : "idle";

    public Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        IRecordingSession? session = ActiveSession();
        SessionOutcome? lastOutcome;
        lock (_gate)
        {
            lastOutcome = _lastOutcome;
        }

        if (session is null)
        {
            return Task.FromResult(new StatusResponse(
                "idle", null, null, null, null, null, null, null, null, LastOutcome: lastOutcome));
        }

        EncoderProgress? progress = session.LatestProgress;
        RecordingPlanSummary plan = SummarisePlan(session);
        (int gapCount, double coverage) = LiveCoverage(session);
        return Task.FromResult(new StatusResponse(
            session.State.ToString().ToLowerInvariant(),
            session.Context.SessionId,
            plan.StartedUtc,
            DateTimeOffset.UtcNow - plan.StartedUtc,
            plan.Encoder,
            plan.FrameRate,
            progress?.OutTime,
            session.Context.WorkingFolder,
            DiskMinutes(session),
            gapCount,
            coverage,
            session.CurrentQuality,
            session.CurrentSpeedPreset,
            lastOutcome));
    }

    public Task<ListRecordingsResponse> ListRecordingsAsync(CancellationToken cancellationToken)
    {
        Touch();
        return Task.FromResult(new ListRecordingsResponse(
            RecordingCatalog.Scan(_settingsStore.Load().WorkingFolder, cancellationToken)));
    }

    public async Task<VerifyResponse> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        Touch();
        IntegrityRecord? record = IntegrityRecord.ReadOrNull(request.Folder);
        if (record is null)
        {
            return new VerifyResponse(false, [$"No integrity record found in {request.Folder} — was the session finalised?"]);
        }

        IReadOnlyList<string> problems = await record.VerifyAsync(request.Folder, cancellationToken).ConfigureAwait(false);
        return new VerifyResponse(problems.Count == 0, problems);
    }

    public async Task<RecoverResponse> RecoverAsync(CancellationToken cancellationToken)
    {
        Touch();
        IReadOnlyList<RecoveryReport> reports = await RunRecoveryScanAsync(cancellationToken).ConfigureAwait(false);
        return new RecoverResponse(
        [
            .. reports.Select(r => new RecoverySummary(r.SessionFolder, r.Succeeded, r.FailureReason, r.RecoveredDuration)),
        ]);
    }

    /// <summary>
    /// Makes start requests wait for <see cref="RunStartupRecoveryAsync"/>. Called
    /// before the pipe opens, so no start can slip in ahead of recovery (SPEC §6:
    /// interrupted sessions are recovered before new work begins).
    /// </summary>
    public void HoldStartsUntilRecoveryCompletes() =>
        _startupRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The host's startup recovery scan. Releases held starts when it finishes —
    /// including when it fails, because a recovery problem is logged and reported,
    /// and must never stop the user recording something new.
    /// </summary>
    public async Task RunStartupRecoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunRecoveryScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error(exception, "The startup recovery scan failed; new recordings are allowed regardless");
        }
        finally
        {
            _startupRecovery.TrySetResult();
        }
    }

    /// <summary>The recovery scan the host runs at startup (SPEC §6: "on every host
    /// start … run this automatically without prompting, then report").</summary>
    public async Task<IReadOnlyList<RecoveryReport>> RunRecoveryScanAsync(CancellationToken cancellationToken)
    {
        var pipeline = new FinalizationPipeline(FfmpegLocator.FindFfmpeg(), FfmpegLocator.FindFfprobe(), _log);
        var scanner = new RecoveryScanner(pipeline, _log);
        IReadOnlyList<RecoveryReport> reports = await scanner.ScanAndRecoverAsync(
            _settingsStore.Load().WorkingFolder, cancellationToken).ConfigureAwait(false);

        foreach (RecoveryReport report in reports)
        {
            if (report.Succeeded)
            {
                _log.Information(
                    "Recovered session in {Folder}: {Duration} of footage, {Repaired} segment(s) repaired, {Lost} lost",
                    report.SessionFolder, report.RecoveredDuration, report.RepairedSegments, report.TimeLost);
            }
        }

        return reports;
    }

    public Task<ListTransfersResponse> ListTransfersAsync(CancellationToken cancellationToken)
    {
        Touch();
        return Task.FromResult(new ListTransfersResponse(
        [
            .. _transferQueue.ListRecent(DateTimeOffset.UtcNow).Select(i => new TransferSummary(
                i.Id, i.OutputPath, i.DestinationName, i.State, i.Attempts, i.NextAttemptUtc, i.LastError)),
        ]));
    }

    public Task<StateResponse> RetryTransferAsync(RetryTransferRequest request, CancellationToken cancellationToken)
    {
        Touch();
        if (!_transferQueue.Retry(request.Id))
        {
            return Task.FromResult(new StateResponse(NotFound,
                $"There is no transfer {request.Id} that can be retried — it does not exist, or it already completed. " +
                "Run 'captr transfers list' to see the ids."));
        }

        return Task.FromResult(new StateResponse("pending", $"Transfer {request.Id} queued for retry."));
    }

    /// <summary>
    /// Puts transfers that stopped on a bad credential back in the queue.
    /// </summary>
    /// <remarks>
    /// A <c>paused-auth</c> row is deliberately excluded from the queue's due list,
    /// so nothing picks it up again by itself — which means that without this,
    /// replacing an expired secret fixed nothing until every affected transfer was
    /// retried by hand. Called when settings are saved (the moment a new secret is
    /// stored) and once when a host starts. Re-arming costs one attempt, and a
    /// credential that is still wrong simply pauses again.
    /// </remarks>
    private void ReArmAuthPausedTransfers()
    {
        try
        {
            _transferQueue.ResumeAuthPaused();
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.Warning(exception, "Could not re-arm transfers waiting on a credential");
        }
    }

    /// <summary>
    /// Stops a transfer the user has given up on. The row stops retrying by itself
    /// and keeps its last error; Retry is the only thing that starts it again.
    /// Nothing local is touched — the recording is still on disk either way.
    /// </summary>
    public Task<StateResponse> CancelTransferAsync(CancelTransferRequest request, CancellationToken cancellationToken)
    {
        Touch();
        if (!_transferQueue.Cancel(request.Id))
        {
            return Task.FromResult(new StateResponse(NotFound,
                $"There is no transfer {request.Id} that can be stopped — it does not exist, or it already " +
                "finished or stopped. Run 'captr transfers list' to see the ids."));
        }

        _log.Information("Transfer {Id} stopped at the user's request", request.Id);
        return Task.FromResult(new StateResponse(
            TransferQueue.StateCancelled,
            $"Transfer {request.Id} stopped. It will not retry until you press Retry."));
    }

    /// <summary>
    /// Saves settings, enforcing the SPEC §8 lock: while a recording is running,
    /// only degrading capture/quality changes are accepted, and an accepted
    /// degradation is applied to the live session (rolling a new segment, journaled).
    /// Everything unrelated to the encoder saves freely at any time.
    /// </summary>
    public Task<SetSettingsResponse> SetSettingsAsync(SetSettingsRequest request, CancellationToken cancellationToken)
    {
        Touch();
        CaptrSettings current = _settingsStore.Load();
        IRecordingSession? session = RecordingSessionOrNull();

        if (session is null)
        {
            _settingsStore.Save(request.Settings);
            ReArmAuthPausedTransfers();
            return Task.FromResult(new SetSettingsResponse(true, false, "Saved."));
        }

        // Compare against what the session is ACTUALLY running at, not against the
        // settings file — automatic frame-rate reduction may already have taken the
        // live session below the saved value.
        CaptrSettings live = current with
        {
            FrameRate = session.CurrentFrameRate,
            Quality = session.CurrentQuality,
            SpeedPreset = session.CurrentSpeedPreset,
            ExcludedDisplayIds = session.CurrentExcludedDisplayIds,
        };

        SettingsChangeVerdict verdict = SettingsChangePolicy.Evaluate(live, request.Settings);
        if (!verdict.Allowed)
        {
            return Task.FromResult(new SetSettingsResponse(false, false, verdict.RejectionMessage));
        }

        _settingsStore.Save(request.Settings);
        ReArmAuthPausedTransfers();

        if (!verdict.DegradesRecording)
        {
            return Task.FromResult(new SetSettingsResponse(true, false,
                "Saved. The running recording is unaffected."));
        }

        session.ApplyDegradation(request.Settings);
        _log.Information("Degrading settings change applied to the running session");
        return Task.FromResult(new SetSettingsResponse(true, true,
            "Saved and applied to the running recording, which rolled a new segment. The change is journaled."));
    }

    public Task<ResendResponse> ResendAsync(ResendRequest request, CancellationToken cancellationToken)
    {
        Touch();

        // Re-send the session's OUTPUTS (not its raw segments) to every enabled
        // destination — SPEC §9's "re-sending to a destination", used after a
        // destination was fixed, added, or its transfer was abandoned.
        IntegrityRecord? record = IntegrityRecord.ReadOrNull(request.Folder);
        if (record is null)
        {
            return Task.FromResult(new ResendResponse(0,
                $"No integrity record in {request.Folder} — only a finalised recording can be re-sent."));
        }

        List<DestinationSettings> destinations =
            [.. _settingsStore.Load().Destinations.Where(d => d.Enabled)];
        if (destinations.Count == 0)
        {
            return Task.FromResult(new ResendResponse(0, "No destinations are enabled, so there is nowhere to send."));
        }

        // Names and folders resolve against WHEN THE RECORDING WAS MADE, not now, so
        // a recording sent again next week still lands in its own dated folder. The
        // journal is where that information lives; without it the tokens would
        // silently expand to today.
        NamingContext? context = RecordingCatalog.ReadNamingContext(request.Folder);

        int queued = 0;
        var skipped = new List<string>();
        foreach (HashedFile output in record.Outputs)
        {
            string path = Path.Combine(request.Folder, output.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            IReadOnlyList<string> alreadyThere = _transferQueue.CompletedDestinationsFor(path);
            foreach (DestinationSettings destination in destinations)
            {
                // Sending a second copy to a destination that already has it would
                // land beside the first as "name (2)" and mean nothing. Skipping is
                // what makes this button safe to press twice.
                if (alreadyThere.Contains(destination.Name, StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(destination.Name);
                    continue;
                }

                Enqueue(path, destination, context, Path.GetExtension(path));
                queued++;
            }
        }

        _log.Information("Re-send queued {Count} transfer(s) for {Folder}", queued, request.Folder);
        return Task.FromResult(new ResendResponse(queued, DescribeResend(queued, destinations.Count, skipped)));
    }

    /// <summary>Plain English for what a re-send actually did, including the case
    /// where every destination already has the file and nothing was queued.</summary>
    private static string DescribeResend(int queued, int destinationCount, List<string> skipped)
    {
        string skippedText = skipped.Count == 0
            ? ""
            : $" Already at: {string.Join(", ", skipped.Distinct(StringComparer.OrdinalIgnoreCase))}.";

        if (queued > 0)
        {
            return $"Queued {queued} transfer(s) across {destinationCount} destination(s).{skippedText}";
        }

        return skipped.Count > 0
            ? $"Nothing to send — every enabled destination already has this recording.{skippedText}"
            : "Nothing to send — the output files are no longer in the working folder.";
    }

    /// <summary>
    /// The stop-side hand-off (SPEC §7): rename each finalised output by the user's
    /// pattern (sanitised, collision-suffixed — within the working folder, so
    /// nothing leaves it) and enqueue one transfer per enabled destination.
    /// Failures here are logged, never thrown — the recording itself is already
    /// safe on disk, and transfer problems must not look like recording problems.
    /// </summary>
    private void HandleFinalized(FinalizationResult result)
    {
        try
        {
            CaptrSettings settings = _settingsStore.Load();
            var context = new NamingContext
            {
                StartUtc = result.Coverage.StartUtc,
                EndUtc = result.Coverage.EndUtc,
                MachineName = result.Session.MachineName,
                UserName = result.Session.UserName,
                TimeZone = FindTimeZone(result.Session.LocalTimeZoneId),
                Label = result.Session.Label,
            };

            int groupIndex = 0;
            foreach (string output in result.OutputFiles)
            {
                groupIndex++;
                string desiredName = OutputNamer.BuildFileName(settings.OutputPattern, context);
                if (result.OutputFiles.Count > 1)
                {
                    // Multiple arrangement groups: part-number the outputs.
                    desiredName = Path.GetFileNameWithoutExtension(desiredName)
                        + FormattableString.Invariant($" part{groupIndex}") + Path.GetExtension(desiredName);
                }

                string workingFolder = Path.GetDirectoryName(output)!;
                string finalPath = OutputNamer.ResolveCollision(workingFolder, desiredName);
                File.Move(output, finalPath);

                // The integrity record was written against the pipeline's intermediate
                // name; point it at the name the file now has. Skipping this makes
                // `recordings verify` report EVERY finalised recording as missing.
                if (!IntegrityRecord.RecordOutputRename(
                        workingFolder, Path.GetFileName(output), Path.GetFileName(finalPath)))
                {
                    _log.Warning(
                        "Renamed {Output} to {Final} but its integrity record could not be updated; " +
                        "verification of this recording will report the output as missing",
                        Path.GetFileName(output), Path.GetFileName(finalPath));
                }

                _log.Information("Output named {Final}", finalPath);

                foreach (DestinationSettings destination in settings.Destinations.Where(d => d.Enabled))
                {
                    Enqueue(finalPath, destination, context, Path.GetExtension(finalPath));
                }
            }

            if (!settings.Destinations.Any(d => d.Enabled))
            {
                // A workflow with no destinations is a supported choice, not a
                // misconfiguration — say so plainly instead of staying silent.
                _log.Information(
                    "No destinations are enabled; the recording stays in its working folder and nothing is transferred");
            }
        }
        catch (Exception exception) when (exception is IOException or SettingsValidationException)
        {
            _log.Error(exception, "Output naming/enqueue failed; the finalised files remain in the working folder");
        }

        Touch();
    }

    /// <summary>
    /// Queues one file for one destination, resolving BOTH the name it takes there
    /// and the folder it lands in.
    /// </summary>
    /// <remarks>
    /// Both are resolved here, once, and stored on the queue row rather than being
    /// recomputed on each attempt. A destination folder may contain date tokens
    /// (<c>\\archive\recordings\{date:yyyy-MM}</c>), and a transfer that fails at
    /// 23:59 and retries at 00:01 must land where it was meant to, not in tomorrow.
    /// </remarks>
    private void Enqueue(
        string sourcePath, DestinationSettings destination, NamingContext? context, string extension)
    {
        string? targetName = context is null ? null : BuildTargetName(destination, context, extension);
        string? targetFolder = ResolveTargetFolder(destination, context);

        long id = _transferQueue.Enqueue(sourcePath, destination.Name, targetName, targetFolder);
        _log.Information("Transfer {Id} queued: {File} → {Destination}{Folder} as {Name}",
            id, sourcePath, destination.Name,
            targetFolder is null ? "" : " (" + targetFolder + ")",
            targetName ?? Path.GetFileName(sourcePath));
    }

    /// <summary>
    /// The destination folder with its date tokens expanded, or null when it has
    /// none (in which case the worker uses the destination's folder as configured).
    /// </summary>
    private string? ResolveTargetFolder(DestinationSettings destination, NamingContext? context)
    {
        string? configured = destination.Kind == DestinationKind.Folder
            ? destination.FolderPath
            : destination.SharePointFolder;

        if (context is null
            || string.IsNullOrWhiteSpace(configured)
            || !configured.Contains('{', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return OutputNamer.ExpandFolderPath(configured, context);
        }
        catch (ArgumentException exception)
        {
            _log.Error(exception,
                "Destination {Destination} has an invalid folder pattern; sending to the folder as written instead",
                destination.Name);
            return null;
        }
    }

    /// <summary>
    /// The name a recording takes at ONE destination, from that destination's own
    /// naming pattern. Returns null when the destination has no pattern, which means
    /// "keep the recording's own name" — the common case.
    /// </summary>
    private string? BuildTargetName(DestinationSettings destination, NamingContext context, string extension)
    {
        if (string.IsNullOrWhiteSpace(destination.FileNamePattern))
        {
            return null;
        }

        try
        {
            string name = OutputNamer.BuildFileName(destination.FileNamePattern, context);
            return Path.HasExtension(name) ? name : name + extension;
        }
        catch (ArgumentException exception)
        {
            // A bad pattern must never cost a transfer: fall back to the local name
            // and say loudly which destination needs fixing.
            _log.Error(exception,
                "Destination {Destination} has an invalid file-name pattern; transferring under the recording's own name instead",
                destination.Name);
            return null;
        }
    }

    private static TimeZoneInfo FindTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>The session status should describe: the newest one while it runs,
    /// otherwise any older one still finalising (so "stop, then wait for idle" still
    /// means "wait until the recording is saved").</summary>
    private IRecordingSession? ActiveSession()
    {
        lock (_gate)
        {
            if (_sessionRun is { IsCompleted: false })
            {
                return _session;
            }

            return _finishing.Count > 0 ? _finishing[^1].Session : null;
        }
    }

    /// <summary>The session that is actually recording (or paused) — the only one
    /// pause, resume, and settings changes can act on.</summary>
    private IRecordingSession? RecordingSessionOrNull()
    {
        lock (_gate)
        {
            return _session is not null && _sessionRun is { IsCompleted: false } && !IsFinishing(_session)
                ? _session
                : null;
        }
    }

    private void Touch() => _lastActivityUtc = DateTimeOffset.UtcNow;

    private sealed record RecordingPlanSummary(DateTimeOffset StartedUtc, string Encoder, int FrameRate);

    private static RecordingPlanSummary SummarisePlan(IRecordingSession session)
    {
        try
        {
            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
                Path.Combine(session.Context.WorkingFolder, SessionJournal.FileName));
            if (events.OfType<SessionStarted>().FirstOrDefault() is { } start)
            {
                return new RecordingPlanSummary(start.TimestampUtc, start.EncoderName, start.FrameRate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through: a status query must never fail because the journal was
            // momentarily unreadable (or its folder has been deleted under us).
        }

        return new RecordingPlanSummary(
            DateTimeOffset.UtcNow, session.Context.InitialPlan.Encoder.CodecName, session.CurrentFrameRate);
    }

    /// <summary>
    /// Coverage of the session SO FAR (SPEC §9: the status view shows coverage
    /// including any gaps while recording, not only afterwards). Computed from the
    /// live journal with "now" as the end of the observation window — the same
    /// arithmetic finalisation will apply, so the number the user watches is the
    /// number they get.
    /// </summary>
    private static (int GapCount, double Coverage) LiveCoverage(IRecordingSession session)
    {
        try
        {
            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
                Path.Combine(session.Context.WorkingFolder, SessionJournal.FileName));
            CoverageReport report = CoverageCalculator.Compute(events, DateTimeOffset.UtcNow);
            return (report.GapCount, report.Coverage);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            // A status query must never fail because the journal was momentarily
            // unreadable; report the optimistic default and move on.
            return (0, 1.0);
        }
    }

    private static double? DiskMinutes(IRecordingSession session)
    {
        try
        {
            var guard = new DiskGuard(session.Context.WorkingFolder, session.Context.MeasuredBytesPerHour);
            return guard.MinutesRemaining(guard.FreeBytesOnVolume()).TotalMinutes;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
