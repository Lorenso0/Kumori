using Kumori.Core.Models;

namespace Kumori.App.ViewModels;

/// <summary>
/// Matches osu-web's score mod display order: mod type first, then acronym
/// using numeric-aware comparison. Storage order remains untouched.
/// </summary>
internal static class ModDisplayOrder
{
    private static readonly HashSet<string> difficultyReduction =
        new(StringComparer.OrdinalIgnoreCase) { "DC", "EZ", "HT", "NF", "NR", "SR" };

    private static readonly HashSet<string> difficultyIncrease =
        new(StringComparer.OrdinalIgnoreCase) { "AC", "BL", "CO", "DT", "FI", "FL", "HD", "HR", "NC", "PF", "SD", "ST", "TC" };

    private static readonly HashSet<string> conversion =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "1K", "2K", "3K", "4K", "5K", "6K", "7K", "8K", "9K", "10K",
            "AL", "CL", "CS", "DA", "DS", "HO", "IN", "MR", "RD", "SG", "SW", "TP",
        };

    private static readonly HashSet<string> automation =
        new(StringComparer.OrdinalIgnoreCase) { "AP", "RX", "SO" };

    private static readonly HashSet<string> fun =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "AD", "AS", "BM", "BR", "BU", "DF", "DP", "FF", "FR", "GR", "MF", "MG",
            "MU", "NS", "RP", "SI", "SY", "TR", "WD", "WG", "WU", "BPM",
        };

    private static readonly HashSet<string> system =
        new(StringComparer.OrdinalIgnoreCase) { "NM", "TD", "V2" };

    public static IReadOnlyList<string> Sort(IEnumerable<string> acronyms) => acronyms
        .OrderBy(Category)
        .ThenBy(acronym => acronym, AcronymComparer.Instance)
        .ToArray();

    public static IReadOnlyList<ModEntry> Sort(IEnumerable<ModEntry> mods) => mods
        .OrderBy(mod => Category(mod.Acronym))
        .ThenBy(mod => mod.Acronym, AcronymComparer.Instance)
        .ToArray();

    private static int Category(string acronym)
    {
        if (difficultyReduction.Contains(acronym)) return 0;
        if (difficultyIncrease.Contains(acronym)) return 1;
        if (conversion.Contains(acronym)) return 2;
        if (automation.Contains(acronym)) return 3;
        if (fun.Contains(acronym)) return 4;
        if (system.Contains(acronym)) return 5;
        return 6;
    }

    private sealed class AcronymComparer : IComparer<string>
    {
        public static readonly AcronymComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftKey = KeyCount(left);
            var rightKey = KeyCount(right);
            if (leftKey is not null && rightKey is not null)
            {
                return leftKey.Value.CompareTo(rightKey.Value);
            }
            if (leftKey is not null) return -1;
            if (rightKey is not null) return 1;
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static int? KeyCount(string acronym) =>
            acronym.EndsWith('K') && int.TryParse(acronym[..^1], out var count) ? count : null;
    }
}
