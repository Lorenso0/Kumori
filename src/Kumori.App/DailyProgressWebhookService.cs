using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kumori.App.FarmFinder;
using Kumori.App.ViewModels;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.FarmFinder;
using Kumori.Storage;
using Serilog;
using static System.FormattableString;

namespace Kumori.App;

public sealed class DailyProgressWebhookService
{
    internal const string DailyCardAttachmentName = "kumori-daily-recap.png";
    private const long DiscordAttachmentLimit = 10L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private static readonly HttpClient ProfileImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private readonly SettingsService settings;
    private readonly Func<string, DailyProgressReport?> loadReport;
    private readonly HttpClient httpClient;
    private readonly Func<long, CancellationToken, Task<byte[]?>> loadAvatar;
    private readonly Func<string, CancellationToken, Task<byte[]?>> loadCountryFlag;
    private readonly Func<long, CancellationToken, Task<byte[]?>> loadBanner;
    private readonly Func<long, long, string, CancellationToken, Task<byte[]?>> loadBeatmapArtwork;
    private readonly string stateFile;
    private DateOnly? lastProcessedInMemory;

    public DailyProgressWebhookService(
        SettingsService settings,
        AnalyticsRepository analytics,
        HttpClient? httpClient = null,
        string? stateFile = null)
        : this(
            settings,
            analytics.GetDailyProgress,
            httpClient,
            stateFile,
            DownloadAvatarAsync,
            DownloadCountryFlagAsync,
            DownloadBannerAsync,
            LoadBeatmapArtworkAsync)
    {
    }

