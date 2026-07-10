using System.Globalization;
using System.Windows.Data;

namespace Kumori.App.ViewModels;

/// <summary>Maps a mod acronym to a readable tooltip label.</summary>
public sealed class ModAcronymToNameConverter : IValueConverter
{
    public static readonly ModAcronymToNameConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ModBadgeInfo.DisplayName(value as string ?? "");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
