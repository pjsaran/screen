using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Captr.App.Views;

/// <summary>Visible when false, collapsed when true — the mirror of WPF's built-in
/// BooleanToVisibilityConverter, for "show Start only while NOT recording".</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
