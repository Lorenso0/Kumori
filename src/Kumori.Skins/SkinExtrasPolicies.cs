using System.Globalization;
using System.Numerics;

namespace Kumori.Skins;

internal static class SkinCursorMiddlePolicy
{
    public static bool IsCursorFamily(string familyId) =>
        familyId.Equals("osu.cursor", StringComparison.OrdinalIgnoreCase);

    public static bool IsCursorMiddle(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        return stem.Equals("cursormiddle", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record SkinFollowpointSequenceProblem(
    string Code,
    string Message,
    string? Filename = null);

internal static class SkinFollowpointSequence
{
    private const string frame_prefix = "followpoint-";

    public static SkinFollowpointSequenceProblem? Validate(IEnumerable<string> filenames)
    {
        var frames = filenames
            .Select(filename => tryGetFrameIndex(filename, out var index)
                ? (Filename: filename, Index: (BigInteger?)index)
                : (Filename: filename, Index: null))
            .Where(frame => frame.Index.HasValue)
            .Select(frame => (frame.Filename, Index: frame.Index!.Value))
            .OrderBy(frame => frame.Index)
            .GroupBy(frame => frame.Index)
            .Select(group => group.First())
            .ToArray();
        if (frames.Length == 0)
            return null;
        if (frames[0].Index != BigInteger.Zero)
        {
            return new SkinFollowpointSequenceProblem(
                "followpoint-sequence-start",
                $"Numbered followpoints start at frame {frames[0].Index}; frame 0 is required.",
                frames[0].Filename);
        }
        for (var index = 1; index < frames.Length; index++)
        {
            var expected = frames[index - 1].Index + BigInteger.One;
            if (frames[index].Index == expected)
                continue;
            return new SkinFollowpointSequenceProblem(
                "followpoint-sequence-gap",
                $"Numbered followpoints are missing frame {expected}. "
                + "Transparent timing frames must remain in the pack.",
                frames[index].Filename);
        }
        return null;
    }

    private static bool tryGetFrameIndex(string filename, out BigInteger index)
    {
        index = BigInteger.Zero;
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        if (!stem.StartsWith(frame_prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var digits = stem.AsSpan(frame_prefix.Length);
        return !digits.IsEmpty
               && digits.ToString().All(character => character is >= '0' and <= '9')
               && BigInteger.TryParse(
                   digits,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out index);
    }
}
