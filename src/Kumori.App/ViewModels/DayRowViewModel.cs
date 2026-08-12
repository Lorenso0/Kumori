using Kumori.Core;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

/// <summary>Calendar-day separator for the main play history.</summary>
public sealed class DayRowViewModel : HistoryRowViewModel
{
    private readonly string _resultStatsLine;
    private double? _ppChange;

    public DayRowViewModel(
        string dayKey,
        IReadOnlyCollection<AttemptRowViewModel> attempts,
        bool isCollapsed,
        double? ppChange = null)
    {
        DayKey = dayKey;
        IsCollapsed = isCollapsed;
        _ppChange = ppChange;
        HeaderLine = LocalTimeDisplay.CalendarDate(dayKey, DisplayDateTime.UnknownDate);

        var completed = attempts.Count(attempt =>
            string.Equals(attempt.Model.Outcome, "completed", StringComparison.OrdinalIgnoreCase));
        var bestPp = attempts.Count == 0 ? 0 : attempts.Max(attempt => attempt.Model.Pp);
        var keyPresses = attempts.Sum(attempt =>
            (long)attempt.Model.Key1Count + attempt.Model.Key2Count);
        // Attempt duration is time actively spent in a map. Session gaps and
        // idle time are deliberately not represented in this daily total.
        var activePlaytime = FormatPlaytime(attempts.Sum(attempt =>
            Math.Max(0, attempt.Model.DurationSeconds)));
        _resultStatsLine = Invariant($"{attempts.Count:N0} plays  ·  {completed:N0} completed  ·  {bestPp:0.0}pp best");
        ActivityStatsLine = Invariant($"{keyPresses:N0} key presses  ·  {activePlaytime} active playtime");
    }

    public string DayKey { get; }
    public string HeaderLine { get; }
    public string PrimaryStatsLine => $"PP gained {PpChangeText}  ·  {_resultStatsLine}";
    public string ActivityStatsLine { get; }
    public string StatsLine => $"{PrimaryStatsLine}  ·  {ActivityStatsLine}";
    public string PpChangeText => _ppChange is { } change
        ? Invariant($"{change:+0.0;-0.0;0.0}pp")
        : "—";
    public bool IsCollapsed { get; }
    public string ToggleLabel => IsCollapsed ? "Expand day" : "Collapse day";

    public void UpdatePpChange(double? ppChange)
    {
        if (_ppChange == ppChange)
        {
            return;
        }

        _ppChange = ppChange;
        OnPropertyChanged(nameof(PpChangeText));
        OnPropertyChanged(nameof(PrimaryStatsLine));
        OnPropertyChanged(nameof(StatsLine));
    }

    private static string FormatPlaytime(double seconds)
    {
        var totalMinutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0
            ? Invariant($"{hours}h {minutes:00}m")
            : Invariant($"{minutes}m");
    }
}
