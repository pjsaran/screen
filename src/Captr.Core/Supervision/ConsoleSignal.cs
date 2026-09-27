using Windows.Win32;
using Windows.Win32.System.Console;

namespace Captr.Core.Supervision;

/// <summary>
/// Sends Ctrl+C to a console process this one did not start — how a new host asks
/// an ORPHANED encoder to stop cleanly.
/// </summary>
/// <remarks>
/// <para>
/// A host that launched FFmpeg stops it with <c>'q'</c> on its stdin. A host that
/// adopted it after a crash cannot: that pipe belonged to the dead host. It used to
/// kill the orphan outright — and a hard-killed FFmpeg leaves its segment without
/// its closing structure and loses everything still in its output buffer, which at
/// the low bitrates of a quiet screen is tens of seconds. Seen in testing: an orphan
/// recording for 45 s recovered as 11 s, and on another run as nothing at all.
/// </para>
/// <para>
/// FFmpeg runs as a console program with no console WINDOW, but it has a console,
/// and Ctrl+C on that console makes it finish exactly as 'q' does. A process can
/// attach to another's console only when it has none of its own — true of the
/// recording host, which is a windowed program. From anywhere else (a test runner in
/// a terminal) this reports false and the caller falls back to ending the process.
/// </para>
/// </remarks>
internal static class ConsoleSignal
{
    private static readonly Lock Gate = new();

    /// <summary>True when Ctrl+C was delivered to the console of <paramref name="processId"/>.</summary>
    public static bool TrySendCtrlC(int processId)
    {
        // Attaching is process-wide, so one at a time.
        lock (Gate)
        {
            if (!PInvoke.GetConsoleWindow().IsNull || !PInvoke.AttachConsole((uint)processId))
            {
                return false;
            }

            try
            {
                // Ignore the Ctrl+C in THIS process while attached — it is broadcast to
                // every process on the console, and that briefly includes us.
                PInvoke.SetConsoleCtrlHandler(null, true);
                return PInvoke.GenerateConsoleCtrlEvent(PInvoke.CTRL_C_EVENT, 0);
            }
            finally
            {
                PInvoke.FreeConsole();

                // The event is delivered asynchronously; keep ignoring it a moment
                // longer than it can take to arrive, then restore normal handling.
                Thread.Sleep(100);
                PInvoke.SetConsoleCtrlHandler(null, false);
            }
        }
    }
}
