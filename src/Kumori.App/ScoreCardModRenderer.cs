using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.App.ViewModels;
using static System.FormattableString;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;

namespace Kumori.App;

internal static class ScoreCardModRenderer
{
    private static readonly Dictionary<string, BitmapSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IconCacheGate = new();
    private static readonly Typeface Bold = new(
        new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static double Draw(
        DrawingContext drawing,
        IEnumerable<string> acronyms,
        double? bpm,
        double x,
        double y,
        double maximumWidth,
        bool compact = false)
    {
        string[] allMods = acronyms
            .Select(value => value.Trim().TrimStart('+').ToUpperInvariant())
            .Where(value => value is not "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool hasBpmAdjust = allMods.Contains("BPM", StringComparer.OrdinalIgnoreCase);
        string[] mods = allMods
            .Where(value => value is not "CL" and not "BPM")
            .Take(8)
            .ToArray();
        if (mods.Length == 0 && !hasBpmAdjust)
            mods = ["NM"];

        double start = x;
        double gap = compact ? 5 : 7;
        foreach (string mod in mods)
        {
            double width = compact ? 38 : 46;
            if (x + width > start + maximumWidth)
                break;
            DrawIconBadge(drawing, mod, x, y, compact);
            x += width + gap;
        }

        if (hasBpmAdjust && bpm is > 0 && double.IsFinite(bpm.Value))
        {
            double width = compact ? 78 : 88;
            if (x + width <= start + maximumWidth)
            {
                DrawBpmBadge(drawing, bpm.Value, x, y, compact);
                x += width + gap;
            }
        }

        return x > start ? x - gap : start;
    }

    private static void DrawIconBadge(
        DrawingContext drawing,
        string acronym,
        double x,
        double y,
        bool compact)
    {
        double width = compact ? 38 : 46;
        string accent = ModBadgeInfo.Background(acronym);
        string foreground = ModBadgeInfo.Foreground(acronym);
        double hexHeight = compact ? 29 : 34;
        double inset = compact ? 7 : 8;
        var hex = new StreamGeometry();
        using (StreamGeometryContext context = hex.Open())
        {
            context.BeginFigure(new WpfPoint(x, y + hexHeight / 2), true, true);
            context.LineTo(new WpfPoint(x + inset, y), true, false);
            context.LineTo(new WpfPoint(x + width - inset, y), true, false);
            context.LineTo(new WpfPoint(x + width, y + hexHeight / 2), true, false);
            context.LineTo(new WpfPoint(x + width - inset, y + hexHeight), true, false);
            context.LineTo(new WpfPoint(x + inset, y + hexHeight), true, false);
        }
        hex.Freeze();
        drawing.DrawGeometry(Brush(accent), null, hex);

        double iconSize = compact ? 22 : 26;
        var iconBounds = new Rect(
            x + (width - iconSize) / 2,
            y + (hexHeight - iconSize) / 2,
            iconSize,
            iconSize);
        if (!TryDrawTintedIcon(drawing, acronym, iconBounds, foreground))
            DrawCentered(drawing, acronym, x + width / 2, y + (compact ? 8 : 10), compact ? 10 : 12, foreground, width - 8);
        DrawCentered(
            drawing,
            acronym,
            x + width / 2,
            y + hexHeight + 3,
            compact ? 9 : 10,
            "#D8C5CF",
            width - 8);
    }

    private static void DrawBpmBadge(
        DrawingContext drawing,
        double bpm,
        double x,
        double y,
        bool compact)
    {
        double width = compact ? 78 : 88;
        string accent = ModBadgeInfo.Background("BPM");
        string foreground = ModBadgeInfo.Foreground("BPM");
        double hexHeight = compact ? 29 : 34;
        double inset = compact ? 7 : 8;
        double leftWidth = compact ? 38 : 44;
        double badgeY = y + (compact ? 3 : 4);
        drawing.DrawGeometry(Brush("#55152F"), null, CreateHex(x, badgeY, width, hexHeight, inset));
        drawing.DrawGeometry(Brush(accent), null, CreateHex(x, badgeY, leftWidth, hexHeight, inset));

        DrawCentered(
            drawing,
            "BPM",
            x + leftWidth / 2,
            badgeY + (compact ? 7 : 8),
            compact ? 9 : 10.5,
            foreground,
            leftWidth - 10);
        DrawCentered(
            drawing,
            Invariant($"{bpm:0.#}"),
            x + leftWidth + (width - leftWidth) / 2 - 1,
            badgeY + (compact ? 5.5 : 6.5),
            compact ? 12 : 14,
            accent,
            width - leftWidth - 8);

        DrawBpmCog(drawing, x + leftWidth - (compact ? 3 : 4), badgeY - 2, compact, accent, foreground);
    }

    private static StreamGeometry CreateHex(
        double x,
        double y,
        double width,
        double height,
        double inset)
    {
        var hex = new StreamGeometry();
        using (StreamGeometryContext context = hex.Open())
        {
            context.BeginFigure(new WpfPoint(x, y + height / 2), true, true);
            context.LineTo(new WpfPoint(x + inset, y), true, false);
            context.LineTo(new WpfPoint(x + width - inset, y), true, false);
            context.LineTo(new WpfPoint(x + width, y + height / 2), true, false);
            context.LineTo(new WpfPoint(x + width - inset, y + height), true, false);
            context.LineTo(new WpfPoint(x + inset, y + height), true, false);
        }
        hex.Freeze();
        return hex;
    }

    private static void DrawBpmCog(
        DrawingContext drawing,
        double x,
        double y,
        bool compact,
        string accent,
        string foreground)
    {
        double scale = compact ? 0.8 : 1;
        var cog = new StreamGeometry();
        using (StreamGeometryContext context = cog.Open())
        {
            WpfPoint P(double px, double py) => new(x + px * scale, y + py * scale);
            context.BeginFigure(P(4.5, 0), true, true);
            context.PolyLineTo(
            [
                P(6.5, 0), P(7.1, 1.7), P(8.8, 1), P(10, 2.3), P(9.3, 4),
                P(11, 4.6), P(11, 6.5), P(9.3, 7.1), P(10, 8.8), P(8.8, 10),
                P(7.1, 9.3), P(6.5, 11), P(4.5, 11), P(3.9, 9.3), P(2.2, 10),
                P(1, 8.8), P(1.7, 7.1), P(0, 6.5), P(0, 4.6), P(1.7, 4),
                P(1, 2.3), P(2.2, 1), P(3.9, 1.7),
            ], true, true);
        }
        cog.Freeze();
        drawing.DrawGeometry(Brush(accent), null, cog);
        drawing.DrawEllipse(
            Brush(foreground),
            null,
            new WpfPoint(x + 5.5 * scale, y + 5.5 * scale),
            1.75 * scale,
            1.75 * scale);
    }

    private static bool TryDrawTintedIcon(
        DrawingContext drawing,
        string acronym,
        Rect bounds,
        string color)
    {
        string? filename = ModBadgeInfo.IconFileName(acronym);
        if (filename is null)
            return false;
        try
        {
            BitmapSource? image;
            lock (IconCacheGate)
            {
                if (!IconCache.TryGetValue(filename, out image))
                {
                    using Stream? resource = new ResourceManager(
                            "Kumori.g",
                            typeof(ScoreCardModRenderer).Assembly)
                        .GetStream($"assets/modicons/{filename}".ToLowerInvariant(), CultureInfo.InvariantCulture);
                    if (resource is null)
                        return false;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = resource;
                    bitmap.EndInit();
                    var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                    Int32Rect? opaque = FindOpaqueBounds(formatted);
                    image = opaque.HasValue ? new CroppedBitmap(formatted, opaque.Value) : formatted;
                    image.Freeze();
                    IconCache[filename] = image;
                }
            }
            var mask = new ImageBrush(image) { Stretch = Stretch.Uniform };
            mask.Freeze();
            drawing.PushOpacityMask(mask);
            drawing.DrawRectangle(Brush(color), null, bounds);
            drawing.Pop();
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or NotSupportedException
                                           or InvalidOperationException)
        {
            return false;
        }
    }

    private static Int32Rect? FindOpaqueBounds(BitmapSource source)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x * 4 + 3] <= 8)
                    continue;
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

    private static void DrawCentered(
        DrawingContext drawing,
        string text,
        double center,
        double y,
        double size,
        string color,
        double maximumWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Bold,
            size,
            Brush(color),
            1)
        {
            MaxTextWidth = Math.Max(1, maximumWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        drawing.DrawText(formatted, new WpfPoint(center - formatted.Width / 2, y));
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static MediaPen Pen(string value, double thickness)
    {
        var pen = new MediaPen(Brush(value), thickness);
        pen.Freeze();
        return pen;
    }
}
