using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.System.StationsAndDesktops;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// Whether this process can see a desktop to record. Owns the refusal that stops a
/// recording in session 0 — the place a scheduled task set to "Run whether user is
/// logged on or not", or a service, runs — from starting at all.
/// </summary>
/// <remarks>
/// Desktop Duplication cannot capture from session 0 or from a non-interactive
/// window station, and nothing there fails loudly: the capture starts, the files
/// grow, and every frame is black. The user guide warned about it, and Captr used to
/// record anyway and report success. Refusing at start turns a lost day into a clear
/// error in the task's history.
/// </remarks>
public static class InteractiveSession
{
    /// <summary>
    /// Test hook for the end-to-end suite, which cannot run itself in session 0:
    /// when set to <c>1</c>, the check behaves as if it were there. It can only ever
    /// cause a refusal, never permit anything.
    /// </summary>
    public const string SimulateSession0Variable = "CAPTR_SIMULATE_SESSION0";

    /// <summary>Null when a desktop can be captured from here; otherwise the reason,
    /// worded for the person who set up the scheduled task.</summary>
    public static string? WhyCaptureIsImpossible()
    {
        if (Environment.GetEnvironmentVariable(SimulateSession0Variable) == "1")
        {
            return Evaluate(0, "WinSta0");
        }

        using var current = Process.GetCurrentProcess();
        return Evaluate(current.SessionId, WindowStationName());
    }

    /// <summary>The decision itself, separated from reading the machine so it can be
    /// tested for every case.</summary>
    internal static string? Evaluate(int sessionId, string? windowStation)
    {
        if (sessionId == 0)
        {
            return "Captr cannot record here: it is running in session 0 — a scheduled task set to \"Run whether " +
                   "user is logged on or not\", or a service — where there is no desktop to capture, so every frame " +
                   "would be black. Set the task to \"Run only when user is logged on\" (see Scheduling in the user guide).";
        }

        if (windowStation is not null && !string.Equals(windowStation, "WinSta0", StringComparison.OrdinalIgnoreCase))
        {
            return $"Captr cannot record here: it is running on the non-interactive window station '{windowStation}', " +
                   "which has no visible desktop, so every frame would be black. Run it as the signed-in user " +
                   "(for a scheduled task: \"Run only when user is logged on\").";
        }

        return null;
    }

    private static unsafe string? WindowStationName()
    {
        HWINSTA station = PInvoke.GetProcessWindowStation();
        if (station.IsNull)
        {
            return null;
        }

        char* buffer = stackalloc char[256];
        uint needed;
        return PInvoke.GetUserObjectInformation(
                new Windows.Win32.Foundation.HANDLE(station.Value), USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                buffer, 256 * sizeof(char), &needed)
            ? new string(buffer)
            : null;
    }
}
