using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Kumori.Skins;

public enum SkinImageTransformMode
{
    Colorize,
    Tint,
    MultiplicativeTint,
    HueSaturationLightness,
}

public sealed record SkinImageTransform(
    SkinImageTransformMode Mode,
    SkinRgb Colour,
    double HueDegrees = 0,
    double SaturationMultiplier = 1,
    double LightnessMultiplier = 1);

public sealed class SkinImageTransformService
{
    public byte[] Apply(
        byte[] encoded,
        string filename,
        SkinImageTransform transform)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(transform.HueDegrees)
            || !double.IsFinite(transform.SaturationMultiplier)
            || !double.IsFinite(transform.LightnessMultiplier)
            || transform.SaturationMultiplier < 0
            || transform.LightnessMultiplier < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transform),
                "Image transform values must be finite and multipliers cannot be negative.");
        }

        using var image = Image.Load<Rgba32>(encoded);
        var bgra = new byte[checked(image.Width * image.Height * 4)];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var offset = (y * image.Width + x) * 4;
                    bgra[offset] = row[x].B;
                    bgra[offset + 1] = row[x].G;
                    bgra[offset + 2] = row[x].R;
                    bgra[offset + 3] = row[x].A;
                }
            }
        });

        switch (transform.Mode)
        {
            case SkinImageTransformMode.Colorize:
                SkinPixelTools.ApplyColorize(bgra, transform.Colour);
                break;
            case SkinImageTransformMode.Tint:
                SkinPixelTools.ApplyTint(bgra, transform.Colour);
                break;
            case SkinImageTransformMode.MultiplicativeTint:
                SkinPixelTools.ApplyMultiplicativeTint(bgra, transform.Colour);
                break;
            case SkinImageTransformMode.HueSaturationLightness:
                SkinPixelTools.ApplyHueSaturation(
                    bgra,
                    transform.HueDegrees,
                    transform.SaturationMultiplier,
                    transform.LightnessMultiplier);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transform));
        }

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var offset = (y * image.Width + x) * 4;
                    row[x] = new Rgba32(
                        bgra[offset + 2],
                        bgra[offset + 1],
                        bgra[offset],
                        bgra[offset + 3]);
                }
            }
        });

        using var output = new MemoryStream();
        var extension = Path.GetExtension(filename);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            image.Save(output, new JpegEncoder { Quality = 95 });
        }
        else
        {
            image.Save(output, new PngEncoder());
        }
        return output.ToArray();
    }
}
