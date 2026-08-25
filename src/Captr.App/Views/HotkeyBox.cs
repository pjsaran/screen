using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Captr.App.Services;

namespace Captr.App.Views;

/// <summary>
/// A text box that captures a key combination instead of accepting typing. Click it,
/// press the combination you want, and it fills in — "Ctrl+Alt+F9" — rather than
/// making you spell it correctly by hand.
/// </summary>
/// <remarks>
/// <para>
/// Typing a hotkey as text is a small trap: "Control+F9", "CTRL-F9", and "ctrl f9"
/// all look reasonable and none of them parse. Capturing the real key press removes
/// the guesswork, and the box can only ever produce a combination
/// <see cref="HotkeyManager"/> understands, because it builds the string the same way
/// the parser reads it.
/// </para>
/// <para>
/// A modifier alone is never accepted: holding Ctrl while reaching for F9 would
/// otherwise be captured as the answer. Backspace and Delete clear the box, which is
/// how a hotkey is disabled.
/// </para>
/// </remarks>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;

        // The caret would suggest typing, which is exactly what this box does not do.
        IsReadOnlyCaretVisible = false;
        CaretBrush = System.Windows.Media.Brushes.Transparent;
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Handled here, not in OnKeyDown: Tab, arrows, and Alt combinations are
        // swallowed by focus and menu handling before a normal key handler sees them.
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete)
        {
            Clear();
            return;
        }

        if (IsModifier(key))
        {
            // Show the modifiers as they are held, so the box visibly responds while
            // the user reaches for the final key.
            SetText(DescribeModifiers() is { Length: > 0 } held ? held + "+…" : "");
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            SetText("");
            ToolTip = "A global hotkey needs at least one of Ctrl, Alt, Shift or Win — " +
                      "otherwise it would fire while you are typing in another application.";
            return;
        }

        ToolTip = null;
        SetText(DescribeModifiers() + "+" + key);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Releasing a modifier before pressing a real key leaves the "Ctrl+…" hint
        // behind; clear it so the box does not keep an incomplete combination.
        if (IsModifier(e.Key == Key.System ? e.SystemKey : e.Key) && Text.EndsWith('…'))
        {
            SetText("");
        }

        e.Handled = true;
    }

    /// <summary>Selecting text has no meaning in a box that cannot be typed into, and
    /// a click should simply arm it for capture.</summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectionLength = 0;
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    /// <summary>The held modifiers in the fixed order the parser expects.</summary>
    private static string DescribeModifiers()
    {
        var parts = new List<string>(4);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return string.Join('+', parts);
    }

    /// <summary>Writes through the binding as well as the box, so the view model sees
    /// the change even though the user never "typed" anything.</summary>
    private void SetText(string value)
    {
        SetCurrentValue(TextProperty, value);
        GetBindingExpression(TextProperty)?.UpdateSource();
        CaretIndex = Text.Length;
    }
}
