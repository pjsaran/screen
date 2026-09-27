namespace Captr.Core.Transfers;

/// <summary>
/// One line about the transfer queue for places that are not the Transfers page —
/// Home and the tray tooltip — or nothing when there is nothing to say.
/// </summary>
/// <remarks>
/// A transfer waiting for a new secret, or parked after SharePoint refused it, was
/// visible only to someone who opened the Transfers page. Recordings then sat on the
/// local disk, outside retention, while the person believed they had been archived.
/// </remarks>
public static class TransferDigest
{
    /// <summary>"2 transfers on their way · 1 needs attention (see Transfers)", or
    /// empty when everything has arrived or nothing was ever sent.</summary>
    public static string Describe(IEnumerable<TransferItem> items)
    {
        int moving = 0;
        int stuck = 0;
        foreach (TransferItem item in items)
        {
            switch (item.State)
            {
                case TransferQueue.StatePending or TransferQueue.StateInProgress:
                    moving++;
                    break;
                case TransferQueue.StatePausedAuth or TransferQueue.StateManualRetry:
                    stuck++;
                    break;
            }
        }

        var parts = new List<string>();
        if (moving > 0)
        {
            parts.Add(moving == 1 ? "1 transfer on its way" : $"{moving} transfers on their way");
        }

        if (stuck > 0)
        {
            parts.Add(stuck == 1 ? "1 transfer needs attention" : $"{stuck} transfers need attention");
        }

        return parts.Count == 0
            ? ""
            : string.Join(" · ", parts) + (stuck > 0 ? " — see Transfers" : "");
    }
}
