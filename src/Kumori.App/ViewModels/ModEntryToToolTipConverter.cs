using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using Kumori.Core.Models;

namespace Kumori.App.ViewModels;

public sealed class ModEntryToToolTipConverter : IValueConverter
{
    public static readonly ModEntryToToolTipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ModEntry mod) return "Mod";
        var title = ModBadgeInfo.DisplayName(mod.Acronym);
        if (string.IsNullOrWhiteSpace(mod.SettingsJson)) return title;

        try
        {
            using var document = JsonDocument.Parse(mod.SettingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return title;
            var settings = document.RootElement.EnumerateObject()
                .Where(property => !IsHiddenInternalSetting(mod.Acronym, property.Name))
                .Select(property => new Setting(property.Name, Format(property.Name, property.Value)))
                .Where(setting => !string.IsNullOrWhiteSpace(setting.Text))
                .ToArray();
            if (settings.Length == 0) return title;

            var details = mod.Acronym.Equals("DA", StringComparison.OrdinalIgnoreCase)
                ? string.Join("  |  ", settings
                    .OrderBy(setting => DifficultyAdjustOrder(setting.Key))
                    .Select(setting => setting.Text))
                : string.Join(Environment.NewLine, settings.Select(setting => setting.Text));
            return title + Environment.NewLine + details;
        }
        catch (JsonException)
        {
            return title;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool IsHiddenInternalSetting(string acronym, string key) =>
        acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase) &&
        key.Equals("target_initialised", StringComparison.OrdinalIgnoreCase);

    private static string Format(string key, JsonElement value)
    {
        var label = key.ToLowerInvariant() switch
        {
            "speed_change" => "Speed",
            "target_bpm" => "Target BPM",
            "audio_mode" => "Audio mode",
            "scale_map_stats_with_bpm" => "Scale map stats",
            "approach_rate" or "ar" => "AR",
            "circle_size" or "cs" => "CS",
            "overall_difficulty" or "od" => "OD",
            "drain_rate" or "hp" => "HP",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ').ToLowerInvariant()),
        };
        string rendered;
        if (key.Equals("target_bpm", StringComparison.OrdinalIgnoreCase) &&
            TryNumber(value) is { } targetBpm)
        {
            rendered = targetBpm.ToString("0.##", CultureInfo.InvariantCulture) + " BPM";
        }
        else if (key.Equals("audio_mode", StringComparison.OrdinalIgnoreCase))
        {
            string normalized = value.ToString().Replace("_", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal)
                .ToLowerInvariant();
            rendered = normalized switch
            {
                "1" or "adjustpitch" => "Adjust pitch",
                "2" or "nightcore" => "Nightcore",
                _ => "Preserve pitch",
            };
        }
        else rendered = value.ValueKind switch
        {
            JsonValueKind.Number when key.Equals("speed_change", StringComparison.OrdinalIgnoreCase)
                => value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) + "×",
            JsonValueKind.Number => value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
            JsonValueKind.True => "On",
            JsonValueKind.False => "Off",
            JsonValueKind.String => value.GetString() ?? "",
            _ => "",
        };
        return string.IsNullOrWhiteSpace(rendered) ? "" : $"{label}: {rendered}";
    }

    private static double? TryNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out double number) => number,
        JsonValueKind.String when double.TryParse(
            value.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double number) => number,
        _ => null,
    };

    private static int DifficultyAdjustOrder(string key) => key.ToLowerInvariant() switch
    {
        "approach_rate" or "ar" => 0,
        "circle_size" or "cs" => 1,
        "overall_difficulty" or "od" => 2,
        "drain_rate" or "hp" => 3,
        _ => 4,
    };

    private sealed record Setting(string Key, string Text);
}
