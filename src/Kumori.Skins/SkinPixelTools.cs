namespace Kumori.Skins;

public readonly record struct SkinRgb(byte Red, byte Green, byte Blue);

/// <summary>
/// UI-framework-neutral BGRA32 transforms used by both Skin Studio frontends.
/// Alpha is preserved by every colour operation.
/// </summary>
public static class SkinPixelTools
{
    public static bool HasVisiblePixels(ReadOnlySpan<byte> bgra)
    {
        for (var index = 3; index < bgra.Length; index += 4)
            if (bgra[index] != 0)
                return true;
        return false;
    }

    public static void ApplyColorize(Span<byte> bgra, SkinRgb target)
    {
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0)
                continue;
            bgra[index] = target.Blue;
            bgra[index + 1] = target.Green;
            bgra[index + 2] = target.Red;
        }
    }

    public static void ApplyTint(Span<byte> bgra, SkinRgb target)
    {
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            var luminance =
                (0.299 * bgra[index + 2] + 0.587 * bgra[index + 1] + 0.114 * bgra[index]) / 255;
            bgra[index] = (byte)Math.Clamp(target.Blue * luminance, 0, 255);
            bgra[index + 1] = (byte)Math.Clamp(target.Green * luminance, 0, 255);
            bgra[index + 2] = (byte)Math.Clamp(target.Red * luminance, 0, 255);
        }
    }

    public static void ApplyMultiplicativeTint(Span<byte> bgra, SkinRgb target)
    {
        var red = target.Red / 255d;
        var green = target.Green / 255d;
        var blue = target.Blue / 255d;
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0)
                continue;
            bgra[index] = (byte)Math.Clamp(bgra[index] * blue, 0, 255);
            bgra[index + 1] = (byte)Math.Clamp(bgra[index + 1] * green, 0, 255);
            bgra[index + 2] = (byte)Math.Clamp(bgra[index + 2] * red, 0, 255);
        }
    }

    public static void ApplyHueSaturation(
        Span<byte> bgra,
        double hueDegrees,
        double saturationMultiplier,
        double lightnessMultiplier)
    {
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0)
                continue;
            rgbToHsl(
                bgra[index + 2],
                bgra[index + 1],
                bgra[index],
                out var hue,
                out var saturation,
                out var lightness);
            hue = (hue + hueDegrees) % 360;
            if (hue < 0)
                hue += 360;
            saturation = Math.Clamp(saturation * saturationMultiplier, 0, 1);
            lightness = Math.Clamp(lightness * lightnessMultiplier, 0, 1);
            hslToRgb(hue, saturation, lightness, out var red, out var green, out var blue);
            bgra[index] = blue;
            bgra[index + 1] = green;
            bgra[index + 2] = red;
        }
    }

    public static void ApplyPaletteReplace(
        Span<byte> bgra,
        SkinRgb source,
        SkinRgb target,
        byte tolerance)
    {
        rgbToHsl(
            source.Red,
            source.Green,
            source.Blue,
            out _,
            out _,
            out var sourceLightness);
        rgbToHsl(
            target.Red,
            target.Green,
            target.Blue,
            out var targetHue,
            out var targetSaturation,
            out var targetLightness);
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            if (bgra[index + 3] == 0)
                continue;
            var red = bgra[index + 2];
            var green = bgra[index + 1];
            var blue = bgra[index];
            if (Math.Abs(red - source.Red) > tolerance
                || Math.Abs(green - source.Green) > tolerance
                || Math.Abs(blue - source.Blue) > tolerance)
            {
                continue;
            }
            if (red == source.Red
                && green == source.Green
                && blue == source.Blue)
            {
                bgra[index] = target.Blue;
                bgra[index + 1] = target.Green;
                bgra[index + 2] = target.Red;
                continue;
            }
            rgbToHsl(red, green, blue, out _, out _, out var lightness);
            hslToRgb(
                targetHue,
                targetSaturation,
                Math.Clamp(
                    targetLightness + lightness - sourceLightness,
                    0,
                    1),
                out var replacedRed,
                out var replacedGreen,
                out var replacedBlue);
            bgra[index] = replacedBlue;
            bgra[index + 1] = replacedGreen;
            bgra[index + 2] = replacedRed;
        }
    }

    private static void rgbToHsl(
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
        if (hue < 0)
            hue += 360;
    }

    private static void hslToRgb(
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
