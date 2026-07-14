using System.Globalization;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class DisplayDateTimeTests
{
    [Fact]
    public void CalendarDate_UsesDayMonthYearWithoutTimezoneConversion()
    {
        Assert.Equal("04/07/2026", DisplayDateTime.FormatCalendarDate("2026-07-04"));
    }

    [Fact]
    public void LocalDateTimes_UseDayMonthYearUnderUsCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var localClock = new DateTime(2026, 7, 4, 13, 5, 6, DateTimeKind.Unspecified);
            var localValue = new DateTimeOffset(localClock, TimeZoneInfo.Local.GetUtcOffset(localClock));
            var serialized = localValue.ToString("O", CultureInfo.InvariantCulture);

            Assert.Equal("04/07/2026", DisplayDateTime.FormatLocalDate(serialized));
            Assert.Equal("04/07/2026 13:05", DisplayDateTime.FormatLocalDateTime(serialized));
            Assert.Equal("04/07/2026 13:05:06", DisplayDateTime.FormatLocalDateTimeWithSeconds(serialized));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void InvalidValues_DoNotLeakStoredTimestamps()
    {
        Assert.Equal(DisplayDateTime.UnknownDate, DisplayDateTime.FormatCalendarDate("2026/07/04"));
        Assert.Equal(DisplayDateTime.UnknownDate, DisplayDateTime.FormatLocalDateTime("not-a-date"));
    }
}
