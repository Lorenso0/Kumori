using System.Globalization;
using System.Text.Json;

namespace Kumori.FarmFinder;

public sealed class ClockRateCalculator : IClockRateCalculator
{
    public double Calculate(IReadOnlyList<FarmMod> mods)
    {
        foreach (var mod in mods)
        {
            var acronym = mod.NormalizedAcronym;
            if (acronym is not ("DT" or "NC" or "HT" or "DC"))
                continue;

            if (TryReadRate(mod.SettingsJson, out var explicitRate))
                return explicitRate;
            return acronym is "DT" or "NC" ? 1.5 : 0.75;
        }

        return 1;
    }

    private static bool TryReadRate(string json, out double rate)
    {
        rate = 0;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            foreach (var name in new[] { "speed_change", "clock_rate" })
            {
                if (!document.RootElement.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out rate) && rate > 0)
                    return true;
                if (value.ValueKind == JsonValueKind.String
                    && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
                    && rate > 0)
                    return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }
}

public sealed class ModNormalizer(IClockRateCalculator clockRateCalculator) : IModNormalizer
{
    public NormalizedMods Normalize(IReadOnlyList<FarmMod> mods, ModNormalizationOptions options)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            var acronym = mod.NormalizedAcronym;
            if (string.IsNullOrWhiteSpace(acronym) || acronym == "NM")
                continue;
            if (options.TreatNightcoreAsDoubleTime && acronym == "NC")
                acronym = "DT";
            if ((options.WildcardMods?.Contains(acronym) ?? false)
                || (options.HiddenWildcard && acronym == "HD"))
                continue;

            // Pitch affects audio presentation but not timing, difficulty, score,
            // or the DT-family grouping requested by Farm Finder.
            var settings = FarmFinderValidation.CanonicalJson(mod.SettingsJson, "adjust_pitch");
            if (normalized.TryGetValue(acronym, out var existing))
            {
                if (existing == "{}" && settings != "{}")
                    normalized[acronym] = settings;
                continue;
            }
            normalized[acronym] = settings;
        }

        var acronyms = normalized.Keys.ToArray();
        var signature = normalized.Count == 0
            ? "NM"
            : string.Join("+", normalized.Select(pair =>
                pair.Value == "{}" ? pair.Key : $"{pair.Key}:{pair.Value}"));
        return new NormalizedMods(acronyms, signature, clockRateCalculator.Calculate(mods));
    }
}

public sealed class ModMatcher : IModMatcher
{
    public bool Matches(NormalizedMods normalized, FarmFinderQuery query)
    {
        var filters = query.Mods
            .Where(filter => filter.Requirement != ModRequirement.Ignore)
            .Select(filter => new FarmModFilter(NormalizeAcronym(filter.Acronym, query), filter.Requirement))
            .ToArray();
        var active = new HashSet<string>(normalized.Acronyms, StringComparer.OrdinalIgnoreCase);
        active.ExceptWith(
            filters.Where(filter => filter.Requirement == ModRequirement.Wildcard)
                   .Select(filter => filter.Acronym));
        var nm = filters.FirstOrDefault(filter => filter.Acronym == "NM");

        if (nm?.Requirement == ModRequirement.Required && active.Count != 0)
            return false;
        if (nm?.Requirement == ModRequirement.Excluded && active.Count == 0)
            return false;

        foreach (var excluded in filters.Where(filter => filter.Requirement == ModRequirement.Excluded
                                                         && filter.Acronym != "NM"))
        {
            if (active.Contains(excluded.Acronym))
                return false;
        }

        var required = filters.Where(filter => filter.Requirement == ModRequirement.Required
                                               && filter.Acronym != "NM")
                              .Select(filter => filter.Acronym)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!required.IsSubsetOf(active))
            return false;

        if (query.ModMatchMode != ModMatchMode.Exact)
            return true;

        if (query.ExactModScope.Count == 0)
            return active.SetEquals(required);

        var exactScope = query.ExactModScope
            .Select(acronym => NormalizeAcronym(acronym, query))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        active.IntersectWith(exactScope);
        return active.SetEquals(required);
    }

    private static string NormalizeAcronym(string acronym, FarmFinderQuery query)
    {
        var value = acronym.Trim().ToUpperInvariant();
        if (query.TreatNightcoreAsDoubleTime && value == "NC")
            return "DT";
        return value;
    }
}
