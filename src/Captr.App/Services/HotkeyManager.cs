using System.Windows.Interop;

using Captr.Core.Interop;
using Captr.Core.Settings;

namespace Captr.App.Services;

/// <summary>
/// Global hotkeys (SPEC §9). Owns registration against the main window and the
/// conflict message when another application already owns a combination —
/// registration failure is reported in plain language, never silent. What each
/// combination DOES is decided by the caller; Captr registers two toggles
/// (start/stop and pause/resume).
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration can be redone at any time</b> (<see cref="Apply"/>). The hotkeys
/// used to be registered once, at start-up, so changing them on the Settings page and
/// pressing Save did nothing at all until Captr was quit and reopened — and nothing
/// said so. The settings page now re-applies them after every successful save and
/// shows the outcome beside the hotkey boxes.
/// </para>
/// <para>
/// What counts as a valid combination is <see cref="HotkeyCombination"/>'s business,
/// shared with the settings validator and the CLI. This class only turns a valid one
/// into a registration and reports what Windows said.
/// </para>
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private readonly nint _windowHandle;
    private readonly Action _recordToggle;
    private readonly Action _pauseToggle;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<string> _conflicts = [];
    private int _nextId = 0xC000;

    /// <param name="window">The window that receives WM_HOTKEY.</param>
    /// <param name="recordToggle">What the start/stop combination does.</param>
    /// <param name="pauseToggle">What the pause/resume combination does.</param>
    public HotkeyManager(System.Windows.Window window, Action recordToggle, Action pauseToggle)
    {
        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.EnsureHandle();
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowHook);
        _recordToggle = recordToggle;
        _pauseToggle = pauseToggle;
    }

    /// <summary>Combinations that could not be registered at the last
    /// <see cref="Apply"/>, each as one plain sentence.</summary>
    public IReadOnlyList<string> Conflicts => _conflicts;

    /// <summary>
    /// Releases whatever is registered now and registers the two toggles from
    /// <paramref name="hotkeys"/>. Returns <see cref="Conflicts"/>: empty when every
    /// combination that was asked for is now live.
    /// </summary>
    public IReadOnlyList<string> Apply(HotkeySettings hotkeys)
    {
        UnregisterAll();
        _conflicts.Clear();

        Register(hotkeys.RecordToggle, "start/stop recording", _recordToggle);

        // The same keys twice would fail the second registration, and Windows would
        // blame "another application". The validator refuses to SAVE such a pair, but
        // a file edited by hand can still hold one, so it is named for what it is.
        if (HotkeyCombination.AreSame(hotkeys.RecordToggle, hotkeys.PauseToggle))
        {
            _conflicts.Add(
                $"{hotkeys.PauseToggle} is already the start/stop hotkey, so it was not also set for pause/resume. " +
                "Choose different keys on the Settings page.");
        }
        else
        {
            Register(hotkeys.PauseToggle, "pause/resume recording", _pauseToggle);
        }

        return _conflicts;
    }

    /// <summary>Registers one combination ("Ctrl+Alt+F9"); empty string = disabled.
    /// A failure is recorded in <see cref="Conflicts"/> rather than thrown.</summary>
    private void Register(string combination, string purpose, Action action)
    {
        if (string.IsNullOrWhiteSpace(combination))
        {
            return;
        }

        string? problem = HotkeyCombination.DescribeProblem(combination);
        if (problem is not null || !HotkeyCombination.TryParse(combination, out HotkeyCombination parsed))
        {
            _conflicts.Add($"The {purpose} hotkey was not set. {problem}");
            return;
        }

        int id = _nextId++;
        if (GlobalHotkeys.Register(_windowHandle, id, parsed.Modifiers, parsed.VirtualKey))
        {
            _actions[id] = action;
        }
        else
        {
            _conflicts.Add(
                $"{combination} ({purpose}) is already used by another application, so Captr could not take it. " +
                "Choose different keys on the Settings page, or close the application that uses them.");
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

    private void UnregisterAll()
    {
        foreach (int id in _actions.Keys)
        {
            GlobalHotkeys.Unregister(_windowHandle, id);
        }

        _actions.Clear();
    }

    public void Dispose() => UnregisterAll();
}
