using Captr.Core.Sessions;
using Captr.Core.Supervision;

namespace Captr.Integration.Tests.Supervision;

/// <summary>
/// Shared plumbing for supervision tests against the real FFmpeg: a temp working
/// folder, a real journal, and lavfi (synthetic test pattern) argument vectors that
/// exercise the exact segment/progress machinery without needing a desktop or GPU —
/// which is what makes these tests CI-safe (trait Ffmpeg).
/// </summary>
public sealed class SupervisionTestSession : IDisposable
{
    public string WorkingFolder { get; }

    public string FfmpegPath { get; }

    public SessionJournal Journal { get; }

    /// <param name="parentFolder">When given, the session folder is created inside
    /// it — lets recovery-scan tests point the scanner at the parent.</param>
    public SupervisionTestSession(string? parentFolder = null)
    {
        WorkingFolder = parentFolder is null
            ? Directory.CreateTempSubdirectory("captr-supervision-").FullName
            : Directory.CreateDirectory(
                Path.Combine(parentFolder, "session-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        try
        {
            FfmpegPath = FfmpegLocator.FindFfmpeg();
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                "Integration tests need the pinned FFmpeg. Run: pwsh build/fetch-ffmpeg.ps1",
                exception);
        }

        Journal = SessionJournal.CreateNew(WorkingFolder, new SessionStarted
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid(),
            LocalTimeZoneId = TimeZoneInfo.Local.Id,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            AppVersion = "test",
            FfmpegBuildId = "test",
            Displays = [],
            CanvasWidth = 320,
            CanvasHeight = 180,
            FrameRate = 10,
            EncoderName = "libopenh264",
            Quality = "balanced",
            SpeedPreset = "veryfast",
            EncoderArguments = [],
            WorkingFolder = WorkingFolder,
        });
    }

    /// <summary>Real-time synthetic 10 fps input with 2-second segments — the same
    /// segment/keyframe/progress mechanics as a desktop capture, minus the desktop.</summary>
    public List<string> LavfiArguments(int segmentSeconds = 2) =>
    [
        "-hide_banner", "-nostats", "-loglevel", "warning",
        "-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10",
        "-c:v", "libopenh264", "-b:v", "1M",
        "-force_key_frames", $"expr:gte(t,n_forced*{segmentSeconds})",
        "-g", "1000",
        "-progress", Path.Combine(WorkingFolder, Captr.Core.Encoders.EncodingConstants.ProgressFileName),
        "-f", "segment", "-segment_format", "matroska",
        "-segment_time", segmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-reset_timestamps", "1", "-strftime", "1",
        Path.Combine(WorkingFolder, "seg-g01-%Y%m%d-%H%M%S.mkv"),
    ];

    /// <summary>Arguments that fail instantly (missing input) — a reliable synthetic
    /// encoder FAULT with error lines in the log.</summary>
    public List<string> InstantFaultArguments() =>
    [
        "-hide_banner", "-nostats", "-loglevel", "warning",
        "-i", Path.Combine(WorkingFolder, "does-not-exist.mp4"),
        "-c:v", "libopenh264",
        "-progress", Path.Combine(WorkingFolder, Captr.Core.Encoders.EncodingConstants.ProgressFileName),
        "-f", "matroska", Path.Combine(WorkingFolder, "out.mkv"),
    ];

    /// <summary>
    /// Arguments that fail instantly AND leave a real gdigrab capture-loss line in
    /// FFmpeg's own report file — because the missing input is NAMED after one.
    /// </summary>
    /// <remarks>
    /// The trick is deliberate. What broke in production was not the classifier's
    /// logic but its vocabulary, and every unit test bypassed the log file entirely
    /// by handing the classifier a string. This makes FFmpeg itself write the line,
    /// so the assertion covers the whole chain: report file → tail reader → ring
    /// buffer → per-process slice → classification → backoff → relaunch.
    /// </remarks>
    public List<string> CaptureLossArguments() =>
    [
        "-hide_banner", "-nostats", "-loglevel", "warning",
        "-i", Path.Combine(WorkingFolder, "Failed to capture image (error 6).mp4"),
        "-c:v", "libopenh264",
        "-progress", Path.Combine(WorkingFolder, Captr.Core.Encoders.EncodingConstants.ProgressFileName),
        "-f", "matroska", Path.Combine(WorkingFolder, "out.mkv"),
    ];

    public IReadOnlyList<JournalEvent> ReadJournal() =>
        SessionJournal.ReadAll(Path.Combine(WorkingFolder, SessionJournal.FileName));

    public void Dispose()
    {
        Journal.Dispose();
        try
        {
            Directory.Delete(WorkingFolder, recursive: true);
        }
        catch (IOException)
        {
            // A straggling ffmpeg may still hold a segment briefly; temp cleanup
            // is best-effort in tests.
        }
    }
}
