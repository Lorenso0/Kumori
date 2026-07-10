using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kumori.App.ViewModels;

/// <summary>Visible when the bound value is null, collapsed otherwise.</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public static readonly NullToVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
