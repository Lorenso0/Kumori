using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kumori.Core;
using Kumori.Core.Models;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public partial class PerformanceDayViewModel : ObservableObject
{
    private readonly Func<string, Task<IReadOnlyList<AttemptRowViewModel>>>? _loadAttempts;
    private readonly DateTime _today;
    private bool _attemptsLoaded;

    public PerformanceDayViewModel(
        DailyAttemptTrend model,
        Func<string, Task<IReadOnlyList<AttemptRowViewModel>>>? loadAttempts = null,
        DateTime? today = null)
    {
        Model = model;
        _loadAttempts = loadAttempts;
        _today = (today ?? DateTime.Today).Date;
        CompletionRate = Model.Attempts == 0 ? 0 : Model.Completed * 100.0 / Model.Attempts;
    }

    public DailyAttemptTrend Model { get; }
    public ObservableCollection<AttemptRowViewModel> Attempts { get; } = new();
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingAttempts;
    [ObservableProperty] private string _playsStatus = "Loading plays...";

    public string DateText
    {
        get
        {
            if (!TryParseDay(out var date)) return DisplayDateTime.UnknownDate;
            if (date.Date == _today) return "today";
            if (date.Date == _today.AddDays(-1)) return "yesterday";
            return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }
    }
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
    public string ResultStatsLine => Invariant(
        $"{Model.Attempts:N0} plays  ·  {Model.Completed:N0} completed ({CompletionRate:0}%)  ·  {Model.AverageAccuracy:0.00}% avg  ·  {Model.BestPp:0.0}pp best");
    public string ActivityStatsLine => Invariant(
        $"{Model.ZTotal + Model.XTotal:N0} key presses  ·  {FormatPlaytime(Model.TotalDurationSeconds)} active playtime  ·  {Model.TotalMisses:N0} misses");
    public string ChangeStatsLine => $"PP {PpChangeText}  ·  Rank {RankChangeText}";
    public string PpChangeText => Model.PpChange is { } change
        ? Invariant($"{change:+0.0;-0.0;0.0}pp")
        : "—";
    public string RankChangeText => Model.RankChange is { } change
        ? Invariant($"{change:+0;-0;0}")
        : "—";

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_attemptsLoaded && !IsLoadingAttempts)
        {
            _ = LoadAttemptsAsync();
        }
    }

    private async Task LoadAttemptsAsync()
    {
        if (_loadAttempts is null)
        {
            _attemptsLoaded = true;
            PlaysStatus = "No play details available";
            return;
        }

        IsLoadingAttempts = true;
        PlaysStatus = "Loading plays...";
        try
        {
            var attempts = await _loadAttempts(Model.Day);
            Attempts.Clear();
            foreach (var attempt in attempts)
            {
                Attempts.Add(attempt);
            }
            _attemptsLoaded = true;
            PlaysStatus = attempts.Count == 0 ? "No plays recorded for this day" : "";
        }
        catch
        {
            PlaysStatus = "Could not load plays for this day";
        }
        finally
        {
            IsLoadingAttempts = false;
        }
    }

    private bool TryParseDay(out DateTime date) => DateTime.TryParseExact(
        Model.Day,
        "yyyy-MM-dd",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out date);

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
