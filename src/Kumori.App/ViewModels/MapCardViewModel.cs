using Kumori.Core.Models;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public sealed class MapCardViewModel
{
    public MapCardViewModel(string mapKey, IReadOnlyList<AttemptSummary> attempts)
    {
        var ordered = attempts.OrderByDescending(attempt => attempt.Id).ToArray();
        var representative = ordered[0];
        var row = new AttemptRowViewModel(representative);
        MapKey = mapKey;
        Artist = representative.Artist;
        Title = representative.Title;
        Difficulty = representative.Difficulty;
        Mapper = representative.Mapper;
        ArtworkSource = row.ArtworkSource;
        PlayCount = ordered.Length;
        BestPp = ordered.Max(attempt => attempt.Pp);
        BestAccuracy = ordered.Max(attempt => attempt.Accuracy);
        BestCombo = ordered.Max(attempt => attempt.Combo);
        AverageAccuracy = ordered.Average(attempt => attempt.Accuracy);
        AveragePp = ordered.Average(attempt => attempt.Pp);
        AverageCombo = ordered.Average(attempt => attempt.Combo);
        CompletionRate = ordered.Count(attempt => string.Equals(attempt.Outcome, "completed", StringComparison.OrdinalIgnoreCase)) * 100.0 / ordered.Length;
        LastPlayed = row.WhenText;
        Stars = row.StarsText;
    }

    public string MapKey { get; }
    public string Artist { get; }
    public string Title { get; }
    public string Difficulty { get; }
    public string Mapper { get; }
    public string? ArtworkSource { get; }
    public int PlayCount { get; }
    public double BestPp { get; }
    public double BestAccuracy { get; }
    public int BestCombo { get; }
    public double AverageAccuracy { get; }
    public double AveragePp { get; }
    public double AverageCombo { get; }
    public double CompletionRate { get; }
    public string LastPlayed { get; }
    public string Stars { get; }
    public string PlayCountText => Invariant($"{PlayCount:N0} plays");
    public string BestLine => Invariant($"BEST  {BestAccuracy:0.00}%  ·  {BestPp:0.0}pp  ·  {BestCombo:N0}x");
    public string AverageLine => Invariant($"AVG  {AverageAccuracy:0.00}%  ·  {AveragePp:0.0}pp  ·  {AverageCombo:0}x");
    public string CompletionText => Invariant($"{CompletionRate:0}% completed");
}
