using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App.ViewModels;

/// <summary>Maps a mod acronym (e.g. "HD") to its category-coloured badge brush. See <see cref="ModBadgeInfo"/>.</summary>
public sealed class ModAcronymToBrushConverter : IValueConverter
{
    public static readonly ModAcronymToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var acronym = value as string ?? "";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ModBadgeInfo.Background(acronym)));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
