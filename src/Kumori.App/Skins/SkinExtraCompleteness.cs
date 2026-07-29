using System.IO;

namespace Kumori.App.Skins;

internal sealed record SkinExtraMissingAsset(string Key, string DisplayName);

internal sealed record SkinExtraCompletenessReport(
    string FamilyId,
    IReadOnlyList<SkinExtraMissingAsset> MissingAssets)
{
    public bool IsComplete => MissingAssets.Count == 0;
    public string MissingSummary => string.Join(
        ", ",
        MissingAssets.Select(asset => asset.DisplayName));
}

internal static class SkinExtraCompleteness
{
    private static readonly IReadOnlyList<SkinExtraMissingAsset> hitCircleAssets =
    [
        new("hitcircle", "hitcircle body"),
        new("hitcircleoverlay", "hitcircle overlay"),
        new("approachcircle", "approach circle"),
    ];

    private static readonly IReadOnlyList<SkinExtraMissingAsset> hitsoundAssets =
    [
        new("hitsound:hitnormal", "hitnormal"),
        new("hitsound:hitwhistle", "hitwhistle"),
        new("hitsound:hitfinish", "hitfinish"),
        new("hitsound:hitclap", "hitclap"),
    ];

    public static SkinExtraCompletenessReport Analyze(
        string familyId,
        IEnumerable<string> filenames)
    {
        var files = filenames
            .Where(filename => !string.IsNullOrWhiteSpace(filename))
            .ToArray();
        var required = RequiredAssets(familyId);
        var missing = required
            .Where(asset => !files.Any(filename => Supplies(familyId, filename, asset.Key)))
            .ToArray();
        return new SkinExtraCompletenessReport(familyId, missing);
    }

    public static bool Supplies(string familyId, string filename, string assetKey)
    {
        var stem = SkinExtraLogicalGrouping.LogicalStem(Path.GetFileName(filename));
        if (familyId.Equals("osu.hitcircles", StringComparison.OrdinalIgnoreCase))
            return stem.Equals(assetKey, StringComparison.OrdinalIgnoreCase);

        if (familyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase)
            && assetKey.StartsWith("digit:", StringComparison.OrdinalIgnoreCase))
        {
            var digit = assetKey[^1];
            return stem.Length >= 2 && stem[^2] == '-' && stem[^1] == digit;
        }

        if (familyId.StartsWith("audio.hitsounds.", StringComparison.OrdinalIgnoreCase)
            && assetKey.StartsWith("hitsound:", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = assetKey["hitsound:".Length..];
            return stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IReadOnlyList<SkinExtraMissingAsset> RequiredAssets(string familyId)
    {
        if (familyId.Equals("osu.hitcircles", StringComparison.OrdinalIgnoreCase))
            return hitCircleAssets;
        if (familyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(0, 10)
                .Select(digit => new SkinExtraMissingAsset($"digit:{digit}", $"digit {digit}"))
                .ToArray();
        if (familyId.StartsWith("audio.hitsounds.", StringComparison.OrdinalIgnoreCase))
            return hitsoundAssets;
        return [];
    }
}
