using System.Globalization;
using Kumori.Core.Models;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public sealed class PerformanceDayViewModel
{
    public PerformanceDayViewModel(DailyAttemptTrend model)
    {
        Model = model;
        CompletionRate = Model.Attempts == 0 ? 0 : Model.Completed * 100.0 / Model.Attempts;
    }

    public DailyAttemptTrend Model { get; }
    public string DateText => LocalTimeDisplay.CalendarDate(Model.Day);
    public string DayText => DateTime.TryParseExact(Model.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date.ToString("dddd", CultureInfo.InvariantCulture) : "Recorded day";
    public string PlaysText => Invariant($"{Model.Attempts:N0}");
    public string CompletedText => Invariant($"{Model.Completed:N0}");
    // ProgressBar.Value binds TwoWay by default in WPF, so this display value
    // needs a public setter even though the view never edits it interactively.
    public double CompletionRate { get; set; }
    public string CompletionText => Invariant($"{CompletionRate:0}%");
    public string AccuracyText => Invariant($"{Model.AverageAccuracy:0.00}%");
    public string BestPpText => Invariant($"{Model.BestPp:0.0}pp");
}
