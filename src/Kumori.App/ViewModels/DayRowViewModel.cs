using Kumori.Core;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

/// <summary>Calendar-day separator for the main play history.</summary>
public sealed class DayRowViewModel : HistoryRowViewModel
{
    private readonly string _playStatsLine;
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
        _playStatsLine = Invariant($"{attempts.Count:N0} plays  ·  {completed:N0} completed  ·  {bestPp:0.0}pp best");
    }

    public string DayKey { get; }
    public string HeaderLine { get; }
    public string StatsLine => $"PP gained {PpChangeText}  ·  {_playStatsLine}";
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
        OnPropertyChanged(nameof(StatsLine));
    }
}
