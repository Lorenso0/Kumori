using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Kumori.App.Skins;

public static class SkinImageTools
{
    public static bool HasVisiblePixels(ReadOnlySpan<byte> bgra)
    {
        for (var index = 3; index < bgra.Length; index += 4)
            if (bgra[index] != 0)
                return true;
        return false;
    }

    public static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    public static byte[] Pixels(BitmapSource source, out int stride)
    {
        stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static byte[] RenderPixels(SkinElementEntry entry)
    {
        if (entry.OriginalPixels is null)
            throw new InvalidOperationException("The image has not been decoded.");

        var pixels = (byte[])entry.OriginalPixels.Clone();
        switch (entry.Mode)
        {
            case SkinRecolorMode.Colorize when entry.TintColor is { } color:
                ApplyColorize(pixels, color);
                break;
            case SkinRecolorMode.Tint when entry.TintColor is { } tint:
                ApplyTint(pixels, tint);
                break;
            case SkinRecolorMode.HueSaturation:
                ApplyHueSaturation(
                    pixels,
                    entry.HueShiftDegrees,
                    entry.SaturationMultiplier,
                    entry.LightnessMultiplier);
                break;
        }

        return pixels;
    }

    public static BitmapSource Render(SkinElementEntry entry)
    {
        var bitmap = ToBitmap(
            RenderPixels(entry),
            entry.PixelWidth,
            entry.PixelHeight,
            entry.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static void ApplyColorize(byte[] bgra, Color target)
    {
        RgbToHsl(target.R, target.G, target.B, out var targetHue, out var targetSaturation, out _);
        for (var index = 0; index < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0) continue;
            RgbToHsl(bgra[index + 2], bgra[index + 1], bgra[index], out _, out _, out var lightness);
            HslToRgb(targetHue, targetSaturation, lightness, out var red, out var green, out var blue);
            bgra[index] = blue;
            bgra[index + 1] = green;
            bgra[index + 2] = red;
        }
    }

    public static void ApplyTint(byte[] bgra, Color target)
    {
        for (var index = 0; index < bgra.Length; index += 4)
        {
            var luminance =
                (0.299 * bgra[index + 2] + 0.587 * bgra[index + 1] + 0.114 * bgra[index]) / 255;
            bgra[index] = (byte)Math.Clamp(target.B * luminance, 0, 255);
            bgra[index + 1] = (byte)Math.Clamp(target.G * luminance, 0, 255);
            bgra[index + 2] = (byte)Math.Clamp(target.R * luminance, 0, 255);
        }
    }

    public static void ApplyMultiplicativeTint(byte[] bgra, Color target)
    {
        var red = target.R / 255d;
        var green = target.G / 255d;
        var blue = target.B / 255d;
        for (var index = 0; index < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0) continue;
            bgra[index] = (byte)Math.Clamp(bgra[index] * blue, 0, 255);
            bgra[index + 1] = (byte)Math.Clamp(bgra[index + 1] * green, 0, 255);
            bgra[index + 2] = (byte)Math.Clamp(bgra[index + 2] * red, 0, 255);
        }
    }

    public static void ApplyHueSaturation(
        byte[] bgra,
        double hueDegrees,
        double saturationMultiplier,
        double lightnessMultiplier)
    {
        for (var index = 0; index < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0) continue;
            RgbToHsl(
                bgra[index + 2],
                bgra[index + 1],
                bgra[index],
                out var hue,
                out var saturation,
                out var lightness);
            hue = (hue + hueDegrees) % 360;
            if (hue < 0) hue += 360;
            saturation = Math.Clamp(saturation * saturationMultiplier, 0, 1);
            lightness = Math.Clamp(lightness * lightnessMultiplier, 0, 1);
            HslToRgb(hue, saturation, lightness, out var red, out var green, out var blue);
            bgra[index] = blue;
            bgra[index + 1] = green;
            bgra[index + 2] = red;
        }
    }

    public static WriteableBitmap ToBitmap(byte[] pixels, int width, int height, int stride)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        return bitmap;
    }

    public static byte[] Encode(BitmapSource source, string filename)
    {
        BitmapEncoder encoder = Path.GetExtension(filename).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filename).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void RgbToHsl(
        byte red,
        byte green,
        byte blue,
        out double hue,
        out double saturation,
        out double lightness)
    {
        var redValue = red / 255d;
        var greenValue = green / 255d;
        var blueValue = blue / 255d;
        var maximum = Math.Max(redValue, Math.Max(greenValue, blueValue));
        var minimum = Math.Min(redValue, Math.Min(greenValue, blueValue));
        var delta = maximum - minimum;
        lightness = (maximum + minimum) / 2;
        if (delta < 0.000000001)
        {
            hue = saturation = 0;
            return;
        }

        saturation = lightness < 0.5
            ? delta / (maximum + minimum)
            : delta / (2 - maximum - minimum);
        hue = maximum == redValue
            ? 60 * (((greenValue - blueValue) / delta) % 6)
            : maximum == greenValue
                ? 60 * (((blueValue - redValue) / delta) + 2)
                : 60 * (((redValue - greenValue) / delta) + 4);
        if (hue < 0) hue += 360;
    }

    private static void HslToRgb(
        double hue,
        double saturation,
        double lightness,
        out byte red,
        out byte green,
        out byte blue)
    {
        if (saturation < 0.000000001)
        {
            red = green = blue = (byte)Math.Round(lightness * 255);
            return;
        }

        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var secondary = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var offset = lightness - chroma / 2;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, secondary, 0d),
            < 120 => (secondary, chroma, 0d),
            < 180 => (0d, chroma, secondary),
            < 240 => (0d, secondary, chroma),
            < 300 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary),
        };
        red = (byte)Math.Round((r + offset) * 255);
        green = (byte)Math.Round((g + offset) * 255);
        blue = (byte)Math.Round((b + offset) * 255);
    }
}
