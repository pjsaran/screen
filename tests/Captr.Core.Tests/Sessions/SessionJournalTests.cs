using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Core.Tests.Sessions;

public class SessionJournalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-journal-").FullName;

    private static SessionStarted MakeStart(Guid? id = null) => new()
    {
        TimestampUtc = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero),
        SessionId = id ?? Guid.NewGuid(),
        LocalTimeZoneId = "Pacific Standard Time",
        MachineName = "TESTBOX",
        UserName = "tester",
        AppVersion = "0.1.0",
        FfmpegBuildId = "ffmpeg-test",
        Displays = [new RecordedDisplay { StableId = @"\\?\DISPLAY#TST0001#instance", WindowsDisplayNumber = 3, Width = 1920, Height = 1080 }],
        CanvasWidth = 1920,
        CanvasHeight = 1080,
        FrameRate = 15,
        EncoderName = "hevc_nvenc",
        QualityPreset = "sharp-text",
        EncoderArguments = ["-f", "lavfi", "-i", "testsrc2"],
        WorkingFolder = @"C:\work",
    };

    private string JournalPath => Path.Combine(_dir, SessionJournal.FileName);

    [Fact]
    public void Every_event_kind_round_trips_through_the_journal()
    {
        var now = new DateTimeOffset(2026, 8, 19, 9, 1, 0, TimeSpan.Zero);
        var written = new List<JournalEvent>
        {
            new SegmentOpened { TimestampUtc = now, FileName = "seg-001.mkv", ArrangementGroup = 1 },
            new SegmentClosed { TimestampUtc = now, FileName = "seg-001.mkv", SizeBytes = 123, Sha256 = "ab" },
            new GapRecorded { TimestampUtc = now, GapStartUtc = now, Duration = TimeSpan.FromSeconds(3), Reason = "encoder restart" },
            new StopRequested { TimestampUtc = now, Reason = "user" },
            new EncoderRestarted { TimestampUtc = now, Reason = "stall", CountsTowardFallback = true, LogTail = ["line1", "line2"] },
            new EncoderFellBack { TimestampUtc = now, FromEncoder = "hevc_nvenc", ToEncoder = "libopenh264" },
            new FrameRateReduced { TimestampUtc = now, FromFps = 15, ToFps = 10, Reason = "sustained slow encode" },
            new PauseStarted { TimestampUtc = now },
            new PauseEnded { TimestampUtc = now },
            new TopologyChanged { TimestampUtc = now, NewDisplays = [], NewArrangementGroup = 2 },
            new ClockJumped { TimestampUtc = now, ApparentJump = TimeSpan.FromMinutes(60) },
            new EncoderProcessLaunched { TimestampUtc = now, ProcessId = 4242, ProcessStartTimeUtc = now, ImagePath = @"C:\ffmpeg.exe" },
            new SessionFinalized { TimestampUtc = now, OutputFiles = ["out.mkv"], TotalSpan = TimeSpan.FromMinutes(10), RecordedSpan = TimeSpan.FromMinutes(9), GapCount = 1, ReconciliationNote = null },
        };

        using (var journal = SessionJournal.CreateNew(_dir, MakeStart()))
        {
            foreach (JournalEvent journalEvent in written)
            {
                journal.Append(journalEvent);
            }
        }

        IReadOnlyList<JournalEvent> read = SessionJournal.ReadAll(JournalPath);

        read.Count.ShouldBe(written.Count + 1);
        read[0].ShouldBeOfType<SessionStarted>();
        for (int i = 0; i < written.Count; i++)
        {
            // Record equality compares collection properties by reference, so
            // compare the serialized form — which is also what actually matters
            // for a round-trip through the journal file.
            AsJson(read[i + 1]).ShouldBe(AsJson(written[i]), $"event #{i} ({written[i].GetType().Name})");
        }
    }

    private static string AsJson(JournalEvent journalEvent) =>
        System.Text.Json.JsonSerializer.Serialize(journalEvent);

    [Fact]
    public void A_torn_final_line_is_tolerated_and_all_prior_events_survive()
    {
        using (var journal = SessionJournal.CreateNew(_dir, MakeStart()))
        {
            journal.Append(new PauseStarted { TimestampUtc = DateTimeOffset.UtcNow });
        }

        // Simulate a power cut mid-append: append half a JSON line with no newline.
        File.AppendAllText(JournalPath, """{"kind":"segment-closed","fileName":"seg""");

        IReadOnlyList<JournalEvent> read = SessionJournal.ReadAll(JournalPath);

        read.Count.ShouldBe(2);
        read[1].ShouldBeOfType<PauseStarted>();
    }

    [Fact]
    public void An_unknown_event_kind_is_skipped_not_fatal()
    {
        using (var journal = SessionJournal.CreateNew(_dir, MakeStart()))
        {
        }

        // An event kind from some future Captr version.
        File.AppendAllText(JournalPath, """{"kind":"quantum-entangled","timestampUtc":"2030-01-01T00:00:00+00:00"}""" + "\n");

        IReadOnlyList<JournalEvent> read = SessionJournal.ReadAll(JournalPath);

        read.Count.ShouldBe(1);
        read[0].ShouldBeOfType<SessionStarted>();
    }

    [Fact]
    public void OpenExisting_appends_after_previous_events()
    {
        var id = Guid.NewGuid();
        using (var journal = SessionJournal.CreateNew(_dir, MakeStart(id)))
        {
        }

        // A second host process (after a crash) reopens and continues the journal.
        using (var journal = SessionJournal.OpenExisting(_dir))
        {
            journal.Append(new SessionFinalized
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                OutputFiles = [],
                TotalSpan = TimeSpan.Zero,
                RecordedSpan = TimeSpan.Zero,
                GapCount = 0,
                ReconciliationNote = "recovered",
            });
        }

        IReadOnlyList<JournalEvent> read = SessionJournal.ReadAll(JournalPath);

        read.Count.ShouldBe(2);
        read[0].ShouldBeOfType<SessionStarted>().SessionId.ShouldBe(id);
        read[1].ShouldBeOfType<SessionFinalized>();
    }

    [Fact]
    public void The_journal_can_be_read_while_it_is_still_open_for_writing()
    {
        using var journal = SessionJournal.CreateNew(_dir, MakeStart());
        journal.Append(new PauseStarted { TimestampUtc = DateTimeOffset.UtcNow });

        // A status query must be able to read the journal of a live session.
        IReadOnlyList<JournalEvent> read = SessionJournal.ReadAll(JournalPath);

        read.Count.ShouldBe(2);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
