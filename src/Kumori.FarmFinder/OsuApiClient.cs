using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kumori.FarmFinder;

public sealed class OsuApiClient :
    IPlayerCohortProvider,
    IPlayerTopScoresProvider,
    IPlayerTopScoresProviderMetadata,
    IOsuBeatmapScoreProvider,
    IOsuRateLimitSource,
    IDisposable
{
    public const string ApiBaseUrl = "https://osu.ppy.sh/api/v2/";
    public const string TokenUrl = "https://osu.ppy.sh/oauth/token";
    private const int MaximumRetryCount = 5;
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly IOsuCredentialsStore credentialsStore;
    private readonly IRankedModCatalog rankedMods;
    private readonly IClockRateCalculator clockRates;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private readonly SemaphoreSlim requestGate = new(2, 2);
    private readonly SemaphoreSlim pacingGate = new(1, 1);
    private readonly object rateLimitGate = new();
    private readonly bool ownsHttpClient;
    private readonly TimeSpan requestInterval;
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;
    private DateTimeOffset nextRequestAt;
    private DateTimeOffset sharedRateLimitUntil;

    public OsuApiClient(
        IOsuCredentialsStore credentialsStore,
        IRankedModCatalog rankedMods,
        IClockRateCalculator clockRates,
        HttpClient? httpClient = null,
        TimeSpan? minimumRequestInterval = null)
    {
        this.credentialsStore = credentialsStore;
        this.rankedMods = rankedMods;
        this.clockRates = clockRates;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        ownsHttpClient = httpClient is null;
        requestInterval = minimumRequestInterval ?? TimeSpan.FromSeconds(1);
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori-FarmFinder/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public event Action<DateTimeOffset>? RateLimited;

    public string SourceName => "osu! API";
    public int RecommendedConcurrency => 2;
    public int RequestsPerMinute => 60;

    public async Task<OsuBeatmapUserScore?> GetBeatmapUserScoreAsync(
        long beatmapId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId));
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));

        using var response = await SendApiAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"beatmaps/{beatmapId}/scores/users/{userId}?mode=osu&legacy_only=0"),
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        BeatmapUserScoreResponse? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<BeatmapUserScoreResponse>(
                stream, jsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "osu! returned a malformed beatmap-score response.",
                exception);
        }

        if (payload?.Score is not { } score || payload.Position <= 0 || score.Id <= 0)
            return null;
        var endedAt = score.EndedAt ?? score.CreatedAt;
        if (endedAt is null)
            return null;
        var statistics = score.Statistics;
        return new OsuBeatmapUserScore(
            payload.Position,
            score.Id,
            userId,
            beatmapId,
            endedAt.Value,
            score.LegacyScoreId is not null
                ? score.LegacyTotalScore ?? score.TotalScore ?? 0
                : score.TotalScore ?? score.LegacyTotalScore ?? 0,
            score.Accuracy,
            score.Pp ?? 0,
            score.MaxCombo,
            statistics?.Count300 ?? 0,
            statistics?.Count100 ?? 0,
            statistics?.Count50 ?? 0,
            statistics?.CountMiss ?? 0,
            (score.Mods ?? [])
                .Select(mod => mod.Acronym?.Trim().ToUpperInvariant())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray());
    }

    public async Task<IReadOnlyList<string>> GetCountryCodesAsync(
        CancellationToken cancellationToken = default)
    {
        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = BuildCountryRankingPath(cursor);
            using var response = await SendApiAsync(
                () => new HttpRequestMessage(HttpMethod.Get, path),
                cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            CountryRankingResponse payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<CountryRankingResponse>(
                              stream, jsonOptions, cancellationToken)
                          ?? throw new InvalidDataException(
                              "osu! returned an empty country-ranking response.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "osu! returned a malformed country-ranking response.",
                    exception);
            }

            foreach (var entry in payload.Ranking ?? [])
            {
                var code = entry.Code?.Trim().ToUpperInvariant();
                if (code is { Length: 2 })
                    countries.Add(code);
            }
            var next = CursorToJson(payload.Cursor, payload.CursorString);
            if (string.IsNullOrWhiteSpace(next) ||
                string.Equals(next, cursor, StringComparison.Ordinal))
                break;
            cursor = next;
        }
        return countries.OrderBy(code => code, StringComparer.Ordinal).ToArray();
    }

    public async IAsyncEnumerable<RankingPage> GetRankingPagesAsync(
        string? cursorJson,
        int startingRank = 1,
        string? countryCode = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (startingRank < 1)
            throw new ArgumentOutOfRangeException(nameof(startingRank));
        var cursor = string.IsNullOrWhiteSpace(cursorJson) && startingRank > 1
            ? JsonSerializer.Serialize(new
            {
                page = ((startingRank - 1) / OsuApiLimits.PerformanceRankingPageSize) + 1,
            })
            : cursorJson;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = BuildRankingPath(cursor, countryCode);
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            RankingResponse payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<RankingResponse>(
                              stream, jsonOptions, cancellationToken)
                          ?? throw new InvalidDataException("osu! returned an empty ranking response.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("osu! returned a malformed ranking response.", exception);
            }

            var now = DateTimeOffset.UtcNow;
            var players = payload.Ranking
                .Where(item => item.User is not null && item.GlobalRank is > 0)
                .Select(item => new FarmPlayer(
                    item.User!.Id,
                    item.User.Username ?? $"User {item.User.Id}",
                    item.GlobalRank!.Value,
                    item.Pp ?? 0,
                    now))
                .ToArray();
            var next = CursorToJson(payload.Cursor, payload.CursorString);
            yield return new RankingPage(players, next, payload.Total);
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next, cursor, StringComparison.Ordinal))
                yield break;
            cursor = next;
        }
    }

    public async Task<PlayerScoresPayload> GetTopScoresAsync(
        FarmPlayer player,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendApiAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"users/{player.UserId}/scores/best?mode=osu&limit=100&legacy_only=0"),
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        ScoreResponse[] payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ScoreResponse[]>(
                          stream, jsonOptions, cancellationToken)
                      ?? throw new InvalidDataException("osu! returned an empty top-score response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("osu! returned a malformed top-score response.", exception);
        }
        var scores = new List<FarmScore>(payload.Length);
        var beatmaps = new Dictionary<long, FarmBeatmap>();

        foreach (var value in payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Id <= 0 || value.Pp is null || value.Beatmap is null)
                continue;
            var actualMods = new List<FarmMod>(value.Mods?.Length ?? 0);
            var canonicalParts = new List<string>(value.Mods?.Length ?? 0);
            var eligible = true;
            foreach (var apiMod in value.Mods ?? [])
            {
                var settingsJson = apiMod.Settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "{}"
                    : FarmFinderValidation.CanonicalJson(apiMod.Settings.GetRawText());
                var mod = new FarmMod(apiMod.Acronym ?? string.Empty, settingsJson);
                var evaluation = rankedMods.Evaluate(mod);
                if (!evaluation.IsEligible)
                {
                    eligible = false;
                    break;
                }
                actualMods.Add(new FarmMod(evaluation.Acronym, evaluation.CanonicalSettingsJson));
                canonicalParts.Add(evaluation.CanonicalSettingsJson == "{}"
                    ? evaluation.Acronym
                    : $"{evaluation.Acronym}:{evaluation.CanonicalSettingsJson}");
            }
            if (!eligible)
                continue;
            var isLegacy = value.LegacyScoreId is not null;
            if (isLegacy && !actualMods.Any(mod =>
                    mod.NormalizedAcronym.Equals("CL", StringComparison.OrdinalIgnoreCase)))
            {
                var classic = rankedMods.Evaluate(new FarmMod("CL"));
                if (!classic.IsEligible)
                    continue;
                actualMods.Add(new FarmMod(
                    classic.Acronym,
                    classic.CanonicalSettingsJson));
                canonicalParts.Add(classic.CanonicalSettingsJson == "{}"
                    ? classic.Acronym
                    : $"{classic.Acronym}:{classic.CanonicalSettingsJson}");
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
                beatmapSet?.Covers?.Card ?? beatmapSet?.Covers?.Cover ?? string.Empty)
            {
                CircleSize = value.Beatmap.CircleSize,
                ApproachRate = value.Beatmap.ApproachRate,
                OverallDifficulty = value.Beatmap.OverallDifficulty,
                DrainRate = value.Beatmap.DrainRate,
            };
            beatmaps[beatmap.BeatmapId] = beatmap;

            var endedAt = value.EndedAt ?? value.CreatedAt ?? DateTimeOffset.MinValue;
            scores.Add(new FarmScore(
                value.Id,
                player.UserId,
                beatmap.BeatmapId,
                value.Pp.Value,
                value.Accuracy,
                value.Statistics?.CountMiss ?? 0,
                value.MaxCombo,
                value.IsPerfectCombo ?? value.LegacyPerfect ?? false,
                endedAt,
                 actualMods,
                 canonicalParts.Count == 0 ? "NM" : string.Join("+", canonicalParts.Order(StringComparer.Ordinal)),
                 clockRates.Calculate(actualMods),
                 isLegacy ? FarmScoreOrigin.Legacy : FarmScoreOrigin.Lazer,
                 value.LegacyScoreId,
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

    public async Task<double> GetDifficultyStarRatingAsync(
        long beatmapId,
        IReadOnlyList<FarmMod> mods,
        CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId));

        var apiMods = new JsonArray();
        foreach (var mod in mods)
        {
            JsonNode settings;
            try
            {
                settings = JsonNode.Parse(
                               string.IsNullOrWhiteSpace(mod.SettingsJson)
                                   ? "{}"
                                   : mod.SettingsJson)
                           ?? new JsonObject();
            }
            catch (JsonException)
            {
                settings = new JsonObject();
            }
            apiMods.Add(new JsonObject
            {
                ["acronym"] = mod.NormalizedAcronym,
                ["settings"] = settings,
            });
        }
        var body = new JsonObject
        {
            ["mods"] = apiMods,
            ["ruleset"] = "osu",
        }.ToJsonString();
        using var response = await SendApiAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                $"beatmaps/{beatmapId}/attributes")
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/json"),
            },
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        DifficultyAttributesEnvelope payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<DifficultyAttributesEnvelope>(
                          stream,
                          jsonOptions,
                          cancellationToken)
                      ?? throw new InvalidDataException(
                          "osu! returned an empty difficulty-attributes response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "osu! returned a malformed difficulty-attributes response.",
                exception);
        }
        if (payload.Attributes.StarRating <= 0
            || !double.IsFinite(payload.Attributes.StarRating))
            throw new InvalidDataException(
                "osu! returned an invalid difficulty star rating.");
        return payload.Attributes.StarRating;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendApiAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "rankings/osu/performance"),
            cancellationToken);
    }

    public async Task<OsuUserProfileStats> GetUserProfileStatsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));

        using var response = await SendApiAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"users/{userId}/osu?key=id"),
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        UserProfileResponse payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<UserProfileResponse>(
                          stream, jsonOptions, cancellationToken)
                      ?? throw new InvalidDataException("osu! returned an empty user-profile response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("osu! returned a malformed user-profile response.", exception);
        }

        var countryCode = payload.CountryCode?.Trim().ToUpperInvariant();
        var coverUrl = payload.Cover?.Url ?? payload.CoverUrl;
        return new OsuUserProfileStats(
            payload.Statistics?.CountryRank is > 0 ? payload.Statistics.CountryRank : null,
            countryCode is { Length: 2 } ? countryCode : null,
            Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri)
            && coverUri.Scheme == Uri.UriSchemeHttps
                ? coverUri.AbsoluteUri
                : null);
    }

    public void InvalidateToken()
    {
        accessToken = null;
        accessTokenExpiresAt = default;
    }

    private async Task<HttpResponseMessage> SendApiAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= MaximumRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = await GetAccessTokenAsync(cancellationToken);
                await WaitForSharedRateLimitAsync(cancellationToken);
                await WaitForPacingAsync(cancellationToken);
                using var request = requestFactory();
                if (request.RequestUri is not { } requestUri)
                    throw new InvalidOperationException("The osu! API request did not contain a URI.");
                if (!requestUri.IsAbsoluteUri)
                    request.RequestUri = new Uri(new Uri(ApiBaseUrl), requestUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("x-api-version", "20220705");
                var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                    return response;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    InvalidateToken();
                    if (attempt == 0)
                        continue;
                    throw new OsuApiAuthenticationException(
                        "osu! rejected the configured API credentials. Re-enter the Client ID and secret.");
                }
                if (!IsTransient(response.StatusCode))
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"osu! API request failed with HTTP {status}.");
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
            throw new OsuApiRateLimitException("osu! API remained unavailable after five retry attempts.");
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken)
            && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return accessToken;

        await tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken)
                && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return accessToken;

            var configuredCredentials = await credentialsStore.LoadAsync(cancellationToken);
            if (configuredCredentials?.IsConfigured != true)
                throw new OsuApiAuthenticationException(
                    "Configure an osu! API Client ID and secret before updating the Farm Finder index.");
            for (var attempt = 0; attempt <= MaximumRetryCount; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = configuredCredentials.ClientId.ToString(CultureInfo.InvariantCulture),
                        ["client_secret"] = configuredCredentials.ClientSecret,
                        ["grant_type"] = "client_credentials",
                        ["scope"] = "public",
                    }),
                };
                await WaitForSharedRateLimitAsync(cancellationToken);
                await WaitForPacingAsync(cancellationToken);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    TokenResponse? token;
                    try
                    {
                        token = await JsonSerializer.DeserializeAsync<TokenResponse>(
                            stream, jsonOptions, cancellationToken);
                    }
                    catch (JsonException exception)
                    {
                        throw new InvalidDataException("osu! returned a malformed OAuth token response.", exception);
                    }
                    if (string.IsNullOrWhiteSpace(token?.AccessToken))
                        throw new InvalidDataException("osu! returned an invalid OAuth token response.");
                    accessToken = token.AccessToken;
                    accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));
                    return accessToken;
                }
                if (!IsTransient(response.StatusCode))
                    throw new OsuApiAuthenticationException(
                        "osu! rejected the configured API Client ID or secret.");
                if (attempt == MaximumRetryCount)
                    break;
                var delay = RetryDelay(response, attempt);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    PublishRateLimit(delay);
                await Task.Delay(delay, cancellationToken);
            }
            throw new OsuApiRateLimitException(
                "osu! OAuth remained unavailable after five retry attempts.");
        }
        finally
        {
            tokenGate.Release();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var value = date - DateTimeOffset.UtcNow;
            if (value > TimeSpan.Zero)
                return value > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : value;
        }
        var milliseconds = Math.Min(30_000, 500 * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(milliseconds + Random.Shared.Next(0, 250));
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

    private async Task WaitForSharedRateLimitAsync(CancellationToken cancellationToken)
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

    private async Task WaitForPacingAsync(CancellationToken cancellationToken)
    {
        if (requestInterval <= TimeSpan.Zero)
            return;
        await pacingGate.WaitAsync(cancellationToken);
        try
        {
            var remaining = nextRequestAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow + requestInterval;
        }
        finally
        {
            pacingGate.Release();
        }
    }

    private static string BuildRankingPath(string? cursorJson, string? countryCode)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(countryCode))
            query.Add("country=" + Uri.EscapeDataString(countryCode.Trim().ToUpperInvariant()));
        if (string.IsNullOrWhiteSpace(cursorJson))
            return query.Count == 0
                ? "rankings/osu/performance"
                : "rankings/osu/performance?" + string.Join("&", query);
        try
        {
            using var document = JsonDocument.Parse(cursorJson);
            if (document.RootElement.ValueKind == JsonValueKind.String)
                query.Add(
                    "cursor_string=" +
                    Uri.EscapeDataString(document.RootElement.GetString() ?? string.Empty));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return query.Count == 0
                    ? "rankings/osu/performance"
                    : "rankings/osu/performance?" + string.Join("&", query);
            query.AddRange(document.RootElement.EnumerateObject()
                .Select(property =>
                    $"cursor[{Uri.EscapeDataString(property.Name)}]={Uri.EscapeDataString(CursorValue(property.Value))}"));
        }
        catch (JsonException)
        {
            // Ignore a corrupt cursor and restart the selected ranking feed.
        }
        return query.Count == 0
            ? "rankings/osu/performance"
            : "rankings/osu/performance?" + string.Join("&", query);
    }

    private static string BuildCountryRankingPath(string? cursorJson)
    {
        if (string.IsNullOrWhiteSpace(cursorJson))
            return "rankings/osu/country";
        try
        {
            using var document = JsonDocument.Parse(cursorJson);
            if (document.RootElement.ValueKind == JsonValueKind.String)
                return "rankings/osu/country?cursor_string="
                       + Uri.EscapeDataString(document.RootElement.GetString() ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "rankings/osu/country";
            var query = document.RootElement.EnumerateObject()
                .Select(property =>
                    $"cursor[{Uri.EscapeDataString(property.Name)}]={Uri.EscapeDataString(CursorValue(property.Value))}");
            return "rankings/osu/country?" + string.Join("&", query);
        }
        catch (JsonException)
        {
            return "rankings/osu/country";
        }
    }

    private static string CursorValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static string? CursorToJson(JsonElement cursor, string? cursorString)
    {
        if (!string.IsNullOrWhiteSpace(cursorString))
            return JsonSerializer.Serialize(cursorString);
        return cursor.ValueKind is JsonValueKind.Object && cursor.EnumerateObject().Any()
            ? cursor.GetRawText()
            : null;
    }

    public void Dispose()
    {
        tokenGate.Dispose();
        requestGate.Dispose();
        pacingGate.Dispose();
        if (ownsHttpClient)
            http.Dispose();
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record RankingResponse(
        [property: JsonPropertyName("ranking")] RankingEntry[] Ranking,
        [property: JsonPropertyName("cursor")] JsonElement Cursor,
        [property: JsonPropertyName("cursor_string")] string? CursorString,
        [property: JsonPropertyName("total")] int? Total);

    private sealed record CountryRankingResponse(
        [property: JsonPropertyName("ranking")] CountryRankingEntry[]? Ranking,
        [property: JsonPropertyName("cursor")] JsonElement Cursor,
        [property: JsonPropertyName("cursor_string")] string? CursorString);

    private sealed record CountryRankingEntry(
        [property: JsonPropertyName("code")] string? Code);

    private sealed record RankingEntry(
        [property: JsonPropertyName("global_rank")] int? GlobalRank,
        [property: JsonPropertyName("pp")] double? Pp,
        [property: JsonPropertyName("user")] RankingUser? User);

    private sealed record RankingUser(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("username")] string? Username);

    private sealed record UserProfileResponse(
        [property: JsonPropertyName("country_code")] string? CountryCode,
        [property: JsonPropertyName("cover")] UserCoverResponse? Cover,
        [property: JsonPropertyName("cover_url")] string? CoverUrl,
        [property: JsonPropertyName("statistics")] UserStatisticsResponse? Statistics);

    private sealed record UserCoverResponse(
        [property: JsonPropertyName("url")] string? Url);

    private sealed record UserStatisticsResponse(
        [property: JsonPropertyName("country_rank")] long? CountryRank);

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
        [JsonPropertyName("legacy_perfect")] public bool? LegacyPerfect { get; init; }
        [JsonPropertyName("statistics")] public ScoreStatistics? Statistics { get; init; }
        [JsonPropertyName("mods")] public ApiMod[]? Mods { get; init; }
        [JsonPropertyName("beatmap")] public BeatmapResponse? Beatmap { get; init; }
        [JsonPropertyName("beatmapset")] public BeatmapsetResponse? Beatmapset { get; init; }
    }

    private sealed record ScoreStatistics(
        [property: JsonPropertyName("miss")] int? Miss,
        [property: JsonPropertyName("count_miss")] int? LegacyMiss,
        [property: JsonPropertyName("great")] int? Great,
        [property: JsonPropertyName("count_300")] int? LegacyGreat,
        [property: JsonPropertyName("ok")] int? Ok,
        [property: JsonPropertyName("count_100")] int? LegacyOk,
        [property: JsonPropertyName("meh")] int? Meh,
        [property: JsonPropertyName("count_50")] int? LegacyMeh)
    {
        public int CountMiss => Miss ?? LegacyMiss ?? 0;
        public int Count300 => Great ?? LegacyGreat ?? 0;
        public int Count100 => Ok ?? LegacyOk ?? 0;
        public int Count50 => Meh ?? LegacyMeh ?? 0;
    }

    private sealed record BeatmapUserScoreResponse
    {
        [JsonPropertyName("position")] public int Position { get; init; }
        [JsonPropertyName("score")] public ScoreResponse? Score { get; init; }
    }

    private sealed record ApiMod(
        [property: JsonPropertyName("acronym")] string? Acronym,
        [property: JsonPropertyName("settings")] JsonElement Settings);

    private sealed record DifficultyAttributesEnvelope(
        [property: JsonPropertyName("attributes")]
        DifficultyAttributesResponse Attributes);

    private sealed record DifficultyAttributesResponse(
        [property: JsonPropertyName("star_rating")] double StarRating);

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
