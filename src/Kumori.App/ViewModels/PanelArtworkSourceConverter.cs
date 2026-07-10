using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Kumori.App.ViewModels;

public sealed class PanelArtworkSourceConverter : IValueConverter
{
    public static readonly PanelArtworkSourceConverter Instance = new();

    private const double OsuPanelBackgroundRatio = 512d / 80d;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var width = image.PixelWidth;
            var height = image.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return image;
            }

            var usableHeight = Math.Min(height, (int)Math.Ceiling(width / OsuPanelBackgroundRatio));
            var crop = new CroppedBitmap(
                image,
                new Int32Rect(0, (height - usableHeight) / 2, width, usableHeight));
            crop.Freeze();
            return crop;
        }
        catch
        {
            return value;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
