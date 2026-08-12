using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kumori.Skins;

public static class SkinMediaTypes
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg"];

    public static bool IsImage(string filename) =>
        ImageExtensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase);

    public static bool IsAudio(string filename) =>
        AudioExtensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase);
}

public sealed record SkinImageValidationResult(
    int Width,
    int Height,
    bool HasVisiblePixels);

public static class SkinMediaValidationService
{
    private const int max_dimension = 16_384;

    public static SkinImageValidationResult ValidateImage(
        string filename,
        byte[] encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(encoded);
        if (!SkinMediaTypes.IsImage(filename))
            throw new InvalidDataException(
                $"'{filename}' is not a supported skin image.");
        if (encoded.Length == 0)
            throw new InvalidDataException("The edited image is empty.");

        try
        {
            var decoded = SkinImageAnalysis.Decode(encoded);
            if (decoded.Width <= 0
                || decoded.Height <= 0
                || decoded.Width > max_dimension
                || decoded.Height > max_dimension)
            {
                throw new InvalidDataException(
                    $"The edited image dimensions {decoded.Width}x{decoded.Height} are outside the supported range.");
            }
            return new SkinImageValidationResult(
                decoded.Width,
                decoded.Height,
                decoded.HasVisiblePixels);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"The edited image could not be decoded: {ex.Message}",
                ex);
        }
    }
}

internal sealed record SkinImageAnalysis(
    int Width,
    int Height,
    bool HasVisiblePixels,
    byte[] BgraPixels,
    string AverageHash)
{
    public static SkinImageAnalysis Decode(byte[] encoded)
    {
        using var image = Image.Load<Rgba32>(encoded);
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var offset = (y * image.Width + x) * 4;
                    pixels[offset] = row[x].B;
                    pixels[offset + 1] = row[x].G;
                    pixels[offset + 2] = row[x].R;
                    pixels[offset + 3] = row[x].A;
                }
            }
        });
        return new SkinImageAnalysis(
            image.Width,
            image.Height,
            SkinPixelTools.HasVisiblePixels(pixels),
            pixels,
            averageHash(image));
    }

    public static bool IsFullyTransparent(byte[] encoded)
    {
        try
        {
            return !Decode(encoded).HasVisiblePixels;
        }
        catch
        {
            return false;
        }
    }

    public string SemanticHash()
    {
        using var semantic = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        semantic.AppendData(BitConverter.GetBytes(Width));
        semantic.AppendData(BitConverter.GetBytes(Height));
        semantic.AppendData(BgraPixels);
        return Convert.ToHexStringLower(semantic.GetHashAndReset());
    }

    private static string averageHash(Image<Rgba32> source)
    {
        using var scaled = source.Clone(context => context.Resize(8, 8));
        var luminance = new byte[64];
        var offset = 0;
        scaled.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    luminance[offset++] = (byte)Math.Clamp(
                        (int)Math.Round(
                            0.299 * row[x].R
                            + 0.587 * row[x].G
                            + 0.114 * row[x].B),
                        0,
                        255);
                }
            }
        });
        var average = luminance.Average(value => (double)value);
        ulong bits = 0;
        for (var index = 0; index < luminance.Length; index++)
            if (luminance[index] >= average)
                bits |= 1UL << index;
        return bits.ToString("x16");
    }
}
