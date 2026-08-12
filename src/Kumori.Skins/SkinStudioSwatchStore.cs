using System.Text.Json;

namespace Kumori.Skins;

public sealed record SkinStudioSwatch(
    string Hex,
    DateTimeOffset SavedAt);

public sealed class SkinStudioSwatchStore
{
    private const int max_swatches = 32;
    private readonly string path;

    public SkinStudioSwatchStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        path = Path.Combine(
            Path.GetFullPath(workspaceRoot),
            "settings",
            "image-swatches.json");
    }

    public IReadOnlyList<SkinStudioSwatch> List()
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var values = JsonSerializer.Deserialize<List<SkinStudioSwatch>>(
                             File.ReadAllBytes(path),
                             SkinStudioLaunchContract.JsonOptions)
                         ?? [];
            return values
                .Where(swatch => TryNormalizeHex(swatch.Hex, out _))
                .DistinctBy(swatch => swatch.Hex, StringComparer.OrdinalIgnoreCase)
                .Take(max_swatches)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<SkinStudioSwatch> Add(string hex)
    {
        if (!TryNormalizeHex(hex, out var normalized))
            throw new InvalidDataException("Swatch colour must use #RRGGBB.");
        var values = List()
            .Where(swatch => !swatch.Hex.Equals(
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .Prepend(new SkinStudioSwatch(normalized, DateTimeOffset.UtcNow))
            .Take(max_swatches)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.new";
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(
                    values,
                    SkinStudioLaunchContract.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
        return values;
    }

    public static bool TryNormalizeHex(string value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var raw = value.Trim().TrimStart('#');
        if (raw.Length != 6 || raw.Any(character => !Uri.IsHexDigit(character)))
            return false;
        normalized = "#" + raw.ToUpperInvariant();
        return true;
    }
}
