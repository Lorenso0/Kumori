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
        var separator = new DayRowViewModel("2026-07-04", [attempt], isCollapsed: false);
        var day = new PerformanceDayViewModel(new DailyAttemptTrend { Day = "2026-07-04" });

        Assert.Equal("04/07/2026 13:05", attempt.WhenText);
        Assert.Equal("04/07/2026 13:05:06", attempt.WhenExact);
        Assert.Equal("04/07/2026", session.DateText);
        Assert.Equal("04/07/2026", separator.HeaderLine);
        Assert.Equal("04/07/2026", day.DateText);
    }

    [Fact]
    public void DaySeparatorSummarizesItsPlays()
    {
        var separator = new DayRowViewModel("2026-07-04",
        [
            new AttemptRowViewModel(new AttemptSummary { Outcome = "completed", Pp = 123.45 }),
            new AttemptRowViewModel(new AttemptSummary { Outcome = "failed", Pp = 50 }),
        ], isCollapsed: false, ppChange: 6.25);

        Assert.True(separator.IsDayHeader);
        Assert.Equal("PP gained +6.3pp  ·  2 plays  ·  1 completed  ·  123.5pp best", separator.StatsLine);
        Assert.Equal("+6.3pp", separator.PpChangeText);
        Assert.Equal("Collapse day", separator.ToggleLabel);

        separator.UpdatePpChange(-1.25);
        Assert.Equal("-1.3pp", separator.PpChangeText);
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

    [Theory]
    [InlineData("2026-07-16", "today")]
    [InlineData("2026-07-15", "yesterday")]
    [InlineData("2026-07-14", "14/07/2026")]
    public void PerformanceDates_UseTodayYesterdayThenCalendarDates(string dayValue, string expected)
    {
        var day = new PerformanceDayViewModel(
            new DailyAttemptTrend { Day = dayValue },
            today: new DateTime(2026, 7, 16));

        Assert.Equal(expected, day.DateText);
    }

    [Theory]
    [InlineData("completed", false, "")]
    [InlineData("failed", true, "PARTIAL")]
    [InlineData("retried", true, "PARTIAL")]
    [InlineData("quit", true, "PARTIAL")]
    [InlineData("abandoned", true, "PARTIAL")]
    public void AccuracyPresentationMarksEveryUnfinishedOutcomeAsPartial(
        string outcome,
        bool expectedPartial,
        string expectedQualifier)
    {
        var attempt = new AttemptRowViewModel(new AttemptSummary
        {
            Outcome = outcome,
            Accuracy = 100,
        });

        Assert.Equal(expectedPartial, attempt.IsPartialAccuracy);
        Assert.Equal(expectedQualifier, attempt.AccuracyQualifier);
        Assert.Equal("100.00%", attempt.AccuracyText);
    }

    [Fact]
    public void InMemoryMapBestAccuracyIgnoresUnfinishedPerfectPlay()
    {
        var card = new MapCardViewModel("map", new[]
        {
            new AttemptSummary { Id = 2, Outcome = "quit", Accuracy = 100 },
            new AttemptSummary { Id = 1, Outcome = "completed", Accuracy = 98.5 },
        });

        Assert.Equal(98.5, card.BestAccuracy);
        Assert.Equal(99.25, card.AverageAccuracy);
    }

    [Fact]
    public void MapComboTextShowsBestComboAgainstBeatmapMaximum()
    {
        var card = new MapCardViewModel("map", new[]
        {
            new AttemptSummary { Id = 1, Outcome = "completed", Combo = 184, BeatmapMaxCombo = 432 },
        });

        Assert.Equal("184/432", card.ComboText);
    }
}
