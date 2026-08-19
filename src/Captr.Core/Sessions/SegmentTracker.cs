namespace Captr.Core.Sessions;

/// <summary>
/// Watches a session's working folder and journals the life of every segment file:
/// <see cref="SegmentOpened"/> when the encoder starts a new one,
/// <see cref="SegmentClosed"/> — with size and SHA-256 — once it is complete
/// (SPEC §6: "hash each segment as it closes"). Owns segment bookkeeping so the
/// supervisor can stay ignorant of files and finalisation can trust the journal.
/// </summary>
public sealed class SegmentTracker
{
    private readonly string _workingFolder;
    private readonly SessionJournal _journal;
    private readonly Func<int> _currentArrangementGroup;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private string? _openSegment;

    public SegmentTracker(string workingFolder, SessionJournal journal, Func<int> currentArrangementGroup)
    {
        _workingFolder = workingFolder;
        _journal = journal;
        _currentArrangementGroup = currentArrangementGroup;
    }

    /// <summary>Polls until cancelled, then closes out the final segment.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // The session is stopping: one final sweep, then close the last segment —
        // the encoder has already exited, so the file is as complete as it gets.
        await PollOnceAsync(CancellationToken.None).ConfigureAwait(false);
        if (_openSegment is not null)
        {
            await CloseSegmentAsync(_openSegment, CancellationToken.None).ConfigureAwait(false);
            _openSegment = null;
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        string[] segmentFiles;
        try
        {
            segmentFiles = Directory.GetFiles(_workingFolder, "seg-*.mkv");
        }
        catch (DirectoryNotFoundException)
        {
            return; // Working folder vanished mid-session — the disk guard's problem.
        }

        foreach (string path in segmentFiles.Order(StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.Contains(".repaired.", StringComparison.OrdinalIgnoreCase) || !_seen.Add(fileName))
            {
                continue;
            }

            // A new file means the previous one is closed: hash and journal it.
            if (_openSegment is not null)
            {
                await CloseSegmentAsync(_openSegment, cancellationToken).ConfigureAwait(false);
            }

            _journal.Append(new SegmentOpened
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                FileName = fileName,
                ArrangementGroup = _currentArrangementGroup(),
            });
            _openSegment = fileName;
        }
    }

    private async Task CloseSegmentAsync(string fileName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_workingFolder, fileName);
        try
        {
            _journal.Append(new SegmentClosed
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                FileName = fileName,
                SizeBytes = new FileInfo(path).Length,
                Sha256 = await FinalizationPipeline.HashFileAsync(path, cancellationToken).ConfigureAwait(false),
            });
        }
        catch (IOException)
        {
            // Still locked or already gone — finalisation re-probes everything
            // anyway; a missing SegmentClosed is survivable, a crashed tracker not.
        }
    }
}
