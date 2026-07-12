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
                .Select(property => Format(property.Name, property.Value))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            return settings.Length == 0 ? title : title + Environment.NewLine + string.Join(Environment.NewLine, settings);
        }
        catch (JsonException)
        {
            return title;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Format(string key, JsonElement value)
    {
        var label = key.ToLowerInvariant() switch
        {
            "speed_change" => "Speed",
            "approach_rate" or "ar" => "AR",
            "circle_size" or "cs" => "CS",
            "overall_difficulty" or "od" => "OD",
            "drain_rate" or "hp" => "HP",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ').ToLowerInvariant()),
        };
        var rendered = value.ValueKind switch
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
}
