using System.Collections.Concurrent;
using Kumori.FarmFinder;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using Serilog;

namespace Kumori.App.FarmFinder;

/// <summary>
/// Returns persisted exact ratings and calculates cache misses locally before
/// falling back to the official difficulty-attributes endpoint.
/// </summary>
internal sealed class FarmStarRatingCalculator : IFarmStarRatingCalculator
{
    private const string calculatorVersion = "osu-api/v2-normalized-groups-v2";
    private readonly IFarmStarRatingCache persistentCache;
    private readonly OsuApiClient osuApi;
    private readonly HinamizawaStarRatingClient hinamizawa;
    private readonly FarmBeatmapFileCache beatmapFiles;
    private readonly ConcurrentDictionary<RatingKey, double> completed = new();
    private readonly ConcurrentDictionary<RatingKey, Lazy<Task<double?>>> calculations = new();
    private readonly Lazy<Task> loadPersistedRatings;

    public FarmStarRatingCalculator(
        IFarmStarRatingCache persistentCache,
        OsuApiClient osuApi,
        HinamizawaStarRatingClient hinamizawa,
        FarmBeatmapFileCache beatmapFiles)
    {
        this.persistentCache = persistentCache;
        this.osuApi = osuApi;
        this.hinamizawa = hinamizawa;
        this.beatmapFiles = beatmapFiles;
        loadPersistedRatings = new Lazy<Task>(
            LoadPersistedRatingsAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<double?> CalculateAsync(
        FarmBeatmap beatmap,
        IReadOnlyList<FarmMod> mods,
        CancellationToken cancellationToken = default)
    {
        if (mods.Count == 0)
            return beatmap.StarRating;

        var modsKey = CreateModsKey(mods);
        var key = new RatingKey(beatmap.BeatmapId, modsKey);
        await loadPersistedRatings.Value.WaitAsync(cancellationToken);
        if (completed.TryGetValue(key, out var completedRating))
            return completedRating;

        var calculation = calculations.GetOrAdd(
            key,
            _ => new Lazy<Task<double?>>(
                () => CalculateAndPersistAsync(key, beatmap, mods.ToArray(), cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await calculation.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            calculations.TryRemove(key, out _);
        }
    }

    private async Task LoadPersistedRatingsAsync()
    {
        try
        {
            var cached = await persistentCache.LoadAsync(calculatorVersion);
            foreach (var rating in cached)
            {
                if (rating.StarRating > 0 && double.IsFinite(rating.StarRating))
                {
                    completed[new RatingKey(
                        rating.BeatmapId,
                        rating.ModsKey)] = rating.StarRating;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Farm Finder star cache preload failed");
        }
    }

    private async Task<double?> CalculateAndPersistAsync(
        RatingKey key,
        FarmBeatmap beatmap,
        IReadOnlyList<FarmMod> mods,
        CancellationToken cancellationToken)
    {
        try
        {
            var rating = await hinamizawa.GetStarRatingAsync(
                             beatmap,
                             mods,
                             cancellationToken)
                         ?? await TryCalculateLocallyAsync(
                             beatmap,
                             mods,
                             cancellationToken)
                         ?? await osuApi.GetDifficultyStarRatingAsync(
                    beatmap.BeatmapId,
                    mods,
                    cancellationToken);
            completed[key] = rating;
            try
            {
                await persistentCache.SaveAsync(
                    beatmap.BeatmapId,
                    key.Mods,
                    calculatorVersion,
                    rating,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "Farm Finder could not persist a star rating for beatmap {BeatmapId}",
                    beatmap.BeatmapId);
            }
            return rating;
        }
        catch (OsuApiAuthenticationException)
        {
            // A downloaded cache remains usable without credentials. Only
            // combinations absent from it fall back to a labelled base rating.
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Farm Finder star calculation failed for beatmap {BeatmapId}",
                beatmap.BeatmapId);
            return null;
        }
    }

    private async Task<double?> TryCalculateLocallyAsync(
        FarmBeatmap beatmap,
        IReadOnlyList<FarmMod> mods,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await beatmapFiles.GetAsync(beatmap, cancellationToken);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return await Task.Run(() =>
            {
                var ruleset = new OsuRuleset();
                var calculator = ruleset.CreateDifficultyCalculator(
                    new FlatWorkingBeatmap(path, checked((int)beatmap.BeatmapId)));
                var localBase = calculator.Calculate().StarRating;
                var tolerance = Math.Max(0.08, beatmap.StarRating * 0.015);
                if (Math.Abs(localBase - beatmap.StarRating) > tolerance)
                {
                    Log.Debug(
                        "Farm Finder rejected stale beatmap {BeatmapId}: local {LocalStars}, indexed {IndexedStars}",
                        beatmap.BeatmapId,
                        localBase,
                        beatmap.StarRating);
                    return (double?)null;
                }

                var configuredMods = mods.Select(mod =>
                {
                    var settings = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                                       string.IsNullOrWhiteSpace(mod.SettingsJson)
                                           ? "{}"
                                           : mod.SettingsJson)
                                   ?? [];
                    return new APIMod
                    {
                        Acronym = mod.NormalizedAcronym,
                        Settings = settings,
                    }.ToMod(ruleset);
                }).ToArray();
                if (configuredMods.Any(mod => mod is UnknownMod))
                    return null;

                var rating = calculator.Calculate(configuredMods).StarRating;
                return rating > 0 && double.IsFinite(rating)
                    ? rating
                    : null;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Debug(
                exception,
                "Farm Finder local star calculation failed for beatmap {BeatmapId}",
                beatmap.BeatmapId);
            return null;
        }
    }

    private static string CreateModsKey(IReadOnlyList<FarmMod> mods) =>
        string.Join(
            "+",
            mods.Select(mod =>
                    $"{mod.NormalizedAcronym}:{FarmFinderValidation.CanonicalJson(mod.SettingsJson)}")
                .Order(StringComparer.Ordinal));

    private readonly record struct RatingKey(long BeatmapId, string Mods);
}
