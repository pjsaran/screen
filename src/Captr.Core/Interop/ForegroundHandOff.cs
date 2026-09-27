using Windows.Win32;

namespace Captr.Core.Interop;

/// <summary>
/// Lets another process bring its window to the front. Owns the one call a second
/// launch of the window needs before it asks the first to show itself.
/// </summary>
/// <remarks>
/// Windows lets only the process the user is interacting with take the foreground.
/// The second launch IS that process, the first is not, so the first's Activate()
/// usually just flashed its taskbar button - "I clicked Captr and nothing happened".
/// Passing the permission on first makes the window actually come forward.
/// </remarks>
public static class ForegroundHandOff
{
    /// <summary>Allows any process to set the foreground window (until the next
    /// input); false when Windows refused, which only costs the flash above.</summary>
    public static bool AllowAnyProcess() => PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
}
