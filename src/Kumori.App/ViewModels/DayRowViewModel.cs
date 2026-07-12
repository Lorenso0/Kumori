using System.Globalization;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public sealed class DayRowViewModel : HistoryRowViewModel
{
    public DayRowViewModel(string dayKey, IReadOnlyCollection<AttemptRowViewModel> attempts, bool isCollapsed)
    {
        DayKey = dayKey;
        IsCollapsed = isCollapsed;
        HeaderLine = DateTime.TryParse(dayKey, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day)
            ? day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : dayKey;

        var completed = attempts.Count(a => string.Equals(a.Model.Outcome, "completed", StringComparison.OrdinalIgnoreCase));
        var pp = attempts.Count == 0 ? 0 : attempts.Max(a => a.Model.Pp);
        var key1 = attempts.Sum(a => a.Model.Key1Count);
        var key2 = attempts.Sum(a => a.Model.Key2Count);
        StatsLine = Invariant($"{attempts.Count:N0} plays  -  {completed:N0} completed  -  K1 {key1:N0}  K2 {key2:N0}  -  {pp:0.0}pp best");
    }

    public string DayKey { get; }
    public string HeaderLine { get; }
    public string StatsLine { get; }
    public bool IsCollapsed { get; }
    public string Chevron => IsCollapsed ? ">" : "v";
}
