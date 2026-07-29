using System.IO;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Kumori.Tracking;

namespace Kumori.App.Skins;

public sealed class SkinExtrasCurrentSkinSource
{
    private readonly Func<IReadOnlyList<string>> filenames;
    private readonly Func<SkinIniDocument?> currentIni;
    private readonly Func<bool> hasStagedChanges;

    public SkinExtrasCurrentSkinSource(
        string skinName,
        Func<IReadOnlyList<string>> filenames,
        Func<string, CancellationToken, Task<byte[]?>> readFileAsync,
        Func<SkinIniDocument?> currentIni,
        Func<bool> hasStagedChanges)
    {
        SkinName = skinName;
        this.filenames = filenames;
        ReadFileAsync = readFileAsync;
        this.currentIni = currentIni;
        this.hasStagedChanges = hasStagedChanges;
    }

    public string SkinName { get; }
    public IReadOnlyList<string> Filenames => filenames();
    public Func<string, CancellationToken, Task<byte[]?>> ReadFileAsync { get; }
    public SkinIniDocument? CurrentIni => currentIni();
    public bool HasStagedChanges => hasStagedChanges();
}

public sealed record SkinExtrasSelectionResult(
    string PackDirectory,
    SkinExtraPackManifest Manifest,
    int LogicalElementCount,
    int PhysicalFileCount,
    int SettingCount,
    bool ReplaceEntireFamily,
    SkinExtraResolutionPolicy ResolutionPolicy,
    IReadOnlyList<string>? DeleteCurrentFiles = null,
    bool SmoothTrail = false);

public enum SkinExtraResolutionPolicy
{
    UseOneX,
    UpscaleToTwoX,
}

internal static class SkinCursorMiddlePolicy
{
    public const string CanonicalFilename = "cursormiddle.png";

    public static bool IsCursorFamily(string familyId) =>
        familyId.Equals("osu.cursor", StringComparison.OrdinalIgnoreCase);

    public static bool IsCursorMiddle(string filename) =>
        SkinExtraLogicalGrouping.LogicalStem(filename).Equals(
            "cursormiddle",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsOnePixelPlaceholder(
        string filename,
        int pixelWidth,
        int pixelHeight) =>
        IsCursorMiddle(filename)
        && pixelWidth == 1
        && pixelHeight == 1;

    public static bool HasRenderablePixels(
        string filename,
        int pixelWidth,
        int pixelHeight,
        ReadOnlySpan<byte> bgra) =>
        !IsOnePixelPlaceholder(filename, pixelWidth, pixelHeight)
        && SkinImageTools.HasVisiblePixels(bgra);

    public static byte[] CreateSmoothTrailPng() =>
        SkinImageTools.Encode(
            SkinImageTools.ToBitmap([0, 0, 0, 0], 1, 1, 4),
            CanonicalFilename);

    public static IReadOnlyList<SkinDraftChange> BuildChanges(
        IEnumerable<LazerSkinFileInfo> effectiveFiles,
        bool smoothTrail)
    {
        var changes = effectiveFiles
            .Where(file => IsCursorMiddle(file.Filename))
            .Select(file => new SkinDraftChange(
                file.Filename,
                file.Hash,
                [],
                $"{file.Filename} (removed by cursor trail policy)",
                SkinDraftOperation.Delete))
            .ToList();
        if (smoothTrail)
        {
            changes.Add(new SkinDraftChange(
                CanonicalFilename,
                effectiveFiles.FirstOrDefault(file =>
                    file.Filename.Equals(
                        CanonicalFilename,
                        StringComparison.OrdinalIgnoreCase))?.Hash,
                CreateSmoothTrailPng(),
                "cursormiddle.png (Smooth Trail placeholder)"));
        }

        return changes;
    }
}

internal sealed record SkinExtraResolutionMismatch(
    string OneXFilename,
    string ExistingTwoXFilename);

internal static class SkinExtraResolutionPlanner
{
    public static IReadOnlyList<SkinExtraResolutionMismatch> FindMismatches(
        IEnumerable<string> currentFilenames,
        IEnumerable<string> incomingFilenames)
    {
        var incoming = incomingFilenames
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = currentFilenames
            .ToDictionary(Normalize, filename => filename, StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<SkinExtraResolutionMismatch>();

        foreach (var oneXFilename in incoming
                     .Where(SkinElementCategorizer.IsImage)
                     .Where(filename => !SkinElementCategorizer.IsHighResolution(filename))
                     .OrderBy(filename => filename, StringComparer.OrdinalIgnoreCase))
        {
            var twoXFilename = ToTwoXFilename(oneXFilename);
            if (incoming.Contains(twoXFilename)
                || !current.TryGetValue(twoXFilename, out var existingTwoXFilename))
                continue;
            mismatches.Add(new SkinExtraResolutionMismatch(oneXFilename, existingTwoXFilename));
        }

        return mismatches;
    }

    public static string ToTwoXFilename(string filename)
    {
        var normalized = Normalize(filename);
        var extension = Path.GetExtension(normalized);
        return normalized[..^extension.Length] + "@2x" + extension;
    }

    private static string Normalize(string filename) => filename.Replace('\\', '/');
}

internal static partial class SkinExtraLogicalGrouping
{
    public static string Key(
        string familyId,
        string filename,
        IReadOnlyCollection<string> familyFilenames)
    {
        var stem = LogicalStem(filename);
        if (familyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
            return "number-font";

        var match = AnimationFrameSuffix().Match(stem);
        if (!match.Success)
            return stem;

        var candidate = match.Groups["base"].Value;
        var siblings = familyFilenames.Select(LogicalStem).ToArray();
        return siblings.Count(other =>
                   other.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                   || other.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase)
                      && AnimationFrameSuffix().IsMatch(other)) >= 2
            ? candidate
            : stem;
    }

