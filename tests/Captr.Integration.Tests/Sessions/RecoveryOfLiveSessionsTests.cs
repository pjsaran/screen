using Captr.Core.Sessions;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Sessions;

/// <summary>
/// Recovery finalises CRASHED sessions only — never one that is still recording.
/// </summary>
/// <remarks>
/// Scenario: a recording is running and someone runs `captr recover`. Every session
/// folder without a "finalised" entry used to count as crashed, so recovery adopted
/// the live recording's encoder, stopped it after five seconds, and began finalising
/// a folder that was still being written.
/// </remarks>
[Trait("Category", "Os")]
public sealed class RecoveryOfLiveSessionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-recover-live-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string UnfinalisedSession(string name, DateTimeOffset? heartbeatAt, int? hostProcessId)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        using (SessionJournal.CreateNew(folder, new SessionStarted
        {
            TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            SessionId = Guid.NewGuid(),
            LocalTimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = "TEST",
            UserName = "test",
            AppVersion = "test",
            FfmpegBuildId = "test",
            Displays = [],
            CanvasWidth = 320,
            CanvasHeight = 180,
            FrameRate = 10,
            EncoderName = "libx264",
            Quality = "balanced",
            SpeedPreset = "fast",
            EncoderArguments = [],
            WorkingFolder = folder,
        }))
        {
        }

        if (heartbeatAt is { } written)
        {
            new HeartbeatSnapshot
            {
                SessionId = Guid.NewGuid(),
                WrittenUtc = written,
                State = "Recording",
                HostProcessId = hostProcessId ?? Environment.ProcessId,
            }.Write(folder);
        }

        return folder;
    }

    private static RecoveryScanner Scanner() =>
        new(new FinalizationPipeline("ffmpeg.exe", "ffprobe.exe", Logger.None), Logger.None);

    [Fact]
    public async Task A_session_whose_host_is_still_heartbeating_is_left_alone()
    {
        string live = UnfinalisedSession("live", DateTimeOffset.UtcNow, Environment.ProcessId);
        string crashed = UnfinalisedSession("crashed", DateTimeOffset.UtcNow.AddMinutes(-5), Environment.ProcessId);

        IReadOnlyList<RecoveryReport> reports =
            await Scanner().ScanAndRecoverAsync(_root, TestContext.Current.CancellationToken);

        reports.Select(r => r.SessionFolder).ShouldNotContain(live);
        reports.Select(r => r.SessionFolder).ShouldContain(crashed, "a stale heartbeat is a crash, and recovering it is the point");
    }

    [Fact]
    public async Task A_session_the_caller_owns_is_left_alone_even_without_a_heartbeat_yet()
    {
        // A session that has only just started may not have written its first
        // heartbeat; the host names its own sessions explicitly.
        string starting = UnfinalisedSession("starting", heartbeatAt: null, hostProcessId: null);

        IReadOnlyList<RecoveryReport> reports =
            await Scanner().ScanAndRecoverAsync(_root, TestContext.Current.CancellationToken, inUse: [starting]);

        reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_fresh_heartbeat_from_a_process_that_has_gone_is_a_crash()
    {
        string orphaned = UnfinalisedSession("orphaned", DateTimeOffset.UtcNow, hostProcessId: 999_999_999);

        IReadOnlyList<RecoveryReport> reports =
            await Scanner().ScanAndRecoverAsync(_root, TestContext.Current.CancellationToken);

        reports.Select(r => r.SessionFolder).ShouldContain(orphaned);
    }

    [Fact]
    public void Only_a_recording_still_being_made_is_protected_from_deletion()
    {
        // The Recordings page asks this before deleting: deleting a live session
        // pulled the folder out from under the encoder and faulted the recording.
        RecoveryScanner.IsBeingRecorded(UnfinalisedSession("live", DateTimeOffset.UtcNow, Environment.ProcessId))
            .ShouldBeTrue();
        RecoveryScanner.IsBeingRecorded(UnfinalisedSession("crashed", DateTimeOffset.UtcNow.AddMinutes(-5), Environment.ProcessId))
            .ShouldBeFalse();
        RecoveryScanner.IsBeingRecorded(UnfinalisedSession("orphaned", DateTimeOffset.UtcNow, hostProcessId: 999_999_999))
            .ShouldBeFalse();
    }
}
