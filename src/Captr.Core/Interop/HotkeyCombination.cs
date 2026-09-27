using System.Collections.Frozen;
using System.Globalization;

namespace Captr.Core.Interop;

/// <summary>
/// One global-hotkey combination — "Ctrl+Alt+F9" — parsed into the modifier flags and
/// virtual-key code that <see cref="GlobalHotkeys.Register"/> takes. Owns the hotkey
/// string format and the rules for which combinations are sensible, so the settings
/// validator, the CLI, and the window that registers the keys all agree on what a
/// setting means.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in Core and not beside the WPF code that registers the keys.</b> The
/// parser used to be a private detail of the window, reading key names through WPF's
/// <c>Key</c> enum. That made it impossible for <see cref="Settings.SettingsValidator"/>
/// to check a hotkey at all, so a typo saved through <c>captr settings set</c> was
/// accepted and only discovered as a start-up message box. Here the key names are a
/// table of their own, and every caller asks the same question the same way.
/// </para>
/// <para>
/// <b>The format is a public contract and is not changed here.</b> Modifiers first,
/// the key last, joined by '+', case-insensitive, spaces around '+' ignored. Key names
/// are the names the settings page has always written — WPF's <c>Key</c> names, such
/// as <c>F9</c>, <c>A</c>, <c>D5</c>, <c>OemPlus</c> or <c>PageUp</c> — so every
/// combination the page could ever have saved still parses. One deliberate change: a
/// bare digit (<c>Ctrl+Alt+5</c>) now means the digit key. Through WPF's enum it was
/// read as a raw enum VALUE, so "5" silently registered the numeric keypad's Clear key.
/// </para>
/// </remarks>
public readonly record struct HotkeyCombination(GlobalHotkeys.Modifiers Modifiers, uint VirtualKey)
{
    private const uint FirstFunctionKey = 0x70; // VK_F1
    private const uint LastFunctionKey = 0x87;  // VK_F24

    /// <summary>
    /// Key names to Win32 virtual-key codes: every name WPF's <c>Key</c> enum gives a
    /// key that can sensibly be a global hotkey. Modifier keys themselves, IME keys, and
    /// WPF's synthetic values (<c>System</c>, <c>ImeProcessed</c>) are deliberately
    /// absent — none of them can be the LAST key of a combination.
    /// </summary>
    private static readonly FrozenDictionary<string, uint> KeyNames = BuildKeyNames();

    /// <summary>True for F1 to F24, the only keys that may be a hotkey with no Ctrl,
    /// Alt or Win — nobody types a function key into a document.</summary>
    public bool IsFunctionKey => VirtualKey is >= FirstFunctionKey and <= LastFunctionKey;

    /// <summary>True when the combination includes Ctrl, Alt or Win. Shift alone does
    /// not count: Shift+A is how a capital A is typed.</summary>
    public bool HasCommandModifier =>
        (Modifiers & (GlobalHotkeys.Modifiers.Control | GlobalHotkeys.Modifiers.Alt | GlobalHotkeys.Modifiers.Windows))
        != GlobalHotkeys.Modifiers.None;

    /// <summary>"Ctrl+Alt+F9" → the combination; false for anything that is not one.
    /// An empty string is not a combination (it means "disabled" in settings).</summary>
    public static bool TryParse(string? text, out HotkeyCombination combination)
    {
        combination = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(part => part.Length == 0))
        {
            // "Ctrl++F9" or a trailing '+': the '+' key itself is written OemPlus, so an
            // empty part is always a mistake, never a key.
            return false;
        }

        GlobalHotkeys.Modifiers modifiers = GlobalHotkeys.Modifiers.None;
        foreach (string part in parts[..^1])
        {
            GlobalHotkeys.Modifiers modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => GlobalHotkeys.Modifiers.Control,
                "ALT" => GlobalHotkeys.Modifiers.Alt,
                "SHIFT" => GlobalHotkeys.Modifiers.Shift,
                "WIN" or "WINDOWS" => GlobalHotkeys.Modifiers.Windows,
                _ => GlobalHotkeys.Modifiers.None,
            };

            if (modifier == GlobalHotkeys.Modifiers.None)
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!KeyNames.TryGetValue(parts[^1], out uint virtualKey))
        {
            return false;
        }

        combination = new HotkeyCombination(modifiers, virtualKey);
        return true;
    }

    /// <summary>
    /// What is wrong with a hotkey setting, in words for the settings page, or null
    /// when it is usable. Empty is usable: it means the hotkey is switched off.
    /// </summary>
    /// <remarks>
    /// A combination with no Ctrl, Alt or Win is refused unless it is a function key.
    /// A global hotkey is taken away from EVERY application, so Shift+A would make it
    /// impossible to type a capital A anywhere on the machine while Captr is open —
    /// and the recording would start the first time somebody did.
    /// </remarks>
    public static string? DescribeProblem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!TryParse(text, out HotkeyCombination combination))
        {
            return $"'{text}' is not a key combination Captr understands. Click the box on the Settings page " +
                   "and press the keys, or write it like Ctrl+Alt+F9.";
        }

        return combination.HasCommandModifier || combination.IsFunctionKey
            ? null
            : $"'{text}' would fire while you type in other applications. Add Ctrl, Alt or Win, " +
              "or use a function key (F1 to F24).";
    }

    /// <summary>True when two settings name the same keys, however they are written
    /// ("ctrl+alt+f9" and "Alt+Ctrl+F9" are one combination).</summary>
    public static bool AreSame(string? first, string? second) =>
        TryParse(first, out HotkeyCombination a) && TryParse(second, out HotkeyCombination b) && a == b;

    private static FrozenDictionary<string, uint> BuildKeyNames()
    {
        var names = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            names[letter.ToString(CultureInfo.InvariantCulture)] = letter;
        }

        for (uint digit = 0; digit <= 9; digit++)
        {
            names["D" + digit] = 0x30 + digit;
            names[digit.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 0x30 + digit;
            names["NumPad" + digit] = 0x60 + digit;
        }

        for (uint function = 1; function <= 24; function++)
        {
            names["F" + function.ToString(CultureInfo.InvariantCulture)] = FirstFunctionKey + function - 1;
        }

        (string Name, uint Key)[] named =
        [
            ("Cancel", 0x03), ("Back", 0x08), ("Tab", 0x09), ("Clear", 0x0C),
            ("Return", 0x0D), ("Enter", 0x0D), ("Pause", 0x13), ("Capital", 0x14), ("CapsLock", 0x14),
            ("Escape", 0x1B), ("Space", 0x20),
            ("Prior", 0x21), ("PageUp", 0x21), ("Next", 0x22), ("PageDown", 0x22),
            ("End", 0x23), ("Home", 0x24), ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Select", 0x29), ("Print", 0x2A), ("Execute", 0x2B), ("Snapshot", 0x2C), ("PrintScreen", 0x2C),
            ("Insert", 0x2D), ("Delete", 0x2E), ("Help", 0x2F), ("Apps", 0x5D), ("Sleep", 0x5F),
            ("Multiply", 0x6A), ("Add", 0x6B), ("Separator", 0x6C), ("Subtract", 0x6D),
            ("Decimal", 0x6E), ("Divide", 0x6F), ("NumLock", 0x90), ("Scroll", 0x91),
            ("BrowserBack", 0xA6), ("BrowserForward", 0xA7), ("BrowserRefresh", 0xA8), ("BrowserStop", 0xA9),
            ("BrowserSearch", 0xAA), ("BrowserFavorites", 0xAB), ("BrowserHome", 0xAC),
            ("VolumeMute", 0xAD), ("VolumeDown", 0xAE), ("VolumeUp", 0xAF),
            ("MediaNextTrack", 0xB0), ("MediaPreviousTrack", 0xB1), ("MediaStop", 0xB2), ("MediaPlayPause", 0xB3),
            ("LaunchMail", 0xB4), ("SelectMedia", 0xB5), ("LaunchApplication1", 0xB6), ("LaunchApplication2", 0xB7),
            ("Oem1", 0xBA), ("OemSemicolon", 0xBA), ("OemPlus", 0xBB), ("OemComma", 0xBC),
            ("OemMinus", 0xBD), ("OemPeriod", 0xBE), ("Oem2", 0xBF), ("OemQuestion", 0xBF),
            ("Oem3", 0xC0), ("OemTilde", 0xC0), ("AbntC1", 0xC1), ("AbntC2", 0xC2),
            ("Oem4", 0xDB), ("OemOpenBrackets", 0xDB), ("Oem5", 0xDC), ("OemPipe", 0xDC),
            ("Oem6", 0xDD), ("OemCloseBrackets", 0xDD), ("Oem7", 0xDE), ("OemQuotes", 0xDE),
            ("Oem8", 0xDF), ("Oem102", 0xE2), ("OemBackslash", 0xE2),
            ("Attn", 0xF6), ("CrSel", 0xF7), ("ExSel", 0xF8), ("EraseEof", 0xF9),
            ("Play", 0xFA), ("Zoom", 0xFB), ("Pa1", 0xFD), ("OemClear", 0xFE),
        ];

        foreach ((string name, uint key) in named)
        {
            names[name] = key;
        }

        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
