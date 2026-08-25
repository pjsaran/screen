using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Captr.App.Views;

/// <summary>
/// Visible when the bound count is greater than zero, collapsed when it is zero.
/// Pass the parameter <c>"zero"</c> to invert it — visible only when EMPTY. The pair
/// is what lets a table and its "nothing here yet" line share one spot: exactly one
/// of them shows, and an empty table never sits there as a lone header strip.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value is int number ? number : 0;
        bool showWhenEmpty = string.Equals(parameter as string, "zero", StringComparison.OrdinalIgnoreCase);
        return (count > 0) != showWhenEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
