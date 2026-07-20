using System.Globalization;
using System.Windows.Data;
using Kumori.Core.Models;
using Kumori.Gameplay;

namespace Kumori.App.ViewModels;

/// <summary>Formats the serialized target used by a BPM Adjust score badge.</summary>
public sealed class ModEntryToBpmTargetConverter : IValueConverter
{
    public static readonly ModEntryToBpmTargetConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => TargetText(value as ModEntry);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static string TargetText(ModEntry? mod)
    {
        if (mod is null || !mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        double? target = BpmAdjustSettings.Parse(mod.SettingsJson).TargetBpm;
        return target?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>Keeps normal mod badges compact while making room for a BPM target.</summary>
public sealed class ModEntryToScoreBadgeWidthConverter : IValueConverter
{
    public static readonly ModEntryToScoreBadgeWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double standardWidth = string.Equals(parameter as string, "Details", StringComparison.Ordinal)
            ? 34d
            : 27d;
        if (value is not ModEntry mod ||
            !mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            return standardWidth;

        string target = ModEntryToBpmTargetConverter.TargetText(mod);
        return string.IsNullOrEmpty(target)
            ? 34d
            : Math.Ceiling(Math.Max(62d, 42d + target.Length * 6d));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
