using System.Windows.Input;
using System.Windows.Interop;

using Captr.Core.Interop;

namespace Captr.App.Services;

/// <summary>
/// Global hotkeys for start/pause/stop (SPEC §9). Owns registration against the
/// main window and the conflict message when another application already owns a
/// combination — registration failure is reported in plain language, never silent.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly nint _windowHandle;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<string> _conflicts = [];
    private int _nextId = 0xC000;

    public HotkeyManager(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.EnsureHandle();
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowHook);
    }

    /// <summary>Combinations that could not be registered, with the reason.</summary>
    public IReadOnlyList<string> Conflicts => _conflicts;

    /// <summary>Registers one combination ("Ctrl+Alt+F9"); empty string = disabled.
    /// A failure is recorded in <see cref="Conflicts"/> rather than thrown.</summary>
    public void Register(string combination, string purpose, Action action)
    {
        if (string.IsNullOrWhiteSpace(combination))
        {
            return;
        }

        if (!TryParse(combination, out GlobalHotkeys.Modifiers modifiers, out uint virtualKey))
        {
            _conflicts.Add($"'{combination}' ({purpose}) is not a valid combination. Use e.g. Ctrl+Alt+F9.");
            return;
        }

        int id = _nextId++;
        if (GlobalHotkeys.Register(_windowHandle, id, modifiers, virtualKey))
        {
            _actions[id] = action;
        }
        else
        {
            _conflicts.Add(
                $"'{combination}' ({purpose}) is already in use by another application. " +
                "Choose a different combination in settings.");
        }
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (message == WmHotkey && _actions.TryGetValue((int)wParam, out Action? action))
        {
            action();
            handled = true;
        }

        return 0;
    }

    /// <summary>"Ctrl+Alt+F9" → modifier flags + virtual key.</summary>
    internal static bool TryParse(string combination, out GlobalHotkeys.Modifiers modifiers, out uint virtualKey)
    {
        modifiers = GlobalHotkeys.Modifiers.None;
        virtualKey = 0;

        string[] parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (string part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= GlobalHotkeys.Modifiers.Control;
                    break;
                case "ALT":
                    modifiers |= GlobalHotkeys.Modifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= GlobalHotkeys.Modifiers.Shift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= GlobalHotkeys.Modifiers.Windows;
                    break;
                default:
                    return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out Key key))
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0 && modifiers != GlobalHotkeys.Modifiers.None;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys)
        {
            GlobalHotkeys.Unregister(_windowHandle, id);
        }

        _actions.Clear();
    }
}
