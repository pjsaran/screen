using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Settings.Migrations;
using Captr.Core.Transfers;
using Serilog.Core;
using Shouldly;

namespace Captr.Core.Tests.Transfers;

/// <summary>
/// The only place Captr deletes recorded data on its own initiative (SPEC §7), so
/// every refusal path is tested explicitly — a wrong "yes" here loses a recording.
/// </summary>
public class RetentionCleanerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("captr-retention-").FullName;
    private readonly string _workingRoot;
    private readonly TransferQueue _queue;
    private readonly SettingsStore _settingsStore;

    public RetentionCleanerTests()
    {
        _workingRoot = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(_workingRoot);
        _queue = new TransferQueue(Path.Combine(_dir, "transfers.db"));
        _settingsStore = new SettingsStore(Path.Combine(_dir, "settings.json"), SettingsMigrator.Default);
        _settingsStore.Save(CaptrSettings.CreateDefault() with { WorkingFolder = _workingRoot, RetentionDays = 14 });
    }

    private RetentionCleaner MakeCleaner() => new(_queue, _settingsStore, Logger.None);

    /// <summary>Creates a session folder; <paramref name="finalizedDaysAgo"/> null
    /// means "never finalised".</summary>
    private string MakeSession(string name, double? finalizedDaysAgo)
    {
        string folder = Path.Combine(_workingRoot, name);
        using (SessionJournal journal = SessionJournal.CreateNew(folder, new SessionStarted
        {
            TimestampUtc = Now.AddDays(-30),
            SessionId = Guid.NewGuid(),
            LocalTimeZoneId = "UTC",
            MachineName = "M",
            UserName = "U",
            AppVersion = "t",
            FfmpegBuildId = "t",
            Displays = [],
            CanvasWidth = 1,
            CanvasHeight = 1,
            FrameRate = 15,
            EncoderName = "e",
            Quality = "balanced",
            SpeedPreset = "veryfast",
            EncoderArguments = [],
            WorkingFolder = folder,
        }))
        {
            if (finalizedDaysAgo is { } days)
            {
                journal.Append(new SessionFinalized
                {
                    TimestampUtc = Now.AddDays(-days),
                    OutputFiles = [Path.Combine(folder, "output.mkv")],
                    TotalSpan = TimeSpan.FromMinutes(10),
                    RecordedSpan = TimeSpan.FromMinutes(10),
                    GapCount = 0,
                    ReconciliationNote = null,
                });
            }
        }

        File.WriteAllText(Path.Combine(folder, "output.mkv"), "video bytes");
        return folder;
    }

    [Fact]
    public void A_transferred_verified_session_past_retention_is_removed()
    {
        string folder = MakeSession("old-transferred", finalizedDaysAgo: 30);
        long id = _queue.Enqueue(Path.Combine(folder, "output.mkv"), "archive");
        _queue.Complete(id);

        MakeCleaner().Clean(Now).ShouldBe([folder]);

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void A_session_inside_the_retention_period_is_kept()
    {
        string folder = MakeSession("recent-transferred", finalizedDaysAgo: 3);
        long id = _queue.Enqueue(Path.Combine(folder, "output.mkv"), "archive");
        _queue.Complete(id);

        MakeCleaner().Clean(Now).ShouldBeEmpty();

        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public void A_session_that_was_never_transferred_anywhere_is_NEVER_removed()
    {
        // The local copy is the only copy — deleting it would destroy the
        // recording, which outranks reclaiming disk (design priority #1).
        string folder = MakeSession("old-but-local-only", finalizedDaysAgo: 365);

        MakeCleaner().Clean(Now).ShouldBeEmpty();

        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public void A_session_with_any_incomplete_transfer_is_kept()
    {
        string folder = MakeSession("old-partly-transferred", finalizedDaysAgo: 30);
        long completed = _queue.Enqueue(Path.Combine(folder, "output.mkv"), "archive");
        _queue.Complete(completed);
        long failed = _queue.Enqueue(Path.Combine(folder, "output.mkv"), "sharepoint");
        _queue.Fail(failed, FailureKind.Permanent, "denied", Now, null);

        MakeCleaner().Clean(Now).ShouldBeEmpty();

        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public void An_unfinalised_session_is_left_for_recovery()
    {
        string folder = MakeSession("interrupted", finalizedDaysAgo: null);
        long id = _queue.Enqueue(Path.Combine(folder, "output.mkv"), "archive");
        _queue.Complete(id);

        MakeCleaner().Clean(Now).ShouldBeEmpty();

        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_that_is_not_a_session_is_ignored()
    {
        string stray = Path.Combine(_workingRoot, "not-a-session");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "notes.txt"), "mine");

        MakeCleaner().Clean(Now).ShouldBeEmpty();

        Directory.Exists(stray).ShouldBeTrue();
    }

    [Fact]
    public void Zero_retention_days_still_requires_a_completed_transfer()
    {
        _settingsStore.Save(_settingsStore.Load() with { RetentionDays = 0 });
        string untransferred = MakeSession("zero-retention-untransferred", finalizedDaysAgo: 1);

        MakeCleaner().Clean(Now).ShouldBeEmpty();

        Directory.Exists(untransferred).ShouldBeTrue();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
