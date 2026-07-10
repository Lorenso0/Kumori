using System.Globalization;
using Kumori.Core.Models;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

/// <summary>
/// Session separator row. Mirrors the Python history session line
/// (osu_tracking.py): a time marker plus two summary lines.
/// </summary>
public sealed class SessionRowViewModel : HistoryRowViewModel
{
    public SessionSummary Model { get; }

    private readonly long? _activeSessionId;

    public SessionRowViewModel(SessionSummary model, bool isCollapsed = false, long? activeSessionId = null)
    {
        Model = model;
        IsCollapsed = isCollapsed;
        _activeSessionId = activeSessionId;
    }

    public bool IsCollapsed { get; }
    public string Chevron => IsCollapsed ? ">" : "v";

    public bool IsActive => _activeSessionId == Model.Id && string.IsNullOrEmpty(Model.EndedAt);

    public string TimeText => LocalTimeDisplay.Time(Model.StartedAt);

    public string TimeColor => IsActive ? "#4ADE80" : "#C4B5FD";

    public string HeaderLine
    {
        get
        {
            var marker = Model.Legacy
                ? "legacy"
                : Model.Interrupted
                    ? "interrupted"
                    : IsActive ? "active" : "session";
            return Invariant($"{Elapsed}  ·  {Model.AttemptCount:N0} plays  ·  K1 {Model.ZCount:N0}  K2 {Model.XCount:N0}  ·  {marker}");
        }
    }

    public string StatsLine
    {
        get
        {
            var rate = Model.AttemptCount > 0 ? Model.CompletedCount * 100.0 / Model.AttemptCount : 0;
            var gain = Model.AccountPpGain;
            var gainText = (gain >= 0 ? "+" : "") + gain.ToString("0.0", CultureInfo.InvariantCulture);
            return Invariant($"{rate:0}% completion rate  ·  {Model.BestPp:0.0}pp best  ·  {gainText} pp");
        }
    }

    private string Elapsed => FormatElapsed(Model.StartedAt, Model.EndedAt);

    private static string FormatElapsed(string start, string? end)
    {
        if (LocalTimeDisplay.Parse(start) is not { } a)
        {
            return "0m 00s";
        }
        var b = LocalTimeDisplay.Parse(end) is { } parsed
            ? parsed
            : DateTimeOffset.Now;
        var seconds = Math.Max(0, (int)(b - a).TotalSeconds);
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var secs = seconds % 60;
        return hours > 0
            ? Invariant($"{hours}h {minutes:00}m")
            : Invariant($"{minutes}m {secs:00}s");
    }
}