    internal DailyProgressWebhookService(
        SettingsService settings,
        Func<string, DailyProgressReport?> loadReport,
        HttpClient? httpClient = null,
        string? stateFile = null,
        Func<long, CancellationToken, Task<byte[]?>>? loadAvatar = null,
        Func<string, CancellationToken, Task<byte[]?>>? loadCountryFlag = null,
        Func<long, CancellationToken, Task<byte[]?>>? loadBanner = null,
        Func<long, long, string, CancellationToken, Task<byte[]?>>? loadBeatmapArtwork = null)
    {
        this.settings = settings;
        this.loadReport = loadReport;
        this.httpClient = httpClient ?? SharedHttpClient;
        this.loadAvatar = loadAvatar ?? ((_, _) => Task.FromResult<byte[]?>(null));
        this.loadCountryFlag = loadCountryFlag ?? ((_, _) => Task.FromResult<byte[]?>(null));
        this.loadBanner = loadBanner ?? ((_, _) => Task.FromResult<byte[]?>(null));
        this.loadBeatmapArtwork = loadBeatmapArtwork ?? ((_, _, _, _) => Task.FromResult<byte[]?>(null));
        this.stateFile = stateFile ?? AppPaths.DailyWebhookStateFile;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var outcome = await TrySendPreviousDayAsync(DateTime.Today, cancellationToken);
                var delay = outcome == DailyWebhookOutcome.Failed
                    ? TimeSpan.FromHours(1)
                    : DelayUntilNextCheck(DateTime.Now);
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<DailyWebhookOutcome> TrySendPreviousDayAsync(
        DateTime localToday,
        CancellationToken cancellationToken)
    {
        var configured = settings.Current.DailyWebhook;
        if (!configured.Enabled || string.IsNullOrWhiteSpace(configured.WebhookUrl))
            return DailyWebhookOutcome.Disabled;

        var target = DateOnly.FromDateTime(localToday.Date.AddDays(-1));
        if (ReadLastProcessedDay() is { } processed && processed >= target)
            return DailyWebhookOutcome.AlreadyProcessed;

        var day = target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DailyProgressReport? report;
        try
        {
            report = await Task.Run(() => loadReport(day), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Log.Warning("Daily webhook report data could not be read; Kumori will retry later");
            return DailyWebhookOutcome.Failed;
        }

        if (report is null)
        {
            MarkProcessed(target);
            return DailyWebhookOutcome.NoActivity;
        }

        try
        {
            await SendAsync(configured.WebhookUrl, report, isTest: false, cancellationToken);
            MarkProcessed(target);
            Log.Information("Daily progress webhook sent for {ReportDay}", day);
            return DailyWebhookOutcome.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A Discord webhook URL contains a secret token. Keep the exception
            // out of logs so an HTTP stack cannot accidentally disclose it.
            Log.Warning("Daily progress webhook failed; Kumori will retry later");
            return DailyWebhookOutcome.Failed;
        }
    }

    public async Task SendTestAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var report = await Task.Run(() => loadReport(today), cancellationToken)
                     ?? new DailyProgressReport
                     {
                         PlayerName = "Kumori user",
                         Summary = new DailyAttemptTrend { Day = today },
                     };
        await SendAsync(webhookUrl, report, isTest: true, cancellationToken);
    }

    private async Task SendAsync(
        string webhookUrl,
        DailyProgressReport report,
        bool isTest,
        CancellationToken cancellationToken)
    {
        if (!TryValidateWebhookUrl(webhookUrl, out var uri))
        {
            throw new InvalidOperationException(
                "Enter a valid HTTPS Discord webhook URL from discord.com/api/webhooks.");
        }

        string cardPath = Path.Combine(
            Path.GetTempPath(),
            $"kumori-daily-recap-{Guid.NewGuid():N}.png");
        string? avatarPath = null;
        string? countryFlagPath = null;
        string? bannerPath = null;
        string? bestArtworkPath = null;
        string? mostPlayedArtworkPath = null;
        try
        {
            var rendered = false;
            try
            {
                if (report.Account?.PlayerId is > 0 and var playerId)
                {
                    try
                    {
                        byte[]? avatar = await loadAvatar(playerId, cancellationToken);
                        if (avatar is { Length: > 0 and <= 5 * 1024 * 1024 })
                        {
                            avatarPath = Path.Combine(
                                Path.GetTempPath(),
                                $"kumori-daily-avatar-{Guid.NewGuid():N}.img");
                            await File.WriteAllBytesAsync(avatarPath, avatar, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException
                                                       or TaskCanceledException
                                                       or IOException
                                                       or InvalidDataException)
                    {
                        Log.Debug(exception, "Could not load osu! profile avatar for daily recap");
                    }
                    try
                    {
                        byte[]? banner = await loadBanner(playerId, cancellationToken);
                        if (banner is { Length: > 0 and <= 8 * 1024 * 1024 })
                        {
                            bannerPath = Path.Combine(
                                Path.GetTempPath(),
                                $"kumori-daily-banner-{Guid.NewGuid():N}.img");
                            await File.WriteAllBytesAsync(bannerPath, banner, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException
                                                       or TaskCanceledException
                                                       or IOException
                                                       or InvalidDataException
                                                       or InvalidOperationException
                                                       or JsonException
                                                       or System.Security.Cryptography.CryptographicException)
                    {
                        Log.Debug(exception, "Could not load osu! profile banner for daily recap");
                    }
                }
                if (report.Account?.CountryCode?.Trim().ToLowerInvariant() is { Length: 2 } countryCode)
                {
                    try
                    {
                        byte[]? flag = await loadCountryFlag(countryCode, cancellationToken);
                        if (flag is { Length: > 0 and <= 512 * 1024 })
                        {
                            countryFlagPath = Path.Combine(
                                Path.GetTempPath(),
                                $"kumori-daily-flag-{Guid.NewGuid():N}.img");
                            await File.WriteAllBytesAsync(countryFlagPath, flag, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException
                                                       or TaskCanceledException
                                                       or IOException
                                                       or InvalidDataException)
                    {
                        Log.Debug(exception, "Could not load country flag for daily recap");
                    }
                }
                if (report.BestPlay is { } bestPlay)
                {
                    bestArtworkPath = await TryWriteBeatmapArtworkAsync(
                        bestPlay.BeatmapId,
                        bestPlay.BeatmapSetId,
                        bestPlay.Difficulty,
                        "best",
                        cancellationToken);
                }
                if (report.MostPlayedMap is { } mostPlayedMap)
                {
                    mostPlayedArtworkPath = await TryWriteBeatmapArtworkAsync(
                        mostPlayedMap.BeatmapId,
                        mostPlayedMap.BeatmapSetId,
                        mostPlayedMap.Difficulty,
                        "most-played",
                        cancellationToken);
                }
                await DailyStatsCardRenderer.RenderAsync(
                    report,
                    isTest,
                    avatarPath,
                    countryFlagPath,
                    bannerPath,
                    bestArtworkPath,
                    mostPlayedArtworkPath,
                    cardPath,
                    cancellationToken);
                rendered = new FileInfo(cardPath).Length is > 0 and <= DiscordAttachmentLimit;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not render daily recap card; using native Discord embed");
            }

            if (rendered)
            {
                using HttpResponseMessage imageResponse = await PostImageAsync(
                    uri,
                    report,
                    cardPath,
                    cancellationToken);
                if (imageResponse.IsSuccessStatusCode)
                    return;
                if (imageResponse.StatusCode is not HttpStatusCode.BadRequest
                    and not HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new InvalidOperationException(
                        Invariant($"Discord returned HTTP {(int)imageResponse.StatusCode}. Check that the webhook still exists."));
                }
                Log.Warning("Discord rejected the daily recap image; using native Discord embed");
            }

            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                uri,
                BuildPayload(report, isTest),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    Invariant($"Discord returned HTTP {(int)response.StatusCode}. Check that the webhook still exists."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("Could not reach Discord. Check your connection and try again.", exception);
        }
        finally
        {
            try { File.Delete(cardPath); } catch { }
            if (avatarPath is not null)
            {
                try { File.Delete(avatarPath); } catch { }
            }
            if (countryFlagPath is not null)
            {
                try { File.Delete(countryFlagPath); } catch { }
            }
            if (bannerPath is not null)
            {
                try { File.Delete(bannerPath); } catch { }
            }
            if (bestArtworkPath is not null)
            {
                try { File.Delete(bestArtworkPath); } catch { }
            }
            if (mostPlayedArtworkPath is not null)
            {
                try { File.Delete(mostPlayedArtworkPath); } catch { }
            }
        }
    }

    private async Task<string?> TryWriteBeatmapArtworkAsync(
        long beatmapId,
        long beatmapSetId,
        string difficulty,
        string role,
        CancellationToken cancellationToken)
    {
        if (beatmapId <= 0 && beatmapSetId <= 0)
            return null;
        try
        {
            byte[]? artwork = await loadBeatmapArtwork(
                beatmapId,
                beatmapSetId,
                difficulty,
                cancellationToken);
            if (artwork is not { Length: > 0 and <= 8 * 1024 * 1024 })
                return null;

            string path = Path.Combine(
                Path.GetTempPath(),
                $"kumori-daily-{role}-art-{Guid.NewGuid():N}.img");
            await File.WriteAllBytesAsync(path, artwork, cancellationToken);
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or TaskCanceledException
                                           or IOException
                                           or InvalidDataException
                                           or NotSupportedException)
        {
            Log.Debug(exception, "Could not load {ArtworkRole} beatmap artwork for daily recap", role);
            return null;
        }
    }

    internal static async Task<byte[]?> DownloadAvatarAsync(
        long playerId,
        CancellationToken cancellationToken)
        => await DownloadImageAsync(
            new Uri($"https://a.ppy.sh/{playerId}"),
            5 * 1024 * 1024,
            cancellationToken);

    internal static async Task<byte[]?> DownloadCountryFlagAsync(
        string countryCode,
        CancellationToken cancellationToken)
    {
        if (countryCode.Length != 2
            || countryCode.Any(character => character is < 'a' or > 'z'))
            return null;
        return await DownloadImageAsync(
            new Uri($"https://osu.ppy.sh/images/flags/{countryCode.ToUpperInvariant()}.png"),
            512 * 1024,
            cancellationToken);
    }

    internal static async Task<byte[]?> LoadBeatmapArtworkAsync(
        long beatmapId,
        long beatmapSetId,
        string difficulty,
        CancellationToken cancellationToken)
    {
        string? artwork = BeatmapArtworkResolver.Resolve(
            beatmapId,
            beatmapSetId,
            difficulty,
            fallbackUrl: null);
        if (string.IsNullOrWhiteSpace(artwork))
            return null;
        if (Uri.TryCreate(artwork, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            return await DownloadImageAsync(uri, 8 * 1024 * 1024, cancellationToken);
        }
        if (!File.Exists(artwork) || new FileInfo(artwork).Length is <= 0 or > 8 * 1024 * 1024)
            return null;
        return await File.ReadAllBytesAsync(artwork, cancellationToken);
    }

    internal static async Task<byte[]?> DownloadBannerAsync(
        long playerId,
        CancellationToken cancellationToken)
    {
        string? coverUrl = null;
        try
        {
            var credentials = new WindowsCredentialsStore(AppPaths.FarmFinderCredentialsFile);
            if ((await credentials.LoadAsync(cancellationToken))?.IsConfigured == true)
            {
                using var api = new OsuApiClient(
                    credentials,
                    new OsuRankedModCatalog(),
                    new ClockRateCalculator());
                coverUrl = (await api.GetUserProfileStatsAsync(playerId, cancellationToken)).CoverUrl;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or TaskCanceledException
                                           or IOException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or JsonException
                                           or System.Security.Cryptography.CryptographicException)
        {
            Log.Debug(exception, "Could not resolve osu! profile banner through the API; trying the public profile page");
        }

        coverUrl ??= await ReadCoverUrlFromProfilePageAsync(playerId, cancellationToken);
        return Uri.TryCreate(coverUrl, UriKind.Absolute, out Uri? coverUri)
               && coverUri.Scheme == Uri.UriSchemeHttps
            ? await DownloadImageAsync(coverUri, 8 * 1024 * 1024, cancellationToken)
            : null;
    }

    private static async Task<string?> ReadCoverUrlFromProfilePageAsync(
        long playerId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ProfileImageHttpClient.GetAsync(
            $"https://osu.ppy.sh/users/{playerId}/osu",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
            return null;
        byte[]? content = await ReadBoundedAsync(response.Content, 4 * 1024 * 1024, cancellationToken);
        if (content is null)
            return null;

        string page = WebUtility.HtmlDecode(Encoding.UTF8.GetString(content));
        const string marker = "\"cover_url\":\"";
        int start = page.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        int end = page.IndexOf('"', start);
        return end > start
            ? page[start..end].Replace("\\/", "/", StringComparison.Ordinal)
            : null;
    }

    private static async Task<byte[]?> DownloadImageAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;
        using HttpResponseMessage response = await ProfileImageHttpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is { } length && length > maximumBytes
            || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == false)
            return null;
        return await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken);
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 128 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> PostImageAsync(
        Uri uri,
        DailyProgressReport report,
        string cardPath,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(
            new StringContent(
                JsonSerializer.Serialize(BuildImagePayload(report), JsonOptions),
                Encoding.UTF8,
                "application/json"),
            "payload_json");
        await using var stream = new FileStream(
            cardPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "files[0]", DailyCardAttachmentName);
        return await httpClient.PostAsync(uri, content, cancellationToken);
    }

    internal static object BuildImagePayload(DailyProgressReport report) => new
    {
        username = "Kumori",
        allowed_mentions = new { parse = Array.Empty<string>() },
        attachments = new[]
        {
            new
            {
                id = 0,
                filename = DailyCardAttachmentName,
                description = $"Kumori daily osu! recap for {report.PlayerName}",
            },
        },
    };

    internal static object BuildPayload(DailyProgressReport report, bool isTest)
    {
        var summary = report.Summary;
        var account = report.Account;
        var playerName = account?.PlayerName;
        if (string.IsNullOrWhiteSpace(playerName)) playerName = report.PlayerName;
        if (string.IsNullOrWhiteSpace(playerName)) playerName = "Kumori user";
        var fields = new List<object>();

        if (report.BestPlay is { } best)
        {
            fields.Add(Field(
                "Highest achieved PP play",
                $"**{MapName(best.Artist, best.Title, best.Difficulty)}**\n" +
                Invariant($"{best.Pp:0.0}pp · {best.Accuracy:0.00}% · {DisplayMods(best.ModsKey)} · {best.Misses:N0} miss{(best.Misses == 1 ? "" : "es")}")));
        }

        if (account?.OldPlayCount is { } oldPlays && account.NewPlayCount is { } newPlays)
            fields.Add(Field(
                "Playcount",
                Invariant($"{Signed(newPlays - oldPlays)} official · {summary.Attempts:N0} local · {summary.DistinctMaps:N0} distinct maps")));
        else
            fields.Add(Field(
                "Playcount",
                Invariant($"{summary.Attempts:N0} local · {summary.DistinctMaps:N0} distinct maps")));

        if (account?.OldGlobalRank is { } oldRank && account.NewGlobalRank is { } newRank)
            fields.Add(Field("Rank", Invariant($"#{oldRank:N0} → #{newRank:N0} ({Signed(oldRank - newRank)})")));
        else if (summary.RankChange is { } rankChange)
            fields.Add(Field("Rank", Signed(rankChange)));

        if (account?.OldCountryRank is { } oldCountryRank
            && account.NewCountryRank is { } newCountryRank)
        {
            var flag = CountryFlag(account.CountryCode);
            fields.Add(Field(
                string.IsNullOrEmpty(flag) ? "Country rank" : $"{flag} Country rank",
                Invariant($"#{oldCountryRank:N0} → #{newCountryRank:N0} ({Signed(oldCountryRank - newCountryRank)})")));
        }

        if (account?.OldTotalPp is { } oldPp && account.NewTotalPp is { } newPp)
            fields.Add(Field("PP", Invariant($"{oldPp:N1}pp → {newPp:N1}pp ({Signed(newPp - oldPp, 1)})")));
        else if (summary.PpChange is { } ppChange)
            fields.Add(Field("PP", Signed(ppChange, 1)));

        var completionRate = summary.Attempts == 0 ? 0 : summary.Completed * 100d / summary.Attempts;
        fields.Add(Field(
            "Daily results",
            Invariant($"{summary.Attempts:N0} plays · {summary.Completed:N0} completed ({completionRate:0}%)\n") +
            Invariant($"{summary.AverageAccuracy:0.00}% average accuracy")));
        fields.Add(Field(
            "Playtime",
            Invariant($"{FormatPlaytime(summary.TotalDurationSeconds)} playtime · K1 {summary.ZTotal:N0} · K2 {summary.XTotal:N0} · {summary.ZTotal + summary.XTotal:N0} total")));

        if (report.MostPlayedMap is { } mostPlayed)
        {
            fields.Add(Field(
                "Most played map",
                $"**{MapName(mostPlayed.Artist, mostPlayed.Title, mostPlayed.Difficulty)}**\n" +
                Invariant($"{mostPlayed.Plays:N0} play{(mostPlayed.Plays == 1 ? "" : "s")} · {DisplayMods(mostPlayed.ModsKey)}")));
        }

        var titlePrefix = isTest ? "TEST · " : "";
        var thumbnail = account is { PlayerId: > 0 }
            ? new { url = $"https://a.ppy.sh/{account.PlayerId}" }
            : null;
        return new
        {
            username = "Kumori",
            embeds = new[]
            {
                new
                {
                    title = $"{titlePrefix}{EscapeMarkdown(playerName)}'s daily osu! recap",
                    color = 0xFF5C66,
                    fields,
                    thumbnail,
                    footer = new
                    {
                        text = $"{DisplayDay(summary.Day)} · Playtime is in-map time only",
                    },
                },
            },
        };
    }

    internal static bool TryValidateWebhookUrl(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps
            && (candidate.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                || candidate.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
                || candidate.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
                || candidate.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase))
            && candidate.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }

    private DateOnly? ReadLastProcessedDay()
    {
        if (lastProcessedInMemory is { } inMemory)
            return inMemory;
        try
        {
            return File.Exists(stateFile)
                   && DateOnly.TryParseExact(File.ReadAllText(stateFile).Trim(), "yyyy-MM-dd", out var day)
                ? day
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void MarkProcessed(DateOnly day)
    {
        lastProcessedInMemory = day;
        try
        {
            var directory = Path.GetDirectoryName(stateFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = stateFile + ".tmp";
            File.WriteAllText(temporary, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            File.Move(temporary, stateFile, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Daily webhook duplicate-protection state could not be saved");
        }
    }

    private static TimeSpan DelayUntilNextCheck(DateTime now)
    {
        var nextDailyCheck = now.Date.AddDays(1).AddMinutes(5);
        var hourlyCheck = now.AddHours(1);
        return (nextDailyCheck < hourlyCheck ? nextDailyCheck : hourlyCheck) - now;
    }

    private static object Field(string name, string value) => new
    {
        name,
        value = value.Length <= 1024 ? value : value[..1021] + "…",
        inline = false,
    };

    private static string MapName(string artist, string title, string difficulty)
    {
        var name = $"{EscapeMarkdown(artist)} – {EscapeMarkdown(title)}";
        return string.IsNullOrWhiteSpace(difficulty)
            ? name
            : $"{name} [{EscapeMarkdown(difficulty)}]";
    }

    private static string DisplayMods(string modsKey) =>
        string.IsNullOrWhiteSpace(modsKey) || modsKey.Equals("NM", StringComparison.OrdinalIgnoreCase)
            ? "NM"
            : EscapeMarkdown(modsKey);

    private static string EscapeMarkdown(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal);

    private static string Signed(long value) => Invariant($"{value:+#,0;-#,0;0}");
    private static string Signed(double value, int decimals) => decimals == 1
        ? Invariant($"{value:+0.0;-0.0;0.0}")
        : Invariant($"{value:+0;-0;0}");

    private static string FormatPlaytime(double seconds)
    {
        var totalMinutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        return totalMinutes >= 60
            ? Invariant($"{totalMinutes / 60}h {totalMinutes % 60:00}m")
            : Invariant($"{totalMinutes}m");
    }

    private static string DisplayDay(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", out var parsed)
            ? parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : day;

    private static string CountryFlag(string? countryCode)
    {
        if (countryCode?.Trim().ToUpperInvariant() is not { Length: 2 } code
            || code.Any(character => character is < 'A' or > 'Z'))
            return string.Empty;
        return char.ConvertFromUtf32(0x1F1E6 + code[0] - 'A')
               + char.ConvertFromUtf32(0x1F1E6 + code[1] - 'A');
    }
}

internal enum DailyWebhookOutcome
{
    Disabled,
    AlreadyProcessed,
    NoActivity,
    Sent,
    Failed,
}
