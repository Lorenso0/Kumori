using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kumori.App.ViewModels;

/// <summary>Collapses an element when the bound value is null.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
