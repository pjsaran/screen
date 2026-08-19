using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Captr.Core.Interop;

/// <summary>
/// The Win32 global-hotkey calls, wrapped so the UI never touches P/Invoke directly
/// (SPEC §12: platform invocation confined to one place). Owns nothing but the
/// translation; registration bookkeeping lives with the caller.
/// </summary>
public static class GlobalHotkeys
{
    /// <summary>Modifier flags mirroring the Win32 MOD_* values.</summary>
    [Flags]
    public enum Modifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Windows = 0x0008,
    }

    /// <summary>Registers a global hotkey; false when another application owns the
    /// combination (the caller reports the conflict in plain language).</summary>
    public static bool Register(nint windowHandle, int id, Modifiers modifiers, uint virtualKey) =>
        PInvoke.RegisterHotKey(
            (HWND)windowHandle, id,
            (HOT_KEY_MODIFIERS)modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, virtualKey);

    /// <summary>Releases a registration.</summary>
    public static bool Unregister(nint windowHandle, int id) =>
        PInvoke.UnregisterHotKey((HWND)windowHandle, id);
}
