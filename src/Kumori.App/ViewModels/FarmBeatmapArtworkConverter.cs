using System.Globalization;
using System.Windows.Data;
using Kumori.FarmFinder;

namespace Kumori.App.ViewModels;

public sealed class FarmBeatmapArtworkConverter : IValueConverter
{
    public static readonly FarmBeatmapArtworkConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FarmBeatmap beatmap)
            return null;
        var source = BeatmapArtworkResolver.Resolve(
            beatmap.BeatmapId,
            beatmap.BeatmapSetId,
            beatmap.Difficulty,
            beatmap.CoverUrl);
        return PanelArtworkSourceConverter.Instance.Convert(source, targetType, parameter, culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
