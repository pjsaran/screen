using Serilog;

namespace Captr.Core.Sessions;

/// <summary>
/// On every host start, finds sessions that were never finalised — a journal with no
/// <c>SessionFinalized</c> event — and runs the finalisation pipeline on each,
/// automatically and without prompting (SPEC §6). Owns the scan and the recovery
/// report; if it misses a session, footage sits unfinalised and undelivered forever.
/// </summary>
public sealed class RecoveryScanner
{
    private readonly FinalizationPipeline _pipeline;
    private readonly ILogger _log;

    public RecoveryScanner(FinalizationPipeline pipeline, ILogger log)
    {
        _pipeline = pipeline;
        _log = log.ForContext<RecoveryScanner>();
    }

    /// <summary>
    /// Scans every session folder under the working root and recovers what needs it.
    /// Returns one report per recovered session (empty = nothing needed recovery).
    /// A session that fails to recover is reported and left untouched for manual
    /// inspection — recovery must never make things worse.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryReport>> ScanAndRecoverAsync(
        string workingRoot, CancellationToken cancellationToken)
    {
        var reports = new List<RecoveryReport>();
        if (!Directory.Exists(workingRoot))
        {
            return reports;
        }

        foreach (string sessionFolder in Directory.GetDirectories(workingRoot))
        {
            string journalPath = Path.Combine(sessionFolder, SessionJournal.FileName);
            if (!File.Exists(journalPath))
            {
                continue; // Not a session folder.
            }

            IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(journalPath);
            if (events.Count == 0 || events.OfType<SessionFinalized>().Any())
            {
                continue; // Unreadable or already finalised.
            }

            _log.Information("Recovering interrupted session in {Folder}", sessionFolder);
            try
            {
                FinalizationResult result = await _pipeline.RunAsync(sessionFolder, cancellationToken).ConfigureAwait(false);
                reports.Add(RecoveryReport.FromResult(sessionFolder, result));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Recovery of one session must never block recovery of the others,
                // and must never destroy anything — report and move on (SPEC §12's
                // catch-record-continue is correct here).
                _log.Error(exception, "Recovery failed for {Folder}; working files left untouched", sessionFolder);
                reports.Add(RecoveryReport.Failure(sessionFolder, exception.Message));
            }
        }

        return reports;
    }
}

/// <summary>What recovery did for one session — duration, segments, repairs, and
/// time lost — reported to the user, never silent (SPEC §6).</summary>
public sealed record RecoveryReport(
    string SessionFolder,
    bool Succeeded,
    string? FailureReason,
    TimeSpan RecoveredDuration,
    int SegmentCount,
    int RepairedSegments,
    TimeSpan TimeLost)
{
    public static RecoveryReport FromResult(string folder, FinalizationResult result) => new(
        folder,
        Succeeded: true,
        FailureReason: null,
        RecoveredDuration: result.Coverage.RecordedSpan,
        SegmentCount: result.SegmentCount,
        RepairedSegments: result.RepairedSegments,
        TimeLost: result.Coverage.TotalSpan - result.Coverage.RecordedSpan);

    public static RecoveryReport Failure(string folder, string reason) =>
        new(folder, false, reason, TimeSpan.Zero, 0, 0, TimeSpan.Zero);
}
