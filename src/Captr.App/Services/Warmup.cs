using Captr.Core.Settings;
using Captr.Core.Transfers;

namespace Captr.App.Services;

/// <summary>
/// Pays the one-off costs of opening Captr's data stores in the background at
/// startup, so no page ever pays them on the click that opens it.
/// </summary>
/// <remarks>
/// <para>
/// MEASURED: the first <see cref="TransferQueue"/> constructed in a process takes
/// about 440 ms, because that is when SQLite's native library is loaded and
/// initialised. Every one after it takes about 0 ms. That 440 ms used to land on
/// whichever page the user opened first — the list appeared, empty, and filled in
/// half a second later, which reads as "this application is slow".
/// </para>
/// <para>
/// Doing it here moves the cost to a moment when nobody is waiting: the window is
/// still being drawn and nothing is clickable yet. It is deliberately fire-and-forget
/// and swallows everything — a failure here must never stop the application starting,
/// because whatever failed will fail again in the page that actually needs it, where
/// it can be reported properly.
/// </para>
/// </remarks>
public static class Warmup
{
    /// <summary>Starts the warm-up. Returns immediately.</summary>
    public static void Begin() => Task.Run(() =>
    {
        try
        {
            // Loading settings pulls in the JSON serializer and the migration chain.
            _ = new SettingsStore().Load();

            // Constructing the queue is what loads and initialises native SQLite.
            _ = new TransferQueue();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SettingsValidationException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            // Nothing to do and nobody to tell: this is an optimisation, not a step.
        }
    });
}
