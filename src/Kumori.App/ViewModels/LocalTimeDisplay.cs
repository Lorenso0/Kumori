using System.Globalization;

namespace Kumori.App.ViewModels;

internal static class LocalTimeDisplay
{
    public static DateTimeOffset? Parse(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : null;

    public static string Time(string? value, string fallback = "")
        => Parse(value)?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? fallback;

    public static string TimeWithSeconds(string? value, string fallback = "")
        => Parse(value)?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? fallback;

    public static string DateTime(string? value, string fallback = "")
        => Parse(value)?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? fallback;

    public static string DateTimeWithSeconds(string? value, string fallback = "")
        => Parse(value)?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? fallback;

    public static string DayKey(string? value)
        => Parse(value)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";
}
