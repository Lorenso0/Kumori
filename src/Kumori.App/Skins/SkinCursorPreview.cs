using System.IO;

namespace Kumori.App.Skins;

internal enum SkinCursorPreviewLayerKind
{
    Trail,
    Middle,
    Cursor,
}

internal sealed record SkinCursorAssetSelection(
    string? CursorFilename,
    string? TrailFilename,
    string? MiddleFilename)
{
    public bool UsesSmoothTrail => MiddleFilename is not null;
}

internal readonly record struct SkinCursorPreviewLayer(
    SkinCursorPreviewLayerKind Kind,
    double CentreX,
    double CentreY,
    double MaxWidth,
    double MaxHeight,
    double Opacity);

/// <summary>
/// The single gameplay-cursor contract used by both Skin Studio and Extras.
/// Asset resolution is root-only because osu! does not resolve gameplay skin
/// elements from arbitrary archive subfolders.
/// </summary>
internal static class SkinCursorPreview
{
    public const double CanvasWidth = 640;
    public const double CanvasHeight = 480;

    public static SkinCursorAssetSelection Resolve(IEnumerable<string> filenames)
    {
        var active = filenames
            .Where(IsRootGameplayFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SkinCursorAssetSelection(
            Resolve(active, "cursor"),
            Resolve(active, "cursortrail"),
            Resolve(active, "cursormiddle"));
    }

    public static IReadOnlyList<SkinCursorPreviewLayer> Compose(
        bool hasCursor,
        bool hasTrail,
        bool hasMiddle,
        bool renderMiddle)
    {
        var points = TrailPoints(hasMiddle);
        var result = new List<SkinCursorPreviewLayer>(
            (hasTrail ? points.Count : 0) + (renderMiddle ? 1 : 0) + (hasCursor ? 1 : 0));
        if (hasTrail)
        {
            for (var index = 0; index < points.Count; index++)
            {
                var progress = points.Count == 1
                    ? 1
                    : index / (double)(points.Count - 1);
                var size = hasMiddle ? 52 : 92 + progress * 18;
                result.Add(new SkinCursorPreviewLayer(
                    SkinCursorPreviewLayerKind.Trail,
                    points[index].X,
                    points[index].Y,
                    size,
                    size,
                    hasMiddle
                        ? 0.08 + progress * 0.67
                        : 0.2 + progress * 0.55));
            }
        }

        var cursorPoint = points[^1];
        if (renderMiddle)
        {
            result.Add(new SkinCursorPreviewLayer(
                SkinCursorPreviewLayerKind.Middle,
                cursorPoint.X,
                cursorPoint.Y,
                116,
                116,
                1));
        }
        if (hasCursor)
        {
            result.Add(new SkinCursorPreviewLayer(
                SkinCursorPreviewLayerKind.Cursor,
                cursorPoint.X,
                cursorPoint.Y,
                132,
                132,
                1));
        }
        return result;
    }

    public static IReadOnlyList<(double X, double Y)> TrailPoints(bool smooth)
    {
        var count = smooth ? 28 : 5;
        var result = new (double X, double Y)[count];
        for (var index = 0; index < count; index++)
        {
            var progress = index / (double)(count - 1);
            result[index] = (
                155 + progress * 300,
                322 - progress * 168);
        }
        return result;
    }

    public static bool IsRootGameplayFile(string filename)
    {
        var normalized = filename.Replace('\\', '/').Trim('/');
        return normalized.Length > 0 && !normalized.Contains('/');
    }

    private static string? Resolve(IEnumerable<string> filenames, string logicalStem) =>
        filenames
            .Where(filename => LogicalStem(filename).Equals(
                logicalStem,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(filename =>
                Path.GetFileNameWithoutExtension(filename)
                    .EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            .ThenBy(filename => filename, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static string LogicalStem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        return stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)
            ? stem[..^3]
            : stem;
    }
}
