using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App.ViewModels;

/// <summary>Maps a mod acronym to osu!lazer's dark foreground colour for that mod category.</summary>
public sealed class ModAcronymToForegroundBrushConverter : IValueConverter
{
    public static readonly ModAcronymToForegroundBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var acronym = value as string ?? "";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ModBadgeInfo.Foreground(acronym)));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
