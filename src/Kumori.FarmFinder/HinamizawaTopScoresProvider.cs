using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kumori.FarmFinder;

/// <summary>
/// Reads public player best scores from the documented Hinamizawa mirror.
/// Calls are proactively paced below the advertised 500 requests/minute cap.
/// A complete response is returned before the repository replaces a player's
/// existing cached score set.
/// </summary>
public sealed class HinamizawaTopScoresProvider :
    IPlayerTopScoresProvider,
    IPlayerTopScoresProviderMetadata,
    IOsuRateLimitSource,
    IDisposable
{
    public const string ApiBaseUrl =
        "https://mirror.hinamizawa.ai/api/v1/hinai/player/";

    private const int MaximumRetryCount = 5;
    private static readonly TimeSpan productionRequestInterval =
        TimeSpan.FromMilliseconds(125);
    private static readonly JsonSerializerOptions jsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IRankedModCatalog rankedMods;
    private readonly IClockRateCalculator clockRates;
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;
    private readonly TimeSpan requestInterval;
    private readonly SemaphoreSlim requestGate = new(8, 8);
    private readonly SemaphoreSlim pacingGate = new(1, 1);
    private readonly object rateLimitGate = new();
    private DateTimeOffset nextRequestAt;
    private DateTimeOffset sharedRateLimitUntil;

    public HinamizawaTopScoresProvider(
        IRankedModCatalog rankedMods,
        IClockRateCalculator clockRates,
        HttpClient? httpClient = null,
        TimeSpan? minimumRequestInterval = null)
    {
        this.rankedMods = rankedMods;
        this.clockRates = clockRates;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        ownsHttpClient = httpClient is null;
        requestInterval = minimumRequestInterval ?? productionRequestInterval;
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Kumori-FarmFinder/0.8.4 (+https://github.com/Lorenzo0111/Kumori)");
        if (!http.DefaultRequestHeaders.Accept.Any())
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string SourceName => "Hinamizawa";
    public int RecommendedConcurrency => 8;
    public int RequestsPerMinute => 500;

    public event Action<DateTimeOffset>? RateLimited;

    public async Task<PlayerScoresPayload> GetTopScoresAsync(
        FarmPlayer player,
        CancellationToken cancellationToken = default)
    {
        if (player.UserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(player));

        using var response = await SendAsync(
            player.UserId,
            cancellationToken);
        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        ScoresEnvelope payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ScoresEnvelope>(
                          stream,
                          jsonOptions,
                          cancellationToken)
                      ?? throw new InvalidDataException(
                          "Hinamizawa returned an empty top-score response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Hinamizawa returned a malformed top-score response.",
                exception);
        }

        if (payload.Scores is null)
            throw new InvalidDataException(
                "Hinamizawa returned a top-score response without scores.");

        var scores = new List<FarmScore>(payload.Scores.Length);
        var beatmaps = new Dictionary<long, FarmBeatmap>();
        foreach (var value in payload.Scores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Unranked configurations do not award pp and cannot enter the
            // user's performance-ranked best-score list.
            if (value.Id <= 0 || value.Pp is null || value.Pp <= 0 ||
                value.Beatmap is null || value.Beatmap.Id <= 0)
                continue;

            var actualMods = ParseEligibleMods(value.Mods, out var eligible);
            if (!eligible)
                continue;
            var isLegacy = value.LegacyScoreId is not null || IsLegacyScore(value.Type);
            if (isLegacy &&
                !actualMods.Any(mod =>
                    mod.NormalizedAcronym.Equals("CL", StringComparison.OrdinalIgnoreCase)))
            {
                var classic = rankedMods.Evaluate(new FarmMod("CL"));
                if (!classic.IsEligible)
                    continue;
                actualMods =
                [
                    .. actualMods,
                    new FarmMod(classic.Acronym, classic.CanonicalSettingsJson),
                ];
            }

            var beatmapSet = value.Beatmapset ?? value.Beatmap.Beatmapset;
            var beatmap = new FarmBeatmap(
                value.Beatmap.Id,
                value.Beatmap.BeatmapsetId,
                beatmapSet?.Artist ?? string.Empty,
                beatmapSet?.Title ?? string.Empty,
                value.Beatmap.Version ?? string.Empty,
                beatmapSet?.Creator ?? string.Empty,
                value.Beatmap.Bpm ?? 0,
                value.Beatmap.HitLength,
                value.Beatmap.TotalLength,
                value.Beatmap.DifficultyRating,
                value.Beatmap.Status ?? beatmapSet?.Status ?? "Unknown",
                beatmapSet?.RankedDate,
                beatmapSet?.Covers?.Card ??
                beatmapSet?.Covers?.Cover ??
                string.Empty)
            {
                CircleSize = value.Beatmap.CircleSize,
                ApproachRate = value.Beatmap.ApproachRate,
                OverallDifficulty = value.Beatmap.OverallDifficulty,
                DrainRate = value.Beatmap.DrainRate,
            };
            beatmaps[beatmap.BeatmapId] = beatmap;

            var canonicalParts = actualMods
                .Select(mod => mod.SettingsJson == "{}"
                    ? mod.NormalizedAcronym
                    : $"{mod.NormalizedAcronym}:{mod.SettingsJson}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            scores.Add(new FarmScore(
                value.Id,
                player.UserId,
                beatmap.BeatmapId,
                value.Pp.Value,
                value.Accuracy,
                value.Statistics?.CountMiss ?? 0,
                value.MaxCombo,
                value.IsPerfectCombo ??
                value.Perfect ??
                value.LegacyPerfect ??
                false,
                value.EndedAt ??
                value.CreatedAt ??
                DateTimeOffset.MinValue,
                actualMods,
                 canonicalParts.Length == 0
                     ? "NM"
                     : string.Join("+", canonicalParts),
                 clockRates.Calculate(actualMods),
                 isLegacy
                     ? FarmScoreOrigin.Legacy
                     : value.BuildId is not null || !string.IsNullOrWhiteSpace(value.Type)
                         ? FarmScoreOrigin.Lazer
                         : FarmScoreOrigin.Unknown,
                 value.LegacyScoreId ?? (isLegacy ? value.Id : null),
                 value.TotalScore,
                 value.LegacyTotalScore,
                 value.BuildId,
                 value.Type));
        }

        return new PlayerScoresPayload(
            player with { ScoresUpdatedAt = DateTimeOffset.UtcNow },
            scores,
            beatmaps.Values.ToArray());
    }

    private IReadOnlyList<FarmMod> ParseEligibleMods(
        JsonElement source,
        out bool eligible)
    {
        eligible = true;
        if (source.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];
        if (source.ValueKind != JsonValueKind.Array)
        {
            eligible = false;
            return [];
        }

        var results = new List<FarmMod>();
        foreach (var value in source.EnumerateArray())
        {
            string acronym;
            string settingsJson;
            if (value.ValueKind == JsonValueKind.String)
            {
                acronym = value.GetString() ?? string.Empty;
                settingsJson = "{}";
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                acronym = value.TryGetProperty("acronym", out var acronymValue)
                    ? acronymValue.GetString() ?? string.Empty
                    : string.Empty;
                settingsJson =
                    value.TryGetProperty("settings", out var settings) &&
                    settings.ValueKind is not (
                        JsonValueKind.Undefined or JsonValueKind.Null)
                        ? FarmFinderValidation.CanonicalJson(
                            settings.GetRawText())
                        : "{}";
            }
            else
            {
                eligible = false;
                return [];
            }

            var evaluation = rankedMods.Evaluate(
                new FarmMod(acronym, settingsJson));
            if (!evaluation.IsEligible)
            {
                eligible = false;
                return [];
            }
            results.Add(new FarmMod(
                evaluation.Acronym,
                evaluation.CanonicalSettingsJson));
        }
        return results;
    }

    private static bool IsLegacyScore(string? type) =>
        type?.StartsWith("score_best_", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<HttpResponseMessage> SendAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= MaximumRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForSharedRateLimitAsync(cancellationToken);
                await WaitForPacingAsync(cancellationToken);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{ApiBaseUrl}{userId}/scores?type=best&mode=osu&limit=100");
                var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                    return response;
                if (!IsTransient(response.StatusCode))
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException(
                        $"Hinamizawa player request failed with HTTP {status}.");
                }
                if (attempt == MaximumRetryCount)
                {
                    response.Dispose();
                    break;
                }

                var delay = RetryDelay(response, attempt);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    PublishRateLimit(delay);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            throw new OsuApiRateLimitException(
                "Hinamizawa remained unavailable after five retry attempts.");
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task WaitForPacingAsync(
        CancellationToken cancellationToken)
    {
        if (requestInterval <= TimeSpan.Zero)
            return;
        await pacingGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var delay = nextRequestAt - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow + requestInterval;
        }
        finally
        {
            pacingGate.Release();
        }
    }

    private async Task WaitForSharedRateLimitAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset until;
            lock (rateLimitGate)
                until = sharedRateLimitUntil;
            var remaining = until - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private void PublishRateLimit(TimeSpan delay)
    {
        var until = DateTimeOffset.UtcNow + delay;
        lock (rateLimitGate)
        {
            if (until > sharedRateLimitUntil)
                sharedRateLimitUntil = until;
            until = sharedRateLimitUntil;
        }
        RateLimited?.Invoke(until);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(
        HttpResponseMessage response,
        int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return LimitDelay(delta);
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var dated = date - DateTimeOffset.UtcNow;
            if (dated > TimeSpan.Zero)
                return LimitDelay(dated);
        }
        var milliseconds = Math.Min(30_000, 500 * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(
            milliseconds + Random.Shared.Next(0, 250));
    }

    private static TimeSpan LimitDelay(TimeSpan delay) =>
        delay > TimeSpan.FromMinutes(10)
            ? TimeSpan.FromMinutes(10)
            : delay;

    public void Dispose()
    {
        requestGate.Dispose();
        pacingGate.Dispose();
        if (ownsHttpClient)
            http.Dispose();
    }

    private sealed record ScoresEnvelope(
        [property: JsonPropertyName("scores")] ScoreResponse[]? Scores);

    private sealed record ScoreResponse
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("build_id")] public int? BuildId { get; init; }
        [JsonPropertyName("total_score")] public long? TotalScore { get; init; }
        [JsonPropertyName("legacy_total_score")] public long? LegacyTotalScore { get; init; }
        [JsonPropertyName("legacy_score_id")] public long? LegacyScoreId { get; init; }
        [JsonPropertyName("pp")] public double? Pp { get; init; }
        [JsonPropertyName("accuracy")] public double Accuracy { get; init; }
        [JsonPropertyName("max_combo")] public int MaxCombo { get; init; }
        [JsonPropertyName("ended_at")] public DateTimeOffset? EndedAt { get; init; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
        [JsonPropertyName("is_perfect_combo")] public bool? IsPerfectCombo { get; init; }
        [JsonPropertyName("perfect")] public bool? Perfect { get; init; }
        [JsonPropertyName("legacy_perfect")] public bool? LegacyPerfect { get; init; }
        [JsonPropertyName("statistics")] public ScoreStatistics? Statistics { get; init; }
        [JsonPropertyName("mods")] public JsonElement Mods { get; init; }
        [JsonPropertyName("beatmap")] public BeatmapResponse? Beatmap { get; init; }
        [JsonPropertyName("beatmapset")] public BeatmapsetResponse? Beatmapset { get; init; }
    }

    private sealed record ScoreStatistics(
        [property: JsonPropertyName("miss")] int? Miss,
        [property: JsonPropertyName("count_miss")] int? LegacyMiss)
    {
        public int CountMiss => Miss ?? LegacyMiss ?? 0;
    }

    private sealed record BeatmapResponse
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("beatmapset_id")] public long BeatmapsetId { get; init; }
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("bpm")] public double? Bpm { get; init; }
        [JsonPropertyName("hit_length")] public int HitLength { get; init; }
        [JsonPropertyName("total_length")] public int TotalLength { get; init; }
        [JsonPropertyName("difficulty_rating")] public double DifficultyRating { get; init; }
        [JsonPropertyName("cs")] public double? CircleSize { get; init; }
        [JsonPropertyName("ar")] public double? ApproachRate { get; init; }
        [JsonPropertyName("accuracy")] public double? OverallDifficulty { get; init; }
        [JsonPropertyName("drain")] public double? DrainRate { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("beatmapset")] public BeatmapsetResponse? Beatmapset { get; init; }
    }

    private sealed record BeatmapsetResponse
    {
        [JsonPropertyName("artist")] public string? Artist { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("creator")] public string? Creator { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("ranked_date")] public DateTimeOffset? RankedDate { get; init; }
        [JsonPropertyName("covers")] public CoversResponse? Covers { get; init; }
    }

    private sealed record CoversResponse(
        [property: JsonPropertyName("card")] string? Card,
        [property: JsonPropertyName("cover")] string? Cover);
}
