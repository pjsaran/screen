using Windows.Win32;
using Windows.Win32.Foundation;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// Shows Windows (and the user, on the shutdown screen) WHY shutdown is briefly
/// held: "Captr is finishing a recording". SPEC §6: register a shutdown block
/// reason, finalise quickly, then release it. Owns the register/unregister pair;
/// leaking a registration would leave a ghost entry on every shutdown screen.
/// </summary>
public sealed class ShutdownBlock : IDisposable
{
    private readonly nint _hwnd;
    private bool _released;

    /// <param name="windowHandle">The host's message window — the reason is
    /// attached to a window, which is why the message-only window exposes its
    /// handle.</param>
    public ShutdownBlock(nint windowHandle, string reason)
    {
        _hwnd = windowHandle;
        PInvoke.ShutdownBlockReasonCreate((HWND)_hwnd, reason);
    }

    public void Dispose()
    {
        if (!_released)
        {
            _released = true;
            PInvoke.ShutdownBlockReasonDestroy((HWND)_hwnd);
        }
    }
}
