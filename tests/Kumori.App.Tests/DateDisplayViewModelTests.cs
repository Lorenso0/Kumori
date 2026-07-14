using System.Globalization;
using Kumori.App.ViewModels;
using Kumori.Core;
using Kumori.Core.Models;
using Xunit;

namespace Kumori.App.Tests;

public sealed class DateDisplayViewModelTests
{
    [Fact]
    public void HistoryAndAnalyticsDates_UseDayMonthYear()
    {
        var localClock = new DateTime(2026, 7, 4, 13, 5, 6, DateTimeKind.Unspecified);
        var startedAt = new DateTimeOffset(localClock, TimeZoneInfo.Local.GetUtcOffset(localClock))
            .ToString("O", CultureInfo.InvariantCulture);

        var attempt = new AttemptRowViewModel(new AttemptSummary { StartedAt = startedAt });
        var session = new SessionRowViewModel(new SessionSummary { StartedAt = startedAt });
        var day = new PerformanceDayViewModel(new DailyAttemptTrend { Day = "2026-07-04" });

        Assert.Equal("04/07/2026 13:05", attempt.WhenText);
        Assert.Equal("04/07/2026 13:05:06", attempt.WhenExact);
        Assert.Equal("04/07/2026", session.DateText);
        Assert.Equal("04/07/2026", day.DateText);
    }

    [Fact]
    public void InvalidHistoryDates_DoNotLeakRawValues()
    {
        var attempt = new AttemptRowViewModel(new AttemptSummary { StartedAt = "raw-timestamp" });
        var day = new PerformanceDayViewModel(new DailyAttemptTrend { Day = "2026/07/04" });

        Assert.Equal(DisplayDateTime.UnknownDate, attempt.WhenText);
        Assert.Equal(DisplayDateTime.UnknownDate, attempt.WhenExact);
        Assert.Equal(DisplayDateTime.UnknownDate, day.DateText);
    }
}
