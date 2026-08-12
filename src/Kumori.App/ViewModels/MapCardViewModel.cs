using Kumori.Core.Models;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public sealed class MapCardViewModel
{
    private readonly AttemptSummary _artworkSummary;
    private bool _artworkResolved;
    private string? _artworkSource;

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
        _artworkSummary = representative;
        PlayCount = ordered.Length;
        var completed = ordered
            .Where(attempt => string.Equals(attempt.Outcome, "completed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        BestPp = completed
            .Select(attempt => attempt.Pp)
            .DefaultIfEmpty(0)
            .Max();
        BestAccuracy = completed
            .Select(attempt => attempt.Accuracy)
            .DefaultIfEmpty(0)
            .Max();
        BestCombo = ordered.Max(attempt => attempt.Combo);
        BeatmapMaxCombo = ordered.Max(attempt => attempt.BeatmapMaxCombo);
        AverageAccuracy = ordered.Average(attempt => attempt.Accuracy);
        AveragePp = ordered.Average(attempt => attempt.Pp);
        AverageCombo = ordered.Average(attempt => attempt.Combo);
        CompletionRate = ordered.Count(attempt => string.Equals(attempt.Outcome, "completed", StringComparison.OrdinalIgnoreCase)) * 100.0 / ordered.Length;
        LastPlayed = row.WhenText;
        Stars = row.StarsText;
    }

    public MapCardViewModel(MapSummary map)
    {
        MapKey = map.MapKey;
        Artist = map.Artist;
        Title = map.Title;
        Difficulty = map.Difficulty;
        Mapper = map.Mapper;
        var representative = new AttemptSummary
        {
            Id = map.LastAttemptId,
            OsuBeatmapId = map.OsuBeatmapId,
            BeatmapSetId = map.BeatmapSetId,
            Checksum = map.Checksum,
            Artist = map.Artist,
            Title = map.Title,
            Difficulty = map.Difficulty,
            Mapper = map.Mapper,
            StartedAt = map.LastStartedAt,
            Stars = map.Stars,
        };
        var row = new AttemptRowViewModel(representative);
        _artworkSummary = representative;
        PlayCount = map.PlayCount;
        BestPp = map.BestPp;
        BestAccuracy = map.BestAccuracy;
        BestCombo = map.BestCombo;
        BeatmapMaxCombo = map.BeatmapMaxCombo;
        AverageAccuracy = map.AverageAccuracy;
        AveragePp = map.AveragePp;
        AverageCombo = map.AverageCombo;
        CompletionRate = map.PlayCount == 0 ? 0 : map.CompletedCount * 100.0 / map.PlayCount;
        LastPlayed = row.WhenText;
        Stars = row.StarsText;
    }

    public string MapKey { get; }
    public string Artist { get; }
    public string Title { get; }
    public string Difficulty { get; }
    public string Mapper { get; }
    public string? ArtworkSource
    {
        get
        {
            if (!_artworkResolved)
            {
                _artworkSource = BeatmapArtworkResolver.Resolve(_artworkSummary);
                _artworkResolved = true;
            }

            return _artworkSource;
        }
    }
    public int PlayCount { get; }
    public double BestPp { get; }
    public double BestAccuracy { get; }
    public int BestCombo { get; }
    public int BeatmapMaxCombo { get; }
    public double AverageAccuracy { get; }
    public double AveragePp { get; }
    public double AverageCombo { get; }
    public double CompletionRate { get; }
    public string LastPlayed { get; }
    public string Stars { get; }
    public string PlayCountText => Invariant($"{PlayCount:N0} plays");
    public string AveragePerformanceText => Invariant($"{AverageAccuracy:0.00}%  ·  {AveragePp:0.0}pp");
    public string BestPerformanceText => Invariant($"{BestAccuracy:0.00}%  ·  {BestPp:0.0}pp");
    public string ComboText => BeatmapMaxCombo > 0
        ? Invariant($"{BestCombo:N0}/{BeatmapMaxCombo:N0}")
        : Invariant($"{BestCombo:N0}x");
    public string BestStats => Invariant($"{BestAccuracy:0.00}%  ·  {BestPp:0.0}pp  ·  {BestCombo:N0}x");
    public string AverageStats => Invariant($"{AverageAccuracy:0.00}%  ·  {AveragePp:0.0}pp  ·  {AverageCombo:0}x");
    public string BestLine => Invariant($"BEST  {BestAccuracy:0.00}%  ·  {BestPp:0.0}pp  ·  {BestCombo:N0}x");
    public string AverageLine => Invariant($"AVG  {AverageAccuracy:0.00}%  ·  {AveragePp:0.0}pp  ·  {AverageCombo:0}x");
    public string CompletionText => Invariant($"{CompletionRate:0}% completed");
}
