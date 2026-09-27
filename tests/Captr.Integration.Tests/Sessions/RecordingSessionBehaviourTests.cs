using Captr.Core.Displays;
using Captr.Core.Encoders;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.WindowsEvents;

using Serilog.Core;

using Shouldly;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Captr.Integration.Tests.Sessions;

/// <summary>
/// What a recording session does with the things that happen to it between encoder
/// runs — settings changes, display changes, pauses — driven against a real session
/// object (real journal, real ballast) with the displays supplied by the test and no
/// encoder running.
/// </summary>
/// <remarks>
/// Each of these used to go wrong silently: a display excluded while paused came
/// back, a display excluded mid-session came back at the next topology change, and
/// the software fallback kept recording the displays and rate the session STARTED
/// with. None of it showed until someone watched the footage.
/// </remarks>
[Trait("Category", "Os")]
public sealed class RecordingSessionBehaviourTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("captr-behaviour-").FullName;
    private readonly List<DisplayInfo> _displays = [Display("A", 0, 0), Display("B", 1, 1920)];

    private readonly List<RecordingSession> _sessions = [];

    public void Dispose()
    {
        foreach (RecordingSession session in _sessions)
        {
            session.ReleaseWithoutRunning();
        }

        Directory.Delete(_folder, recursive: true);
    }

    private static DisplayInfo Display(string id, int index, int x) => new()
    {
        StableId = id,
        WindowsDisplayNumber = index + 1,
        FriendlyName = "Test " + id,
        DxgiOutputIndex = index,
        DxgiAdapterIndex = 0,
        Width = 1920,
        Height = 1080,
        VirtualX = x,
        VirtualY = 0,
        DpiScale = 1.0,
        RefreshRateHz = 60,
    };

    private RecordingSession NewSession()
    {
        var plan = new RecordingPlan
        {
            SessionId = Guid.NewGuid(),
            Sources = [.. _displays.Select(d => new CaptureSource(d.DxgiOutputIndex, d.Width, d.Height, d.VirtualX, d.VirtualY))],
            FrameRate = 15,
            Encoder = new EncoderSettings("h264_nvenc", []),
            WorkingFolder = _folder,
        };
        var context = new RecordingSession.SessionContext(
            plan.SessionId, _folder, "ffmpeg.exe", "ffprobe.exe", plan, SoftwareFallbackArguments: null,
            MeasuredBytesPerHour: 1_000_000_000, ExcludedDisplayIds: [], "balanced", "fast",
            CacheToInvalidateOnEarlyFailure: null, SoftwareFallbackCodec: "libx264");
        var start = new SessionStarted
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = plan.SessionId,
            LocalTimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = "TEST",
            UserName = "test",
            AppVersion = "test",
            FfmpegBuildId = "test",
            Displays = [],
            CanvasWidth = 3840,
            CanvasHeight = 1080,
            FrameRate = 15,
            EncoderName = "h264_nvenc",
            Quality = "balanced",
            SpeedPreset = "fast",
            EncoderArguments = [],
            WorkingFolder = _folder,
        };
        RecordingSession session = RecordingSession.Create(context, start, Logger.None, () => _displays);
        _sessions.Add(session);
        return session;
    }

    private static CaptrSettings Excluding(params string[] ids) =>
        CaptrSettings.CreateDefault() with { ExcludedDisplayIds = ids, FrameRate = 10 };

    private IReadOnlyList<JournalEvent> Journal() =>
        SessionJournal.ReadAll(Path.Combine(_folder, SessionJournal.FileName));

    [Fact]
    public async Task A_settings_change_made_while_paused_takes_effect_on_resume()
    {
        RecordingSession session = NewSession();
        Task paused = session.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        session.ApplyDegradation(Excluding("B"));
        session.RequestResume();
        await paused.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        session.CurrentExcludedDisplayIds.ShouldBe(["B"], "the user was told the change was applied");
        session.CurrentFrameRate.ShouldBe(10);
    }

    [Fact]
    public void A_display_excluded_mid_session_stays_excluded_after_a_topology_change()
    {
        RecordingSession session = NewSession();
        session.ApplyDegradation(Excluding("B"));
        session.HandleWhileIdle(RecordingSession.SessionCommand.ApplyDegradation).ShouldBeTrue();

        session.RebuildForNewTopology();

        Journal().OfType<TopologyChanged>().ShouldAllBe(change => change.NewDisplays.All(d => d.StableId == "A"));
        string arguments = string.Join(' ', session.BuildRunSpec().PrimaryArguments);
        CountOf(arguments, "ddagrab").ShouldBe(1, "display B stays excluded");
    }

    [Fact]
    public void A_display_notification_that_changes_nothing_recorded_does_not_restart_the_encoder()
    {
        // Windows broadcasts "devices changed" for any USB device, and the event
        // window hears broadcasts now. Each one used to start a new arrangement group:
        // a restart, a gap, and a second output file, for nothing.
        RecordingSession session = NewSession();

        session.DisplaysDifferFromPlan().ShouldBeFalse();
        session.RebuildForNewTopology();
        Journal().OfType<TopologyChanged>().ShouldBeEmpty();

        _displays[1] = _displays[1] with { Width = 2560, Height = 1440 };
        session.DisplaysDifferFromPlan().ShouldBeTrue("a resolution change is a real change");
        session.RebuildForNewTopology();
        Journal().OfType<TopologyChanged>().ShouldHaveSingleItem().NewArrangementGroup.ShouldBe(2);
    }

    [Fact]
    public void When_every_display_is_gone_the_session_waits_rather_than_relaunching_a_stale_plan()
    {
        RecordingSession session = NewSession();
        _displays.Clear();

        session.RebuildForNewTopology();

        Journal().OfType<TopologyChanged>().ShouldBeEmpty();
        Journal().OfType<SessionNote>().ShouldContain(note => note.Text.Contains("waits for a display"));
    }

    [Fact]
    public void The_software_fallback_follows_the_current_plan_not_the_one_the_session_started_with()
    {
        RecordingSession session = NewSession();
        session.ApplyDegradation(Excluding("B"));
        session.HandleWhileIdle(RecordingSession.SessionCommand.ApplyDegradation);

        Captr.Core.Supervision.EncoderRunSpec spec = session.BuildRunSpec();

        spec.FallbackArguments.ShouldNotBeNull();
        string fallback = string.Join(' ', spec.FallbackArguments);
        fallback.ShouldContain("libx264");
        CountOf(fallback, "ddagrab").ShouldBe(1, "display B was excluded");
        fallback.ShouldContain("framerate=10", Case.Sensitive);
        fallback.ShouldContain("seg-g02-", Case.Sensitive);
        string.Join(' ', spec.PrimaryArguments).ShouldContain("seg-g02-", Case.Sensitive);
    }

    [Fact]
    public void A_finished_session_stops_answering_for_the_shutdown_screen()
    {
        // The event window lives as long as the host; a session that stayed
        // subscribed after it ended held every later shutdown with "Captr is
        // finishing the recording".
        using var window = new SystemEventWindow();
        RecordingSession session = NewSession();
        session.AttachSystemEvents(window);
        session.DetachSystemEvents();

        QueryEndSession(window);

        window.ShutdownBlocked.ShouldBeFalse();
    }

    [Fact]
    public void A_running_session_holds_the_shutdown_until_it_releases_it()
    {
        using var window = new SystemEventWindow();
        RecordingSession session = NewSession();
        session.AttachSystemEvents(window);

        QueryEndSession(window);

        window.ShutdownBlocked.ShouldBeTrue("Windows must wait while a recording is being saved");
        session.DetachSystemEvents();
    }

    private static int CountOf(string text, string word) =>
        (text.Length - text.Replace(word, "", StringComparison.Ordinal).Length) / word.Length;

    private static unsafe void QueryEndSession(SystemEventWindow window)
    {
        // Sent to OUR window only — never broadcast: every other program would take it
        // as the machine shutting down.
        PInvoke.SendMessageTimeout(
            (HWND)window.Handle, PInvoke.WM_QUERYENDSESSION, 0, 0,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK, 5000, null);
    }
}
