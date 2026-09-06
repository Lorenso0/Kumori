using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumori.FarmFinder;
using Serilog;

namespace Kumori.App.FarmFinder;

/// <summary>
/// Reads Hinamizawa's cached rosu-pp difficulty table. One request returns all
/// common ranked mod combinations for a beatmap.
/// </summary>
internal sealed class HinamizawaStarRatingClient
{
    private const string endpoint =
        "https://mirror.hinamizawa.ai/v3/osu/pp/";
    private const string customEndpoint =
        "https://mirror.hinamizawa.ai/v3/osu/pp-calc/";
    private static readonly JsonSerializerOptions jsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HttpClient sharedHttp = CreateHttpClient();
    private static readonly HashSet<string> supportedDifficultyMods =
        new(["EZ", "HD", "HR", "DT", "FL"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> difficultyNeutralMods =
        new(["NF", "SD", "PF", "SO", "CL"], StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient http;
    private readonly SemaphoreSlim requestGate = new(48, 48);
    private readonly ConcurrentDictionary<long, Lazy<Task<RatingTable?>>> lookups = new();
    private readonly ConcurrentDictionary<long, RatingTable> completed = new();
    private readonly ConcurrentDictionary<CustomRatingKey, Lazy<Task<double?>>> customLookups = new();
    private readonly ConcurrentDictionary<CustomRatingKey, double> customCompleted = new();

    public HinamizawaStarRatingClient(HttpClient? httpClient = null)
    {
        http = httpClient ?? sharedHttp;
    }

    public async Task<double?> GetStarRatingAsync(
        FarmBeatmap beatmap,
        IReadOnlyList<FarmMod> mods,
        CancellationToken cancellationToken = default)
    {
        var requested = NormalizeRequestedMods(mods);
        var legacyMods = BuildLegacyModString(mods);
        if (requested is null && legacyMods is null)
            return null;

        var table = await GetTableAsync(beatmap.BeatmapId, cancellationToken);
        if (table is null)
            return null;

        var noMod = FindRating(
            table,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var tolerance = Math.Max(0.08, beatmap.StarRating * 0.015);
        if (noMod is null || Math.Abs(noMod.Value - beatmap.StarRating) > tolerance)
            return null;

        if (requested is not null && FindRating(table, requested) is { } commonRating)
            return commonRating;
        return legacyMods is null
            ? null
            : await GetCustomRatingAsync(
                beatmap.BeatmapId,
                legacyMods,
                cancellationToken);
    }

    private async Task<RatingTable?> GetTableAsync(
        long beatmapId,
        CancellationToken cancellationToken)
    {
        if (completed.TryGetValue(beatmapId, out var completedTable))
            return completedTable;

        var lookup = lookups.GetOrAdd(
            beatmapId,
            _ => new Lazy<Task<RatingTable?>>(
                () => DownloadTableAsync(beatmapId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var table = await lookup.Value.WaitAsync(cancellationToken);
            if (table is not null)
                completed[beatmapId] = table;
            return table;
        }
        finally
        {
            lookups.TryRemove(beatmapId, out _);
        }
    }

    private async Task<double?> GetCustomRatingAsync(
        long beatmapId,
        string mods,
        CancellationToken cancellationToken)
    {
        var key = new CustomRatingKey(beatmapId, mods);
        if (customCompleted.TryGetValue(key, out var completedRating))
            return completedRating;

        var lookup = customLookups.GetOrAdd(
            key,
            _ => new Lazy<Task<double?>>(
                () => DownloadCustomRatingAsync(key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var rating = await lookup.Value.WaitAsync(cancellationToken);
            if (rating is { } value)
                customCompleted[key] = value;
            return rating;
        }
        finally
        {
            customLookups.TryRemove(key, out _);
        }
    }

    private async Task<RatingTable?> DownloadTableAsync(
        long beatmapId,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            using var response = await http.GetAsync(
                $"{endpoint}{beatmapId}/all?mode=0",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<RatingTable>(
                stream,
                jsonOptions,
                cancellationToken);
            return payload is { Success: true, Mods.Count: > 0 }
                ? payload
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Debug(
                exception,
                "Hinamizawa star lookup failed for beatmap {BeatmapId}",
                beatmapId);
            return null;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task<double?> DownloadCustomRatingAsync(
        CustomRatingKey key,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            using var response = await http.GetAsync(
                $"{customEndpoint}{key.BeatmapId}?mods={Uri.EscapeDataString(key.Mods)}&accuracy=100&misses=0&mode=0",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CustomRatingResponse>(
                stream,
                jsonOptions,
                cancellationToken);
            var stars = payload?.Difficulty?.Stars;
            return payload is { Success: true } && stars is > 0 && double.IsFinite(stars.Value)
                ? stars
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Debug(
                exception,
                "Hinamizawa custom star lookup failed for beatmap {BeatmapId} with {Mods}",
                key.BeatmapId,
                key.Mods);
            return null;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private static HashSet<string>? NormalizeRequestedMods(
        IReadOnlyList<FarmMod> mods)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            var acronym = mod.NormalizedAcronym;
            if (FarmFinderValidation.CanonicalJson(mod.SettingsJson) != "{}")
                return null;
            if (difficultyNeutralMods.Contains(acronym))
                continue;
            if (!supportedDifficultyMods.Contains(acronym))
                return null;
            result.Add(acronym);
        }
        return result;
    }

    private static string? BuildLegacyModString(IReadOnlyList<FarmMod> mods)
    {
        var acronyms = new List<string>(mods.Count);
        foreach (var mod in mods)
        {
            var acronym = mod.NormalizedAcronym;
            if (FarmFinderValidation.CanonicalJson(mod.SettingsJson) != "{}" ||
                acronym.Length != 2)
                return null;
            if (acronym == "CL")
                continue;
            acronyms.Add(acronym);
        }
        return acronyms.Count == 0
            ? "NM"
            : string.Concat(acronyms.OrderBy(ModOrder));
    }

    private static int ModOrder(string acronym) => acronym switch
    {
        "NF" => 0,
        "EZ" => 1,
        "HD" => 2,
        "HR" => 3,
        "SD" => 4,
        "DT" => 5,
        "HT" => 6,
        "NC" => 7,
        "FL" => 8,
        "SO" => 9,
        "PF" => 10,
        _ => 100,
    };

    private static HashSet<string>? ParseModKey(string key)
    {
        if (key.Equals("NM", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (key.Length == 0 || key.Length % 2 != 0)
            return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < key.Length; offset += 2)
            result.Add(key.Substring(offset, 2));
        return result;
    }

    private static double? FindRating(
        RatingTable table,
        IReadOnlySet<string> requested)
    {
        foreach (var entry in table.Mods)
        {
            var available = ParseModKey(entry.Key);
            if (available is null || !available.SetEquals(requested))
                continue;
            var stars = entry.Value.Stars;
            return stars > 0 && double.IsFinite(stars) ? stars : null;
        }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Kumori-FarmFinder/0.8.10 (+https://github.com/Lorenzo0111/Kumori)");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    internal sealed record RatingTable(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("mods")]
        Dictionary<string, RatingValue> Mods);

    internal sealed record RatingValue(
        [property: JsonPropertyName("stars")] double Stars);

    private sealed record CustomRatingResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("difficulty")] CustomDifficulty? Difficulty);

    private sealed record CustomDifficulty(
        [property: JsonPropertyName("stars")] double Stars);

    private readonly record struct CustomRatingKey(long BeatmapId, string Mods);
}
