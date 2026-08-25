using System.Windows;
using System.Windows.Controls;

namespace Captr.App.Theme;

/// <summary>
/// Letter-spacing (typographic "tracking") for the small uppercase labels the UI is
/// built from — column headers, state pills, the navigation rail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all:</b> WPF has no letter-spacing property. Set a caption
/// in capitals without it and the letters jam together, which is exactly why
/// uppercase micro-labels look cheap in most WPF applications and expensive
/// everywhere else. The fix used across the industry is to insert a hair space
/// (U+200A, about a tenth of an em) between characters, which is what
/// <see cref="Space"/> does.
/// </para>
/// <para>
/// <b>Why an attached property rather than a converter or a behaviour:</b> the
/// obvious alternative — watching <see cref="TextBlock.TextProperty"/> with a
/// <c>DependencyPropertyDescriptor</c> — keeps a strong reference to every TextBlock
/// for the life of the process, which is a textbook WPF leak. Here the binding
/// targets <see cref="TextProperty"/> instead of <c>Text</c>, so the flow is one-way
/// and holds nothing: <c>&lt;TextBlock theme:Typo.Text="{Binding StateLabel}" /&gt;</c>.
/// </para>
/// <para>
/// Only ever use this on SHORT labels. Hair spaces are real characters, so a tracked
/// string is copied and read aloud with them in it; that is a fair trade for
/// "LENGTH", and a bad one for a sentence.
/// </para>
/// </remarks>
public static class Typo
{
    /// <summary>U+200A HAIR SPACE — the narrowest space Windows fonts render, and the
    /// right width for tracking capitals at caption sizes.</summary>
    private const char HairSpace = '\u200A';

    /// <summary>
    /// Set this instead of <see cref="TextBlock.Text"/> to have the value shown in
    /// capitals with tracking.
    /// </summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(Typo),
        new PropertyMetadata(null, OnTextChanged));

    /// <summary>XAML setter for <see cref="TextProperty"/>.</summary>
    public static void SetText(DependencyObject element, string? value) =>
        element.SetValue(TextProperty, value);

    /// <summary>XAML getter for <see cref="TextProperty"/>.</summary>
    public static string? GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block)
        {
            block.Text = Space(e.NewValue as string);
        }
    }

    /// <summary>
    /// Upper-cases and tracks a label. An empty value stays empty, so a pill bound to
    /// a state that has no label collapses instead of showing a stray space.
    /// </summary>
    public static string Space(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        string upper = value.ToUpperInvariant();
        var builder = new System.Text.StringBuilder(upper.Length * 2);
        for (int i = 0; i < upper.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(HairSpace);
            }

            builder.Append(upper[i]);
        }

        return builder.ToString();
    }
}
