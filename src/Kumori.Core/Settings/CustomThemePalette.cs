using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kumori.Core.Settings;

/// <summary>
/// Portable, versioned custom-theme data. Keys describe semantic UI roles rather
/// than framework resource names so exported themes remain stable as the UI evolves.
/// </summary>
public sealed class CustomThemeSettings
{
    public string Name { get; set; } = "My theme";
    public Dictionary<string, string> Colors { get; set; } = CustomThemePalette.CreateDefaultColors();
}

public sealed class CustomThemeFile
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "My theme";

    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = [];
}

public sealed record CustomThemeColorRole(string Key, string Group, string Label, string Description);

public static partial class CustomThemePalette
{
    public const string FileFormat = "kumori-theme";
    public const int FileVersion = 1;

    public static readonly IReadOnlyList<CustomThemeColorRole> ColorRoles =
    [
        new("AppBackground", "Surfaces and layout", "Window canvas", "The outer background behind every page."),
        new("PanelBackground", "Surfaces and layout", "Panels and sections", "Large content panels and section backgrounds."),
        new("CardBackground", "Surfaces and layout", "Standard cards", "Normal play cards, settings cards, and data containers."),
        new("CardHoverBackground", "Surfaces and layout", "Hovered cards", "A card while the pointer is over it."),
        new("CardSelectedBackground", "Surfaces and layout", "Selected cards", "The active play, selected row, or chosen item."),
        new("NavigationBackground", "Surfaces and layout", "Navigation sidebar", "The app's left navigation rail."),
        new("TopBarBackground", "Surfaces and layout", "Window top bar", "The title and connection-status bar across the top."),
        new("MetricBackground", "Surfaces and layout", "Statistic tiles", "Compact score, accuracy, and session metric surfaces."),
        new("OverlayBackground", "Surfaces and layout", "Modal backdrop", "The translucent layer behind dialogs and overlays."),

        new("ControlBackground", "Controls and borders", "Inputs and buttons", "Text fields, inactive buttons, and compact controls."),
        new("ControlHoverBackground", "Controls and borders", "Hovered controls", "A button or input while the pointer is over it."),
        new("SubtleBorder", "Controls and borders", "Quiet dividers", "Low-emphasis outlines and separators."),
        new("StrongBorder", "Controls and borders", "Focus outlines", "Selected, focused, and high-emphasis outlines."),

        new("TextPrimary", "Text", "Main text", "Headings, values, and the most important labels."),
        new("TextSecondary", "Text", "Supporting text", "Normal labels and descriptive copy."),
        new("TextMuted", "Text", "Muted metadata", "Timestamps, hints, and low-priority details."),

        new("AccentPink", "Accents and status", "Primary accent", "Primary buttons, active states, and brand highlights."),
        new("AccentPurple", "Accents and status", "Secondary accent", "Charts, secondary emphasis, and supporting highlights."),
        new("Success", "Accents and status", "Success / completed", "Completed plays and positive status indicators."),
        new("Warning", "Accents and status", "Warning / 50s", "Warnings and low-value hit judgements."),
        new("Danger", "Accents and status", "Errors / misses", "Errors, failed states, and misses."),
        new("Cyan", "Accents and status", "Analyzer highlight", "Replay analysis, cursor paths, and technical highlights."),
    ];

    public static readonly IReadOnlyList<string> ColorKeys = ColorRoles.Select(role => role.Key).ToArray();

