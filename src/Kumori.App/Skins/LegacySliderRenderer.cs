using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Kumori.App.Skins;

/// <summary>
/// Direct port of osu!lazer's legacy slider body colour and geometry rules.
/// </summary>
public static class LegacySliderRenderer
{
    private const float ShadowPortion = 1f - (59f / 64f);
    private const float BorderPortion = 0.1875f;

    public static WriteableBitmap Render(
        int width,
        int height,
        IReadOnlyList<Point> path,
        double radius,
        Color comboColour,
        Color? sliderBorder,
        Color? sliderTrackOverride,
        CancellationToken cancellationToken = default)
    {
        var accentRgb = sliderTrackOverride ?? comboColour;
        var accent = (
            R: accentRgb.R / 255f,
            G: accentRgb.G / 255f,
            B: accentRgb.B / 255f,
            A: 0.7f);
        var border = sliderBorder ?? Color.FromRgb(255, 255, 255);
        var borderChannels = (
            R: border.R / 255f,
            G: border.G / 255f,
            B: border.B / 255f,
            A: 1f);
        var outer = Darken(accent, 0.1f);
        var inner = Lighten(accent, 0.5f);
        var pixels = new byte[width * height * 4];

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (var point in path)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        var x0 = Math.Max(0, (int)(minX - radius - 2));
        var x1 = Math.Min(width - 1, (int)(maxX + radius + 2));
        var y0 = Math.Max(0, (int)(minY - radius - 2));
        var y1 = Math.Min(height - 1, (int)(maxY + radius + 2));

        for (var y = y0; y <= y1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = x0; x <= x1; x++)
            {
                var distance = Math.Sqrt(DistanceSquaredToPath(x + 0.5, y + 0.5, path));
                if (distance > radius + 1)
                    continue;

                var position = (float)Math.Max(0, 1 - distance / radius);
                var colour = ColourAt(position, borderChannels, outer, inner);
                var coverage = Math.Clamp(radius + 0.5 - distance, 0, 1);
                var alpha = colour.A * (float)coverage;
                var offset = (y * width + x) * 4;
                pixels[offset] = Channel(colour.B);
                pixels[offset + 1] = Channel(colour.G);
                pixels[offset + 2] = Channel(colour.R);
                pixels[offset + 3] = Channel(alpha);
            }
        }

        var bitmap = SkinImageTools.ToBitmap(pixels, width, height, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static List<Point> SampleSCurve(
        double x0,
        double y0,
        double x1,
        double y1,
        int segments = 48)
    {
        var start = new Point(x0, y0);
        var end = new Point(x1, y1);
        var control1 = new Point(x0 + (x1 - x0) * 0.35, y0 - 110);
        var control2 = new Point(x0 + (x1 - x0) * 0.65, y1 + 110);
        var points = new List<Point>(segments + 1);

        for (var index = 0; index <= segments; index++)
        {
            var time = (double)index / segments;
            var inverse = 1 - time;
            var x = inverse * inverse * inverse * start.X
                    + 3 * inverse * inverse * time * control1.X
                    + 3 * inverse * time * time * control2.X
                    + time * time * time * end.X;
            var y = inverse * inverse * inverse * start.Y
                    + 3 * inverse * inverse * time * control1.Y
                    + 3 * inverse * time * time * control2.Y
                    + time * time * time * end.Y;
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static (float R, float G, float B, float A) ColourAt(
        float position,
        (float R, float G, float B, float A) border,
        (float R, float G, float B, float A) outer,
        (float R, float G, float B, float A) inner)
    {
        if (position <= ShadowPortion)
            return Interpolate(position, (0, 0, 0, 0f), (0, 0, 0, 0.25f), 0, ShadowPortion);
        if (position <= BorderPortion)
            return border;
        return Interpolate(position, outer, inner, BorderPortion, 1);
    }

    private static (float R, float G, float B, float A) Interpolate(
        float time,
        (float R, float G, float B, float A) start,
        (float R, float G, float B, float A) end,
        float startTime,
        float endTime)
    {
        var duration = endTime - startTime;
        if (duration == 0)
            return start;
        var amount = Math.Clamp((time - startTime) / duration, 0, 1);
        return (
            start.R + amount * (end.R - start.R),
            start.G + amount * (end.G - start.G),
            start.B + amount * (end.B - start.B),
            start.A + amount * (end.A - start.A));
    }

    private static (float R, float G, float B, float A) Darken(
        (float R, float G, float B, float A) colour,
        float amount) =>
        (colour.R / (1 + amount), colour.G / (1 + amount), colour.B / (1 + amount), colour.A);

    private static (float R, float G, float B, float A) Lighten(
        (float R, float G, float B, float A) colour,
        float amount)
    {
        amount *= 0.5f;
        return (
            Math.Min(1, colour.R * (1 + 0.5f * amount) + amount),
            Math.Min(1, colour.G * (1 + 0.5f * amount) + amount),
            Math.Min(1, colour.B * (1 + 0.5f * amount) + amount),
            colour.A);
    }

    private static double DistanceSquaredToPath(
        double x,
        double y,
        IReadOnlyList<Point> path)
    {
        var best = double.MaxValue;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var start = path[index];
            var end = path[index + 1];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var lengthSquared = deltaX * deltaX + deltaY * deltaY;
            var amount = lengthSquared < 1e-9
                ? 0
                : Math.Clamp(((x - start.X) * deltaX + (y - start.Y) * deltaY) / lengthSquared, 0, 1);
            var nearestX = start.X + amount * deltaX - x;
            var nearestY = start.Y + amount * deltaY - y;
            best = Math.Min(best, nearestX * nearestX + nearestY * nearestY);
        }

        return best;
    }

    private static byte Channel(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
