using System.Text.Json;

namespace Kumori.ReplayViewer;

/// <summary>
/// Orders score mods the same way as osu-web: mod type first, then acronym.
/// The stored order remains untouched; this is only used for presentation.
/// </summary>
internal static class ReplayModDisplayOrder
{
    private static readonly string[] knownAcronyms =
    [
        "SV2", "10K",
        "NM", "AC", "AS", "AL", "AD", "AP", "AT", "BR", "BL", "BM", "BU", "CN", "CL", "CS", "CO",
        "DC", "DF", "DP", "DA", "DT", "DS", "EZ", "8K", "FI", "5K", "FL", "FF", "4K", "FR",
        "GR", "HT", "HR", "HD", "HO", "IN", "MG", "MR", "MF", "MU", "NC", "9K", "NF", "NR",
        "NS", "1K", "PF", "RD", "RX", "RP", "7K", "SR", "SG", "6K", "SI", "SO", "ST", "SD",
        "SW", "SY", "TP", "3K", "TD", "TC", "TR", "2K", "WG", "WD", "WU",
    ];

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
            "MU", "NS", "RP", "SI", "SY", "TR", "WD", "WG", "WU",
        };

    private static readonly HashSet<string> system =
        new(StringComparer.OrdinalIgnoreCase) { "NM", "TD", "V2" };

    public static IReadOnlyList<string> FromKey(string? modsKey) => parse(modsKey)
        .Where(acronym => !acronym.Equals("NM", StringComparison.OrdinalIgnoreCase))
        .OrderBy(category)
        .ThenBy(acronym => acronym, AcronymComparer.Instance)
        .ToArray();

    private static IReadOnlyList<string> parse(string? modsKey)
    {
        if (string.IsNullOrWhiteSpace(modsKey) || modsKey.Equals("NM", StringComparison.OrdinalIgnoreCase))
            return [];

        string trimmed = modsKey.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
            return splitPackedAcronyms(trimmed);

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return splitPackedAcronyms(trimmed);

            var acronyms = new List<string>();
            foreach (JsonElement mod in document.RootElement.EnumerateArray())
            {
                string? acronym = mod.ValueKind switch
                {
                    JsonValueKind.String => mod.GetString(),
                    JsonValueKind.Object when mod.TryGetProperty("acronym", out JsonElement property) => property.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(acronym))
                    acronyms.Add(acronym.Trim().ToUpperInvariant());
            }

            return acronyms.Count == 0 ? splitPackedAcronyms(trimmed) : acronyms;
        }
        catch (JsonException)
        {
            return splitPackedAcronyms(trimmed);
        }
    }

    private static IReadOnlyList<string> splitPackedAcronyms(string modsKey)
    {
        string remaining = modsKey.Trim().ToUpperInvariant();
        var acronyms = new List<string>();

        while (remaining.Length > 0)
        {
            string? matched = knownAcronyms.FirstOrDefault(acronym => remaining.StartsWith(acronym, StringComparison.Ordinal));
            int length = matched?.Length ?? Math.Min(2, remaining.Length);
            acronyms.Add(matched ?? remaining[..length]);
            remaining = remaining[length..];
        }

        return acronyms;
    }

    private static int category(string acronym)
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

            int? leftKey = keyCount(left);
            int? rightKey = keyCount(right);
            if (leftKey is not null && rightKey is not null) return leftKey.Value.CompareTo(rightKey.Value);
            if (leftKey is not null) return -1;
            if (rightKey is not null) return 1;
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static int? keyCount(string acronym) =>
            acronym.EndsWith('K') && int.TryParse(acronym[..^1], out int count) ? count : null;
    }
}
