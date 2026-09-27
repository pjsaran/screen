using Captr.Core.Encoders;
using Captr.Core.Hosting;
using Captr.Core.Ipc;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Settings.Migrations;
using Captr.Core.Supervision;
using Captr.Core.Transfers;
using Captr.Core.WindowsEvents;

using Serilog.Core;

using Shouldly;

namespace Captr.Core.Tests.Hosting;

/// <summary>
/// The host's start rules (SPEC §10): starting while recording is not an error, a
/// start is never lost, and there is only ever ONE recording.
/// </summary>
/// <remarks>
/// A scheduled task, a hotkey, the tray, and the CLI can all ask for a start at the
/// same moment — and planning a start is slow (display resolution, and on a first
/// run an encoder trial), so "at the same moment" is a window of seconds, not
/// microseconds. The sessions here are fakes: what is under test is the host's
/// bookkeeping, not the recording.
/// </remarks>
public sealed class HostServiceStartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-host-").FullName;
    private readonly SettingsStore _settings;
    private readonly TransferQueue _queue;
    private readonly List<FakeSession> _created = [];
    private readonly Lock _createdGate = new();
    private int _planCalls;

    public HostServiceStartTests()
    {
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), SettingsMigrator.Default);
        _settings.Save(CaptrSettings.CreateDefault() with { WorkingFolder = Path.Combine(_dir, "Sessions") });
        _queue = new TransferQueue(Path.Combine(_dir, "transfers.db"));
    }

    public void Dispose()
    {
        foreach (FakeSession session in _created)
        {
            session.Finish();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private HostService MakeHost(TimeSpan planningTakes) => new(
        _settings, null, Logger.None, _queue, null,
        async (settings, request, cancellationToken) =>
        {
            Interlocked.Increment(ref _planCalls);
            await Task.Delay(planningTakes, cancellationToken);
            var session = new FakeSession(Path.Combine(_dir, "Sessions", Guid.NewGuid().ToString("N")));
            lock (_createdGate)
            {
                _created.Add(session);
            }

            return session;
        });

    private static StartRequest Start() => new(null, null, null, null);

    [Fact]
    public async Task Two_starts_at_the_same_moment_make_one_recording_and_both_succeed()
    {
        HostService host = MakeHost(planningTakes: TimeSpan.FromMilliseconds(500));
        CancellationToken ct = TestContext.Current.CancellationToken;

        StartResponse[] responses = await Task.WhenAll(
            host.StartAsync(Start(), ct), host.StartAsync(Start(), ct), host.StartAsync(Start(), ct));

        _planCalls.ShouldBe(1, "a second plan means a second encoder recording the same screen");
        responses.Count(r => !r.AlreadyRecording).ShouldBe(1);
        responses.Select(r => r.SessionId).Distinct().Count().ShouldBe(1, "every caller is told about the same session");
    }

    [Fact]
    public async Task The_session_every_caller_was_told_about_is_the_one_stop_stops()
    {
        HostService host = MakeHost(planningTakes: TimeSpan.FromMilliseconds(300));
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(host.StartAsync(Start(), ct), host.StartAsync(Start(), ct));
        await host.StopAsync(ct);

        _created.ShouldAllBe(session => session.StopRequested, "no recording may be left running where nothing can reach it");
    }

    [Fact]
    public async Task Starting_while_the_previous_recording_finalises_starts_a_new_one()
    {
        // Back-to-back schedules: a stop task at 12:00 and a start task at 12:00. The
        // first recording is still being finalised (joining and hashing can take
        // minutes) when the second start arrives.
        HostService host = MakeHost(planningTakes: TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        StartResponse first = await host.StartAsync(Start(), ct);
        await host.StopAsync(ct);
        _created[0].EnterState(SessionState.Finalizing);

        StartResponse second = await host.StartAsync(Start(), ct);

        second.AlreadyRecording.ShouldBeFalse("the next block must actually be recorded");
        second.SessionId.ShouldNotBe(first.SessionId);
        host.IsBusy.ShouldBeTrue();
    }

    [Fact]
    public async Task Status_keeps_reporting_a_finalising_recording_until_it_is_done()
    {
        // The installer's "stop, then wait for idle" and every script that waits for
        // a recording to be saved depend on this.
        HostService host = MakeHost(planningTakes: TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await host.StartAsync(Start(), ct);
        await host.StopAsync(ct);
        _created[0].EnterState(SessionState.Finalizing);

        (await host.GetStatusAsync(ct)).State.ShouldBe("finalizing");

        _created[0].Finish();
        await WaitUntilAsync(() => !host.IsBusy, ct);
        (await host.GetStatusAsync(ct)).State.ShouldBe("idle");
    }

    [Fact]
    public async Task A_recording_that_fails_on_its_own_is_still_reported_after_it_ends()
    {
        HostService host = MakeHost(planningTakes: TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        StartResponse started = await host.StartAsync(Start(), ct);
        _created[0].Fail("The encoder failed repeatedly.");
        await WaitUntilAsync(() => !host.IsBusy, ct);

        StatusResponse status = await host.GetStatusAsync(ct);
        status.State.ShouldBe("idle");
        status.LastOutcome.ShouldNotBeNull();
        status.LastOutcome.SessionId.ShouldBe(started.SessionId);
        status.LastOutcome.Result.ShouldBe("failed");
        status.LastOutcome.Reason.ShouldBe("The encoder failed repeatedly.");
    }

    [Fact]
    public async Task A_session_that_throws_is_logged_as_faulted_rather_than_vanishing()
    {
        HostService host = MakeHost(planningTakes: TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await host.StartAsync(Start(), ct);
        _created[0].Throw(new IOException("heartbeat.json is locked"));
        await WaitUntilAsync(() => !host.IsBusy, ct);

        SessionOutcome? outcome = (await host.GetStatusAsync(ct)).LastOutcome;
        outcome.ShouldNotBeNull();
        outcome.Result.ShouldBe("faulted");
        outcome.Reason!.ShouldContain("heartbeat.json is locked");
    }

    [Fact]
    public async Task Pause_on_a_recording_that_is_already_finalising_says_so_instead_of_claiming_success()
    {
        HostService host = MakeHost(planningTakes: TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await host.StartAsync(Start(), ct);
        await host.StopAsync(ct);
        _created[0].EnterState(SessionState.Finalizing);

        StateResponse paused = await host.PauseAsync(ct);

        paused.State.ShouldNotBe("paused");
        _created[0].PauseRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task A_start_during_the_startup_recovery_scan_waits_for_it_instead_of_racing_it()
    {
        HostService host = MakeHost(planningTakes: TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;
        host.HoldStartsUntilRecoveryCompletes();

        Task<StartResponse> start = host.StartAsync(Start(), ct);
        await Task.Delay(200, ct);
        start.IsCompleted.ShouldBeFalse("recovery must finish before new work begins (SPEC §6)");

        await host.RunStartupRecoveryAsync(ct);
        (await start).AlreadyRecording.ShouldBeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    /// <summary>A session whose life the test controls.</summary>
    private sealed class FakeSession(string folder) : IRecordingSession
    {
        private readonly TaskCompletionSource<FinalizationResult> _run =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool StopRequested { get; private set; }

        public bool PauseRequested { get; private set; }

        public RecordingSession.SessionContext Context { get; } = new(
            Guid.NewGuid(), folder, "ffmpeg.exe", "ffprobe.exe",
            new RecordingPlan
            {
                SessionId = Guid.NewGuid(),
                Sources = [],
                FrameRate = 15,
                Encoder = new EncoderSettings("libx264", []),
                WorkingFolder = folder,
            },
            null, 1, [], "balanced", "fast");

        public SessionState State { get; private set; } = SessionState.Recording;

        public EncoderProgress? LatestProgress => null;

        public int CurrentFrameRate => 15;

        public string CurrentQuality => "balanced";

        public string CurrentSpeedPreset => "fast";

        public IReadOnlyList<string> CurrentExcludedDisplayIds => [];

        public string? FailureReason { get; private set; }

        public void EnterState(SessionState state) => State = state;

        public void Finish()
        {
            State = SessionState.Completed;
            _run.TrySetCanceled();
        }

        public void Fail(string reason)
        {
            FailureReason = reason;
            State = SessionState.Failed;
            _run.TrySetCanceled();
        }

        public void Throw(Exception exception) => _run.TrySetException(exception);

        public void RequestStop()
        {
            StopRequested = true;
            State = SessionState.Stopping;
        }

        public void RequestPause() => PauseRequested = true;

        public void RequestResume()
        {
        }

        public void ApplyDegradation(CaptrSettings degraded)
        {
        }

        public void AttachSystemEvents(SystemEventWindow events)
        {
        }

        // VSTHRD003: handing back a task started elsewhere IS the point of this fake —
        // the test decides when the "recording" ends — and no synchronisation context
        // exists here for it to deadlock on.
#pragma warning disable VSTHRD003
        public Task<FinalizationResult> RunAsync(CancellationToken hostShutdown) => _run.Task;
#pragma warning restore VSTHRD003
    }
}
