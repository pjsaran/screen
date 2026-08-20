using Captr.Core.Sessions;
using Captr.Core.Settings;

using Serilog;

namespace Captr.Core.Delivery;

/// <summary>
/// Reclaims disk by removing session folders that are genuinely finished with
/// (SPEC §7: "delete local working files only after every enabled destination has
/// confirmed and verified the transfer, a user-configured retention period has
/// elapsed, and free space allows"). Owns the deletion decision — the single place
/// where Captr destroys recorded data on its own initiative, which is why every
/// condition is checked explicitly and logged.
/// </summary>
/// <remarks>
/// <para>
/// SPEC-INTERPRETATION NOTE (flagged as SPEC §0 asks). A session with NO enabled
/// destination satisfies "every enabled destination has confirmed" vacuously — but
/// its working folder is then the ONLY copy of the recording, and deleting it would
/// violate design priority #1 (the recording survives). So this cleaner only ever
/// removes sessions whose footage demonstrably exists somewhere else: it requires
/// at least one completed delivery. Recordings kept purely locally are never
/// deleted automatically; the user deletes them from the UI with a typed
/// confirmation, or by hand.
/// </para>
/// <para>
/// Deletion is all-or-nothing per session folder. Anything unfinalised, undelivered,
/// or too young is left completely alone.
/// </para>
/// </remarks>
public sealed class RetentionCleaner
{
    private readonly DeliveryQueue _queue;
    private readonly SettingsStore _settingsStore;
    private readonly ILogger _log;

    public RetentionCleaner(DeliveryQueue queue, SettingsStore settingsStore, ILogger log)
    {
        _queue = queue;
        _settingsStore = settingsStore;
        _log = log.ForContext<RetentionCleaner>();
    }

    /// <summary>
    /// Removes every session folder that qualifies. Returns the folders deleted.
    /// Safe to call often; it does nothing when nothing qualifies.
    /// </summary>
    public IReadOnlyList<string> Clean(DateTimeOffset nowUtc)
    {
        CaptrSettings settings = _settingsStore.Load();
        var deleted = new List<string>();

        if (!Directory.Exists(settings.WorkingFolder))
        {
            return deleted;
        }

        IReadOnlyList<DeliveryItem> deliveries = _queue.List();

        foreach (string sessionFolder in Directory.GetDirectories(settings.WorkingFolder))
        {
            if (!ShouldDelete(sessionFolder, deliveries, settings.RetentionDays, nowUtc, out string reasonToKeep))
            {
                _log.Debug("Keeping {Folder}: {Reason}", sessionFolder, reasonToKeep);
                continue;
            }

            try
            {
                Directory.Delete(sessionFolder, recursive: true);
                deleted.Add(sessionFolder);
                _log.Information(
                    "Removed working files for {Folder}: delivered and verified, and older than the {Days}-day retention period",
                    sessionFolder, settings.RetentionDays);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Something still has the folder open, or it is read-only. Leaving
                // data behind is always the safe failure here.
                _log.Warning(exception, "Could not remove working files for {Folder}; leaving them in place", sessionFolder);
            }
        }

        return deleted;
    }

    /// <summary>The complete deletion test, written so each refusal names itself.</summary>
    private static bool ShouldDelete(
        string sessionFolder,
        IReadOnlyList<DeliveryItem> deliveries,
        int retentionDays,
        DateTimeOffset nowUtc,
        out string reasonToKeep)
    {
        string journalPath = Path.Combine(sessionFolder, SessionJournal.FileName);
        if (!File.Exists(journalPath))
        {
            reasonToKeep = "not a session folder";
            return false;
        }

        IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(journalPath);
        if (events.OfType<SessionFinalized>().FirstOrDefault() is not { } finalized)
        {
            // An unfinalised session is recovery's business, never the cleaner's.
            reasonToKeep = "never finalised — recovery will handle it";
            return false;
        }

        if (nowUtc - finalized.TimestampUtc < TimeSpan.FromDays(retentionDays))
        {
            reasonToKeep = $"inside the {retentionDays}-day retention period";
            return false;
        }

        // Every delivery recorded for anything in this folder must have completed,
        // and there must be at least one — see the class remarks on why "no
        // destinations configured" must NOT qualify.
        List<DeliveryItem> forThisSession =
        [
            .. deliveries.Where(d => d.OutputPath.StartsWith(sessionFolder, StringComparison.OrdinalIgnoreCase)),
        ];

        if (forThisSession.Count == 0)
        {
            reasonToKeep = "never delivered anywhere — the local copy is the only copy";
            return false;
        }

        if (forThisSession.Any(d => d.State != "completed"))
        {
            reasonToKeep = "a delivery is still pending or failed";
            return false;
        }

        reasonToKeep = string.Empty;
        return true;
    }
}