    public static string DisplayName(string key) => key switch
    {
        "number-font" => "Number font",
        "cursor" => "Cursor",
        "cursortrail" => "Cursor trail",
        "cursormiddle" => "Cursor middle",
        "hitcircle" => "Hitcircle body",
        "hitcircleoverlay" => "Hitcircle overlay",
        "approachcircle" => "Approach circle",
        "sliderfollowcircle" => "Slider follow circle",
        "sliderscorepoint" => "Slider tick",
        "reversearrow" => "Reverse arrow",
        "menu-background" => "Background",
        _ => string.Join(' ', key.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant() is { Length: > 0 } words
            ? char.ToUpperInvariant(words[0]) + words[1..]
            : key,
    };

    public static string LogicalStem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        return stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)
            ? stem[..^3]
            : stem;
    }

    [GeneratedRegex(@"^(?<base>.+)-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AnimationFrameSuffix();
}

internal sealed record SkinFollowpointSequenceProblem(
    string Code,
    string Message,
    string? Filename = null);

internal static class SkinFollowpointSequence
{
    private const string FramePrefix = "followpoint-";

    public static SkinFollowpointSequenceProblem? Validate(IEnumerable<string> filenames)
    {
        var frames = filenames
            .Select(filename => TryGetFrameIndex(filename, out var index)
                ? (Filename: filename, Index: (BigInteger?)index)
                : (Filename: filename, Index: null))
            .Where(frame => frame.Index.HasValue)
            .Select(frame => (frame.Filename, Index: frame.Index!.Value))
            .OrderBy(frame => frame.Index)
            .ToArray();
        if (frames.Length == 0)
            return null;

        var distinct = frames
            .GroupBy(frame => frame.Index)
            .Select(group => group.First())
            .ToArray();
        if (distinct[0].Index != BigInteger.Zero)
        {
            return new SkinFollowpointSequenceProblem(
                "followpoint-sequence-start",
                $"Numbered followpoints start at frame {distinct[0].Index}; frame 0 is required.",
                distinct[0].Filename);
        }

        for (var index = 1; index < distinct.Length; index++)
        {
            var expected = distinct[index - 1].Index + BigInteger.One;
            if (distinct[index].Index == expected)
                continue;
            return new SkinFollowpointSequenceProblem(
                "followpoint-sequence-gap",
                $"Numbered followpoints are missing frame {expected}. "
                + "Transparent timing frames must remain in the pack.",
                distinct[index].Filename);
        }

        return null;
    }

    private static bool TryGetFrameIndex(string filename, out BigInteger index)
    {
        index = BigInteger.Zero;
        var stem = SkinExtraLogicalGrouping.LogicalStem(filename);
        if (!stem.StartsWith(FramePrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var digits = stem.AsSpan(FramePrefix.Length);
        if (digits.IsEmpty
            || !digits.ToString().All(character => character is >= '0' and <= '9'))
            return false;
        return BigInteger.TryParse(
            digits,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out index);
    }
}

internal static class SkinExtraLogicalSelectionPlanner
{
    public static IReadOnlyList<string> FindReplacedCurrentFiles(
        string familyId,
        IEnumerable<string> currentFilenames,
        IEnumerable<string> selectedIncomingFilenames)
    {
        var current = currentFilenames
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incoming = selectedIncomingFilenames
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (incoming.Count == 0)
            return [];

        var universe = current
            .Concat(incoming)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedKeys = incoming
            .Select(filename => SkinExtraLogicalGrouping.Key(
                familyId,
                filename,
                universe))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return current
            .Where(filename => !incoming.Contains(filename))
            .Where(filename => selectedKeys.Contains(
                SkinExtraLogicalGrouping.Key(
                    familyId,
                    filename,
                    universe)))
            .OrderBy(filename => filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string filename) => filename.Replace('\\', '/');
}

internal sealed record SkinExtraCurrentFallback(
    string Key,
    IReadOnlyList<string> Filenames);

internal static class SkinExtraCurrentFallbackPlanner
{
    public static IReadOnlyList<SkinExtraCurrentFallback> FindMissingLayers(
        string familyId,
        IEnumerable<string> currentFilenames,
        IEnumerable<string> incomingFilenames)
    {
        var current = currentFilenames
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incoming = incomingFilenames
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var universe = current
            .Concat(incoming)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incomingKeys = incoming
            .Select(filename => SkinExtraLogicalGrouping.Key(
                familyId,
                filename,
                universe))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return current
            .GroupBy(
                filename => SkinExtraLogicalGrouping.Key(
                    familyId,
                    filename,
                    universe),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !incomingKeys.Contains(group.Key))
            .Where(group => !SkinCursorMiddlePolicy.IsCursorMiddle(group.First()))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SkinExtraCurrentFallback(
                group.Key,
                group.OrderBy(
                        filename => filename,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
    }

    private static string Normalize(string filename) => filename.Replace('\\', '/');
}
