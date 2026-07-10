using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kumori.App.ViewModels;

/// <summary>Visible when true, collapsed otherwise.</summary>
public sealed class BoolToVisibleConverter : IValueConverter
{
    public static readonly BoolToVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
