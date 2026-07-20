using System.Text;
using System.Text.Json;
using Kumori.Core.Models;
using Kumori.Gameplay;

namespace Kumori.Storage;

/// <summary>
/// Builds a stable replay-alignment signature. Only mods which change the
/// playback clock, hit geometry/timing, map layout, or a target's position are
/// relevant. Visibility, audio, fail, input-assistance, and scoring mods do not
/// invalidate a cursor-path comparison.
/// </summary>
internal static class ReplayComparisonCompatibility
{
    private static readonly HashSet<string> replayAlignmentMods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Playback clock.
        "DT", "NC", "HT", "DC", "WU", "WD", "AS", "BPM",

        // Hit geometry, hit windows, approach timing, or layout conversion.
        "EZ", "HR", "DA", "MR", "RD", "TP",

        // Target motion relative to the cursor/playfield.
        "DP", "MG", "RP", "TR", "WG",
    };

    public static string Signature(string modsKey, IReadOnlyList<ModEntry> storedMods)
    {
        IReadOnlyList<ModEntry> mods = storedMods.Count > 0
            ? storedMods
            : fallbackMods(modsKey);

        return string.Join("|", mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.Acronym)
                          && (replayAlignmentMods.Contains(mod.Acronym.Trim())
                              || mod.Acronym.Equals("RAW", StringComparison.OrdinalIgnoreCase)))
            .Select(mod =>
            {
                string acronym = mod.Acronym.Trim().ToUpperInvariant();
                string settings = acronym == "BPM"
                    ? canonicalBpmSettings(mod.SettingsJson)
                    : canonicalJson(mod.SettingsJson);
                return $"{acronym}:{settings}";
            })
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string canonicalBpmSettings(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return canonicalJson(json);

            BpmAdjustSettings settings = BpmAdjustSettings.Parse(document.RootElement);
            return JsonSerializer.Serialize(new
            {
                target_bpm = settings.TargetBpm,
                scale_map_stats_with_bpm = settings.ScaleMapStatsWithBpm,
            });
        }
        catch (JsonException)
        {
            return canonicalJson(json);
        }
    }

    private static IReadOnlyList<ModEntry> fallbackMods(string modsKey)
    {
        if (string.IsNullOrWhiteSpace(modsKey)
            || modsKey.Equals("NM", StringComparison.OrdinalIgnoreCase))
            return [];

        string compact = modsKey.Trim();
        if (compact.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(compact);
                return document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => new ModEntry(
                        item.TryGetProperty("acronym", out JsonElement acronym) ? acronym.GetString() ?? "" : "",
                        item.TryGetProperty("settings", out JsonElement settings) ? settings.GetRawText() : "{}"))
                    .ToArray();
            }
            catch (JsonException)
            {
                // Preserve malformed legacy values as one strict signature so
                // they can never silently match a normal mod configuration.
                return [new ModEntry("RAW", compact)];
            }
        }

        if (compact.Length % 2 != 0)
            return [new ModEntry("RAW", compact)];

        var result = new List<ModEntry>();
        for (var index = 0; index + 1 < compact.Length; index += 2)
            result.Add(new ModEntry(compact.Substring(index, 2), "{}"));
        return result;
    }

    private static string canonicalJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                writeCanonical(writer, document.RootElement);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return $"invalid:{json.Trim()}";
        }
    }

    private static void writeCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    writeCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    writeCanonical(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                // Settings are floating-point bindables in lazer. Writing via
                // double removes irrelevant JSON formatting differences such
                // as 4, 4.0 and 4.00 while retaining the effective value.
                if (element.TryGetDouble(out var number))
                    writer.WriteNumberValue(number);
                else
                    writer.WriteRawValue(element.GetRawText());
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}
