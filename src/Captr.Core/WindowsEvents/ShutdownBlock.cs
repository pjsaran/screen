using Windows.Win32;
using Windows.Win32.Foundation;

namespace Captr.Core.WindowsEvents;

/// <summary>
/// Shows Windows (and the user, on the shutdown screen) WHY shutdown is briefly
/// held: "Captr is finishing a recording". SPEC §6: register a shutdown block
/// reason, finalise quickly, then release it. Owns the register/unregister pair;
/// leaking a registration would leave a ghost entry on every shutdown screen.
/// </summary>
/// <remarks>
/// Both halves must run on the event window's own thread — the API silently does
/// nothing from any other. Creation happens there naturally (Windows' "may I shut
/// down?" message is what asks for the block); release is posted there, because the
/// recording finishes on whichever thread finalisation happened to use. It used to be
/// called from that thread directly, failed without a word, and left Captr named on
/// the shutdown screen for as long as the host lived.
/// </remarks>
public sealed class ShutdownBlock : IDisposable
{
    private readonly SystemEventWindow _window;
    private int _released;

    /// <summary>Registers the reason and makes the window answer "not yet". Call on
    /// the window's thread — from its <see cref="SystemEventWindow.EndSessionRequested"/>
    /// handler.</summary>
    public ShutdownBlock(SystemEventWindow window, string reason)
    {
        _window = window;
        PInvoke.ShutdownBlockReasonCreate((HWND)window.Handle, reason);
        window.ShutdownBlocked = true;
    }

    /// <summary>Lets the shutdown continue: clears the flag first, so a repeat query
    /// is answered "yes", then removes the reason. Safe from any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        nint handle = _window.Handle;
        _window.Post(() =>
        {
            _window.ShutdownBlocked = false;
            if (handle != 0)
            {
                PInvoke.ShutdownBlockReasonDestroy((HWND)handle);
            }
        });
    }
}
