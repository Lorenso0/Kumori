using System.Globalization;

namespace Kumori.Core;

/// <summary>Invariant formatting for dates shown to users.</summary>
public static class DisplayDateTime
{
    public const string UnknownDate = "Unknown date";

    public static DateTimeOffset? ParseLocal(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : null;

    public static string FormatCalendarDate(string? value, string fallback = UnknownDate)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : fallback;

    public static string FormatLocalDate(string? value, string fallback = UnknownDate)
        => ParseLocal(value)?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? fallback;

    public static string FormatLocalDateTime(string? value, string fallback = UnknownDate)
        => ParseLocal(value)?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? fallback;

    public static string FormatLocalDateTimeWithSeconds(string? value, string fallback = UnknownDate)
        => ParseLocal(value)?.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? fallback;

    public static string FormatLocalDateTime(DateTimeOffset value)
        => value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string FormatLocalDateTimeWithSeconds(DateTimeOffset value)
        => value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FormatDateTimeWithSeconds(DateTime value)
    {
        var display = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return display.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
