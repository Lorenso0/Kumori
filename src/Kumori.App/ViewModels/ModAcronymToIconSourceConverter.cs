using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kumori.App.ViewModels;

/// <summary>Maps a mod acronym to the real osu!lazer mod icon texture embedded with the app.</summary>
public sealed class ModAcronymToIconSourceConverter : IValueConverter
{
    public static readonly ModAcronymToIconSourceConverter Instance = new();

    private static readonly Dictionary<string, BitmapSource> cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var acronym = value as string ?? "";
        var fileName = ModBadgeInfo.IconFileName(acronym);

        if (fileName is null)
        {
            return null;
        }

        if (cache.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        var image = LoadTrimmedImage(fileName);

        cache[fileName] = image;
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static BitmapSource LoadTrimmedImage(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://application:,,,/Kumori;component/Assets/ModIcons/{fileName}", UriKind.Absolute);
        image.EndInit();

        var formatted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var bounds = FindOpaqueBounds(formatted);

        BitmapSource source = bounds.HasValue
            ? new CroppedBitmap(formatted, bounds.Value)
            : formatted;

        source.Freeze();
        return source;
    }

    private static Int32Rect? FindOpaqueBounds(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;

            for (var x = 0; x < width; x++)
            {
                if (pixels[row + x * 4 + 3] <= 8)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX >= minX && maxY >= minY
            ? new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1)
            : null;
    }
}
