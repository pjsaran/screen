using System.Windows;
using System.Windows.Controls;

namespace Captr.App.Views;

/// <summary>
/// The title-and-actions strip at the top of every page.
/// </summary>
/// <remarks>
/// <para>
/// One control rather than five hand-rolled headers. Each page previously built its
/// own title block, and they drifted: different fonts, different vertical positions,
/// some with a subtitle and some without, Settings with its buttons at the BOTTOM of
/// the page instead of the top. The result was that the layout visibly moved every
/// time you changed page.
/// </para>
/// <para>
/// Pages place this outside their ScrollViewer, so the title and the page-level
/// actions stay fixed while the content scrolls underneath. Buttons that act on a
/// single row belong in that row, not here.
/// </para>
/// </remarks>
public partial class PageHeader : UserControl
{
    /// <summary>The page's name, shown at title size on the left.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    /// <summary>
    /// The page-level actions, shown right-aligned. Usually a horizontal
    /// <see cref="StackPanel"/> of buttons.
    /// </summary>
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

    public PageHeader() => InitializeComponent();

    /// <inheritdoc cref="TitleProperty"/>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <inheritdoc cref="ActionsProperty"/>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