    private static readonly IReadOnlyDictionary<string, string> defaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppBackground"] = "#0E090B",
            ["PanelBackground"] = "#151013",
            ["CardBackground"] = "#1B1216",
            ["CardHoverBackground"] = "#271820",
            ["CardSelectedBackground"] = "#2A1821",
            ["ControlBackground"] = "#171013",
            ["ControlHoverBackground"] = "#261820",
            ["SubtleBorder"] = "#805A6D",
            ["StrongBorder"] = "#E8558D",
            ["AccentPink"] = "#E8558D",
            ["AccentPurple"] = "#A766C7",
            ["TextPrimary"] = "#F7F1F4",
            ["TextSecondary"] = "#C3A7B3",
            ["TextMuted"] = "#9D808C",
            ["Success"] = "#59C779",
            ["Warning"] = "#FFE044",
            ["Danger"] = "#FF477F",
            ["Cyan"] = "#00D5FF",
            ["NavigationBackground"] = "#120D0F",
            ["TopBarBackground"] = "#110C0E",
            ["OverlayBackground"] = "#E00E090B",
            ["MetricBackground"] = "#1B1216",
        };

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
    };

    public static Dictionary<string, string> CreateDefaultColors() =>
        ColorKeys.ToDictionary(key => key, key => defaults[key], StringComparer.OrdinalIgnoreCase);

    public static CustomThemeSettings Normalize(CustomThemeSettings? theme)
    {
        var result = new CustomThemeSettings
        {
            Name = NormalizeName(theme?.Name),
            Colors = CreateDefaultColors(),
        };

        if (theme?.Colors is null)
            return result;

        foreach (var key in ColorKeys)
        {
            if (TryFind(theme.Colors, key, out var value) && TryNormalizeHex(value, out var normalized))
                result.Colors[key] = normalized;
        }

        return result;
    }

    public static bool TryValidate(
        string? name,
        IReadOnlyDictionary<string, string>? colors,
        out CustomThemeSettings theme,
        out string error)
    {
        theme = new CustomThemeSettings { Name = NormalizeName(name), Colors = [] };
        if (colors is null)
        {
            error = "The theme does not contain a color palette.";
            return false;
        }

        foreach (var key in ColorKeys)
        {
            if (!TryFind(colors, key, out var value))
            {
                error = $"The theme is missing {DisplayName(key)}.";
                return false;
            }

            if (!TryNormalizeHex(value, out var normalized))
            {
                error = $"{DisplayName(key)} must be #RRGGBB or #AARRGGBB.";
                return false;
            }

            theme.Colors[key] = normalized;
        }

        error = string.Empty;
        return true;
    }

    public static string Export(CustomThemeSettings theme)
    {
        if (!TryValidate(theme.Name, theme.Colors, out var validated, out var error))
            throw new InvalidDataException(error);

        return JsonSerializer.Serialize(new CustomThemeFile
        {
            Format = FileFormat,
            Version = FileVersion,
            Name = validated.Name,
            Colors = validated.Colors,
        }, jsonOptions);
    }

    public static CustomThemeSettings Import(string json)
    {
        CustomThemeFile file;
        try
        {
            file = JsonSerializer.Deserialize<CustomThemeFile>(json, jsonOptions)
                   ?? throw new InvalidDataException("The theme file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The selected file is not valid theme JSON.", ex);
        }

        if (!string.Equals(file.Format, FileFormat, StringComparison.Ordinal))
            throw new InvalidDataException("This file is not a Kumori theme.");
        if (file.Version != FileVersion)
            throw new InvalidDataException($"Unsupported theme version {file.Version}; expected {FileVersion}.");
        if (!TryValidate(file.Name, file.Colors, out var validated, out var error))
            throw new InvalidDataException(error);
        return validated;
    }

    public static CustomThemeColorRole Role(string key) =>
        ColorRoles.FirstOrDefault(role => string.Equals(role.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? new CustomThemeColorRole(key, "Other", Regex.Replace(key, "([a-z])([A-Z])", "$1 $2"), "Custom interface color.");

    public static string DisplayName(string key) => Role(key).Label;

    public static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (!HexColour().IsMatch(candidate))
            return false;

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "My theme";
        var trimmed = name.Trim();
        return trimmed[..Math.Min(trimmed.Length, 80)];
    }

    private static bool TryFind(IReadOnlyDictionary<string, string> colors, string key, out string value)
    {
        if (colors.TryGetValue(key, out value!))
            return true;
        var pair = colors.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        value = pair.Value ?? string.Empty;
        return pair.Key is not null;
    }

    [GeneratedRegex("^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColour();
}
