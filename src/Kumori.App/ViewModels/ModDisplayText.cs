using System.Text.Json;
using Serilog;

namespace Kumori.App.ViewModels;

internal static class ModDisplayText
{
    private static readonly string[] knownAcronyms =
    [
        "SV2", "10K", "BPM",
        "NM", "AC", "AS", "AL", "AD", "AP", "AT", "BR", "BL", "BM", "BU", "CN", "CL", "CS", "CO",
        "DC", "DF", "DP", "DA", "DT", "DS", "EZ", "8K", "FI", "5K", "FL", "FF", "4K", "FR",
        "GR", "HT", "HR", "HD", "HO", "IN", "MG", "MR", "MF", "MU", "NC", "9K", "NF", "NR",
        "NS", "1K", "PF", "RD", "RX", "RP", "7K", "SR", "SG", "6K", "SI", "SO", "ST", "SD",
        "SW", "SY", "TP", "3K", "TD", "TC", "TR", "2K", "WG", "WD", "WU"
    ];

    public static string FromKey(string? modsKey)
        => string.Concat(ModDisplayOrder.Sort(AcronymsFromKey(modsKey)));

    public static IReadOnlyList<string> AcronymsFromKey(string? modsKey)
    {
        if (string.IsNullOrWhiteSpace(modsKey) || modsKey == "NM")
        {
            return [];
        }

        var trimmed = modsKey.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return SplitPackedAcronyms(trimmed);
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return SplitPackedAcronyms(trimmed);
            }

            var acronyms = new List<string>();
            foreach (var mod in doc.RootElement.EnumerateArray())
            {
                if (mod.ValueKind == JsonValueKind.String)
                {
                    AddAcronym(acronyms, mod.GetString());
                    continue;
                }

                if (mod.ValueKind == JsonValueKind.Object
                    && mod.TryGetProperty("acronym", out var acronymProperty))
                {
                    AddAcronym(acronyms, acronymProperty.GetString());
                }
            }

            return acronyms.Count == 0 ? SplitPackedAcronyms(trimmed) : acronyms;
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Could not parse mods key for display: {ModsKey}", trimmed);
            return SplitPackedAcronyms(trimmed);
        }
    }

    private static void AddAcronym(List<string> acronyms, string? acronym)
    {
        if (!string.IsNullOrWhiteSpace(acronym))
        {
            acronyms.Add(acronym);
        }
    }

    private static IReadOnlyList<string> SplitPackedAcronyms(string modsKey)
    {
        if (string.IsNullOrWhiteSpace(modsKey) || modsKey == "NM")
        {
            return [];
        }

        var remaining = modsKey.Trim().ToUpperInvariant();
        var acronyms = new List<string>();

        while (remaining.Length > 0)
        {
            var matched = knownAcronyms.FirstOrDefault(a => remaining.StartsWith(a, StringComparison.Ordinal));

            if (matched is null)
            {
                var fallbackLength = Math.Min(2, remaining.Length);
                acronyms.Add(remaining[..fallbackLength]);
                remaining = remaining[fallbackLength..];
                continue;
            }

            if (matched != "NM")
            {
                acronyms.Add(matched);
            }

            remaining = remaining[matched.Length..];
        }

        return acronyms;
    }
}
