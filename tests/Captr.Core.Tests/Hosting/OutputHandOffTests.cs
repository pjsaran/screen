using Captr.Core.Hosting;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Settings.Migrations;
using Captr.Core.Transfers;

using Serilog.Core;

using Shouldly;

namespace Captr.Core.Tests.Hosting;

/// <summary>
/// After finalisation every output is named and queued for every enabled
/// destination — and a problem naming one output never costs its transfers.
/// </summary>
/// <remarks>
/// The whole hand-off used to sit in one try block: a single rename failure (an
/// over-long label, a file held open by a scanner) left every output under its
/// intermediate name and queued NOTHING anywhere, with one log line to show for it.
/// </remarks>
public sealed class OutputHandOffTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-handoff-").FullName;
    private readonly SettingsStore _settings;
    private readonly TransferQueue _queue;

    public OutputHandOffTests()
    {
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), SettingsMigrator.Default);
        _settings.Save(CaptrSettings.CreateDefault() with
        {
            WorkingFolder = _dir,
            Destinations =
            [
                new DestinationSettings { Name = "archive", Kind = DestinationKind.Folder, FolderPath = Path.Combine(_dir, "a") },
                new DestinationSettings { Name = "backup", Kind = DestinationKind.Folder, FolderPath = Path.Combine(_dir, "b") },
            ],
        });
        _queue = new TransferQueue(Path.Combine(_dir, "transfers.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private HostService Host() => new(_settings, null, Logger.None, _queue, null,
        (_, _, _) => throw new InvalidOperationException("no recording in this test"));

    private FinalizationResult Finalised(string output, string? label = null)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var session = new SessionStarted
        {
            TimestampUtc = start,
            SessionId = Guid.NewGuid(),
            LocalTimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = "PC",
            UserName = "me",
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
            WorkingFolder = _dir,
            Label = label,
        };
        var coverage = new CoverageReport(session.SessionId, start, start.AddMinutes(5),
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), []);
        return new FinalizationResult(session, [output], coverage, 1, 0, []);
    }

    private string Output()
    {
        string path = Path.Combine(_dir, "joined-g01.mkv");
        File.WriteAllText(path, "footage");
        return path;
    }

    [Fact]
    public void A_file_that_cannot_be_renamed_is_still_queued_for_every_destination()
    {
        string output = Output();

        // Something else has the file open without allowing a rename.
        using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Host().HandleFinalized(Finalised(output));
        }

        IReadOnlyList<TransferItem> queued = _queue.List();
        queued.Select(item => item.DestinationName).Order().ShouldBe(["archive", "backup"]);
        queued.ShouldAllBe(item => item.OutputPath == output, "it keeps its working name, and still goes");
    }

    [Fact]
    public void A_label_too_long_for_a_file_name_is_shortened_and_the_recording_still_goes()
    {
        _settings.Save(_settings.Load() with { OutputPattern = "{label} {date}" });
        string output = Output();

        Host().HandleFinalized(Finalised(output, label: new string('x', 400)));

        IReadOnlyList<TransferItem> queued = _queue.List();
        queued.Count.ShouldBe(2);
        File.Exists(output).ShouldBeFalse("it was renamed by the pattern");
        File.Exists(queued[0].OutputPath).ShouldBeTrue();
    }
}
