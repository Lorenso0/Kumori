using Kumori.Core.Models;
using Serilog;

namespace Kumori.Tracking;

/// <summary>Rebuilds active beatmap media for historical attempts from lazer or configured mirrors.</summary>
public static class HistoricalBeatmapCacheRecovery
{
    public static IReadOnlyList<AttemptSummary> GetPending(IEnumerable<AttemptSummary> attempts) => attempts
        .Where(attempt => attempt.OsuBeatmapId is > 0 && attempt.BeatmapSetId is > 0)
        .GroupBy(attempt => (attempt.Checksum, attempt.OsuBeatmapId, attempt.BeatmapSetId))
        .Select(group => group.First())
        .Where(TosuMediaCache.NeedsRecovery)
        .ToArray();

    public static int Run(
        IEnumerable<AttemptSummary> pending,
        string primaryMirror,
        IReadOnlyList<string>? fallbackMirrors = null,
        Action<HistoricalMapRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var maps = pending.ToArray();
        var recovered = 0;

        for (var index = 0; index < maps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = maps[index];
            var cached = TosuMediaCache.Cache(new TosuMediaInfo
            {
                Checksum = attempt.Checksum,
                BeatmapId = attempt.OsuBeatmapId,
                BeatmapSetId = attempt.BeatmapSetId,
            }, primaryMirror, fallbackMirrors);
            if (cached is not null)
            {
                recovered++;
            }
            progress?.Invoke(new HistoricalMapRecoveryProgress(
                index + 1, maps.Length, $"{attempt.Artist} — {attempt.Title} [{attempt.Difficulty}]", cached is not null));
        }

        Log.Information("Recovered active beatmap media for {Recovered}/{Total} historical maps", recovered, maps.Length);
        return recovered;
    }
}

public sealed record HistoricalMapRecoveryProgress(int Current, int Total, string MapName, bool Succeeded);
