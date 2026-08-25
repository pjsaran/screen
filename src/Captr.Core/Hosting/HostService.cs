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
    private readonly Lock _gate = new();

    private RecordingSession? _session;
    private Task<FinalizationResult>? _sessionRun;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;

    public HostService(
        SettingsStore settingsStore,
        MessageOnlyWindow? systemEvents,
        ILogger log,
        TransferQueue? transferQueue = null,
        TransferWorker? transferWorker = null)
    {
        _settingsStore = settingsStore;
        _systemEvents = systemEvents;
        _log = log.ForContext<HostService>();
        _transferQueue = transferQueue ?? new TransferQueue();
        _transferWorker = transferWorker;
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
                if (_sessionRun is { IsCompleted: false })
                {
                    return true;
                }
            }

            return _transferWorker?.HasPendingWork == true;
        }
    }

    public async Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        Touch();
        lock (_gate)
        {
            if (_session is not null && _sessionRun is { IsCompleted: false })
            {
                // SPEC §10: not an error — report the existing state, succeed.
                return new StartResponse(
                    AlreadyRecording: true, _session.Context.SessionId,
                    $"Already recording (state {_session.State}); the existing session continues.");
            }
        }

        CaptrSettings settings = _settingsStore.Load();
        var planner = new SessionPlanner(_log);
        (RecordingSession.SessionContext context, SessionStarted startEvent) = await planner.PlanAsync(
            settings, request.FrameRate, request.Quality, request.SpeedPreset, request.Label, cancellationToken)
            .ConfigureAwait(false);

        RecordingSession session = RecordingSession.Create(context, startEvent, _log);
        if (_systemEvents is not null)
        {
            session.AttachSystemEvents(_systemEvents);
        }

        lock (_gate)
        {
            _session = session;
            _sessionRun = Task.Run(
                async () =>
                {
                    FinalizationResult result = await session.RunAsync(CancellationToken.None).ConfigureAwait(false);
                    HandleFinalized(result);
                    return result;
                },
                CancellationToken.None);
        }

        _log.Information("Recording started: session {SessionId} into {Folder}",
            context.SessionId, context.WorkingFolder);
        return new StartResponse(false, context.SessionId, $"Recording started (session {context.SessionId:N}).");
    }

    public Task<StopResponse> StopAsync(CancellationToken cancellationToken)
    {
        Touch();
        RecordingSession? session;
        lock (_gate)
        {
            session = _sessionRun is { IsCompleted: false } ? _session : null;
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
        RecordingSession? session = ActiveSession();
        if (session is null)
        {
            return Task.FromResult(new StateResponse("idle", "Nothing is recording, so there is nothing to pause."));
        }

        session.RequestPause();
        return Task.FromResult(new StateResponse("paused", "Recording paused. The pause is journaled as a gap."));
    }

    public Task<StateResponse> ResumeAsync(CancellationToken cancellationToken)
    {
        Touch();
        RecordingSession? session = ActiveSession();
        if (session is null)
        {
            return Task.FromResult(new StateResponse("idle", "Nothing is recording, so there is nothing to resume."));
        }

        session.RequestResume();
        return Task.FromResult(new StateResponse("recording", "Recording resumed into a new segment."));
    }

    public Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        RecordingSession? session = ActiveSession();
        if (session is null)
        {
            return Task.FromResult(new StatusResponse(
                "idle", null, null, null, null, null, null, null, null));
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
            session.CurrentSpeedPreset));
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
        _transferQueue.Retry(request.Id);
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
        _transferQueue.Cancel(request.Id);
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
        RecordingSession? session = ActiveSession();

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

    private RecordingSession? ActiveSession()
    {
        lock (_gate)
        {
            return _sessionRun is { IsCompleted: false } ? _session : null;
        }
    }

    private void Touch() => _lastActivityUtc = DateTimeOffset.UtcNow;

    private sealed record RecordingPlanSummary(DateTimeOffset StartedUtc, string Encoder, int FrameRate);

    private static RecordingPlanSummary SummarisePlan(RecordingSession session)
    {
        IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(
            Path.Combine(session.Context.WorkingFolder, SessionJournal.FileName));
        SessionStarted start = events.OfType<SessionStarted>().First();
        return new RecordingPlanSummary(start.TimestampUtc, start.EncoderName, start.FrameRate);
    }

    /// <summary>
    /// Coverage of the session SO FAR (SPEC §9: the status view shows coverage
    /// including any gaps while recording, not only afterwards). Computed from the
    /// live journal with "now" as the end of the observation window — the same
    /// arithmetic finalisation will apply, so the number the user watches is the
    /// number they get.
    /// </summary>
    private static (int GapCount, double Coverage) LiveCoverage(RecordingSession session)
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

    private static double? DiskMinutes(RecordingSession session)
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
