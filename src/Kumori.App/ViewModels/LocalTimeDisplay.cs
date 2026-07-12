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
        => Parse(value)?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? fallback;

    public static string DateTimeWithSeconds(string? value, string fallback = "")
        => Parse(value)?.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? fallback;

    public static string Relative(string? value, DateTimeOffset? now = null, string fallback = "")
    {
        if (Parse(value) is not { } timestamp) return fallback;
        var elapsed = (now ?? DateTimeOffset.Now) - timestamp;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 45) return "just now";
        if (elapsed.TotalMinutes < 60) return Ago((int)Math.Floor(elapsed.TotalMinutes), "minute");
        if (elapsed.TotalHours < 24) return Ago((int)Math.Floor(elapsed.TotalHours), "hour");
        if (elapsed.TotalDays < 7) return Ago((int)Math.Floor(elapsed.TotalDays), "day");
        if (elapsed.TotalDays < 30) return Ago((int)Math.Floor(elapsed.TotalDays / 7), "week");
        if (elapsed.TotalDays < 365) return Ago((int)Math.Floor(elapsed.TotalDays / 30), "month");
        return Ago((int)Math.Floor(elapsed.TotalDays / 365), "year");
    }

    private static string Ago(int value, string unit) =>
        $"{Math.Max(1, value)} {unit}{(value == 1 ? "" : "s")} ago";

    public static string DayKey(string? value)
        => Parse(value)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";
}
