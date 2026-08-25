using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Captr.App.Views;

/// <summary>
/// Visible when the bound string has content, collapsed when it is empty or null.
/// Lets inline messages — a save confirmation, a validation error, a verify result —
/// occupy no space at all until there is something to say, which is what keeps the
/// pages compact instead of full of empty reserved rows.
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
