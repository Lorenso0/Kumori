using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Kumori.App.Skins;

public static class SkinImageTools
{
    public static bool HasVisiblePixels(ReadOnlySpan<byte> bgra)
        => SkinPixelTools.HasVisiblePixels(bgra);

    public static bool IsFullyTransparentImage(byte[] encoded)
    {
        try
        {
            var image = Decode(encoded);
            return !HasVisiblePixels(Pixels(image, out _));
        }
        catch
        {
            return false;
        }
    }

    public static BitmapSource? CropToVisiblePixels(BitmapSource source)
    {
        var pixels = Pixels(source, out var stride);
        var left = source.PixelWidth;
        var top = source.PixelHeight;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < source.PixelHeight; y++)
            for (var x = 0; x < source.PixelWidth; x++)
            {
                if (pixels[y * stride + x * 4 + 3] == 0)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        if (right < left || bottom < top)
            return null;
        if (left == 0 && top == 0
                      && right == source.PixelWidth - 1
                      && bottom == source.PixelHeight - 1)
            return source;
        var cropped = new CroppedBitmap(
            source,
            new System.Windows.Int32Rect(
                left,
                top,
                right - left + 1,
                bottom - top + 1));
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource Decode(byte[] bytes, int decodePixelWidth = 0)
    {
        var shouldDownsample = false;
        if (decodePixelWidth > 0)
        {
            using var headerStream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                headerStream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            shouldDownsample = decoder.Frames[0].PixelWidth > decodePixelWidth;
        }

        using var stream = new MemoryStream(bytes);
        BitmapSource source;
        if (shouldDownsample)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.DecodePixelWidth = decodePixelWidth;
            image.EndInit();
            image.Freeze();
            source = image;
        }
        else
        {
            source = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
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
        => SkinPixelTools.ApplyColorize(
            bgra,
            new SkinRgb(target.R, target.G, target.B));

    public static void ApplyTint(byte[] bgra, Color target)
        => SkinPixelTools.ApplyTint(
            bgra,
            new SkinRgb(target.R, target.G, target.B));

    public static void ApplyMultiplicativeTint(byte[] bgra, Color target)
        => SkinPixelTools.ApplyMultiplicativeTint(
            bgra,
            new SkinRgb(target.R, target.G, target.B));

    public static void ApplyHueSaturation(
        byte[] bgra,
        double hueDegrees,
        double saturationMultiplier,
        double lightnessMultiplier)
        => SkinPixelTools.ApplyHueSaturation(
            bgra,
            hueDegrees,
            saturationMultiplier,
            lightnessMultiplier);

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

    public static byte[] CreateTransparentPng(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var stride = checked(width * 4);
        return EncodePng(ToBitmap(new byte[checked(stride * height)], width, height, stride));
    }

    public static byte[] Upscale2X(byte[] encoded, string targetFilename)
    {
        // An osu! @2x asset keeps the same logical size with twice the pixels.
        var source = Decode(encoded);
        var targetWidth = checked(source.PixelWidth * 2);
        var targetHeight = checked(source.PixelHeight * 2);
        if (targetWidth > 32767 || targetHeight > 32767)
            throw new InvalidDataException(
                $"The image is too large to upscale to {targetWidth} × {targetHeight}.");

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(source, new System.Windows.Rect(0, 0, targetWidth, targetHeight));
        }

        var upscaled = new RenderTargetBitmap(
            targetWidth,
            targetHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        upscaled.Render(visual);
        upscaled.Freeze();
        return Encode(upscaled, targetFilename);
    }

}
