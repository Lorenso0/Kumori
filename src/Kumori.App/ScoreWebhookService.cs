using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.FarmFinder;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;
using static System.FormattableString;

namespace Kumori.App;

public sealed class ScoreWebhookService
{
    internal const long DiscordAttachmentLimit = 10L * 1024 * 1024;
    internal const string ScoreCardAttachmentName = "kumori-pb-card.png";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] VerificationDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];
    private static readonly TimeSpan[] FailureDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    private readonly SettingsService settings;
    private readonly ScoreWebhookRepository deliveries;
    private readonly AttemptDetailsRepository details;
    private readonly MovementRepository movement;
    private readonly PlaySharePackageService packages;
    private readonly IOsuBeatmapScoreProvider osuApi;
    private readonly Func<OsuProfileIdentity?> currentProfile;
    private readonly HttpClient http;

    public ScoreWebhookService(
        SettingsService settings,
        ScoreWebhookRepository deliveries,
        AttemptDetailsRepository details,
        MovementRepository movement,
        PlaySharePackageService packages,
        IOsuBeatmapScoreProvider osuApi,
        Func<OsuProfileIdentity?> currentProfile,
        HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.deliveries = deliveries;
        this.details = details;
        this.movement = movement;
        this.packages = packages;
        this.osuApi = osuApi;
        this.currentProfile = currentProfile;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public void ObserveAttemptPersisted(long attemptId)
    {
        if (!settings.Current.DailyWebhook.ScoreAlertsEnabled)
            return;
        OsuProfileIdentity? profile = currentProfile();
        if (profile is null)
            return;
        try
        {
            deliveries.TryEnqueue(
                attemptId,
                profile.PlayerId,
                profile.PlayerName,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not queue score webhook for attempt {AttemptId}", attemptId);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!settings.Current.DailyWebhook.ScoreAlertsEnabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    continue;
                }
                ScoreWebhookDelivery? delivery = deliveries.GetNextDue(DateTimeOffset.UtcNow);
                if (delivery is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                    continue;
                }
                if (delivery.State == "pending")
                    await VerifyAsync(delivery, cancellationToken);
                else
                    await DeliverAsync(delivery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Score webhook worker iteration failed");
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    public async Task SendTestAsync(string webhookUrl, CancellationToken cancellationToken = default)
    {
        long attemptId = deliveries.GetRandomReplayAttemptId()
            ?? throw new InvalidOperationException(
                "Kumori does not have a completed score with captured replay movement to test yet.");
        AttemptDetails attempt = await Task.Run(
            () => details.GetDetails(attemptId),
            cancellationToken)
            ?? throw new InvalidOperationException("The selected test score is no longer available.");
        OsuProfileIdentity? profile = currentProfile();
        string playerName = !string.IsNullOrWhiteSpace(attempt.Summary.PlayerName)
            ? attempt.Summary.PlayerName
            : profile?.PlayerName ?? "Kumori user";
        int? rank = null;
        long scoreId = 0;
        if (profile is not null && attempt.Summary.OsuBeatmapId is > 0)
        {
            try
            {
                OsuBeatmapUserScore? online = await osuApi.GetBeatmapUserScoreAsync(
                    attempt.Summary.OsuBeatmapId.Value,
                    profile.PlayerId,
                    cancellationToken);
                if (online is not null && Matches(attempt, online))
                {
                    rank = online.Position;
                    scoreId = online.ScoreId;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                              && exception is HttpRequestException
                                                  or TaskCanceledException
                                                  or OsuApiAuthenticationException
                                                  or OsuApiRateLimitException
                                                  or InvalidDataException)
            {
                Log.Debug(exception, "Could not match the random test score to an official osu! score");
            }
        }

        IReadOnlyList<ShareMediaFile> replayMedia = ResolveDiscordReplayMedia(
            attempt,
            cancellationToken);
        string attachmentPath = Path.Combine(
            Path.GetTempPath(),
            $"kumori-score-test-{Guid.NewGuid():N}.kumori");
        try
        {
            string attachmentName = SafeReplayFileName(playerName, attempt);
            await packages.ExportAsync(
                attemptId,
                playerName,
                attachmentPath,
                replayMedia,
                ["Backgrounds, videos, and custom hitsounds are omitted from Discord replay packages."],
                KumoriPackageProfile.CompactDiscord,
                cancellationToken);
            _ = await packages.PreviewAsync(attachmentPath, cancellationToken);
            if (new FileInfo(attachmentPath).Length > DiscordAttachmentLimit)
                throw new InvalidOperationException("The random test replay exceeds Discord's 10 MiB attachment limit.");
            await SendScoreCardAsync(
                webhookUrl,
                attempt,
                playerName,
                rank,
                scoreId,
                profile?.PlayerId ?? 0,
                replayAttached: true,
                isTest: true,
                cancellationToken);
            await SendWebhookAsync(
                webhookUrl,
                BuildAttachmentPayload(),
                attachmentPath,
                attachmentName,
                cancellationToken);
        }
        finally
        {
            try { File.Delete(attachmentPath); } catch { }
        }
    }

    private async Task VerifyAsync(ScoreWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - delivery.CreatedAt > TimeSpan.FromHours(24))
        {
            deliveries.MarkTerminal(delivery.AttemptId, "expired", now, "verification_timeout");
            return;
        }
        AttemptDetails? attempt = await Task.Run(
            () => details.GetDetails(delivery.AttemptId),
            cancellationToken);
        if (attempt?.Summary.OsuBeatmapId is not > 0)
        {
            deliveries.MarkTerminal(delivery.AttemptId, "ineligible", now, "missing_attempt");
            return;
        }

        try
        {
            OsuBeatmapUserScore? online = await osuApi.GetBeatmapUserScoreAsync(
                attempt.Summary.OsuBeatmapId.Value,
                delivery.PlayerId,
                cancellationToken);
            if (online is not null && Matches(attempt, online))
            {
                deliveries.MarkConfirmed(
                    delivery.AttemptId,
                    online.Position,
                    online.ScoreId,
                    now);
                return;
            }
            if (delivery.VerificationAttempts >= VerificationDelays.Length)
            {
                deliveries.MarkTerminal(delivery.AttemptId, "unconfirmed", now, "score_not_confirmed");
                return;
            }
            deliveries.ScheduleVerification(
                delivery.AttemptId,
                now + VerificationDelays[delivery.VerificationAttempts],
                "score_not_propagated");
        }
        catch (OsuApiAuthenticationException)
        {
            deliveries.MarkTerminal(delivery.AttemptId, "blocked", now, "osu_credentials");
            Log.Warning("Score webhook paused because osu! API credentials were rejected");
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TaskCanceledException
                                          or OsuApiRateLimitException
                                          or InvalidDataException)
        {
            ScheduleTransientVerification(delivery, now);
        }
    }

    private async Task DeliverAsync(ScoreWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - delivery.CreatedAt > TimeSpan.FromHours(24))
        {
            deliveries.MarkTerminal(delivery.AttemptId, "expired", now, "delivery_timeout");
            return;
        }
        AttemptDetails? attempt = await Task.Run(
            () => details.GetDetails(delivery.AttemptId),
            cancellationToken);
        if (attempt is null || delivery.ConfirmedRank is null || delivery.ConfirmedScoreId is null)
        {
            deliveries.MarkTerminal(delivery.AttemptId, "ineligible", now, "missing_attempt");
            return;
        }

        string? attachmentPath = null;
        string? attachmentName = null;
        var replayStatus = "unavailable";
        try
        {
            MovementMetadata? metadata = await Task.Run(
                () => movement.GetMetadata(delivery.AttemptId, cancellationToken),
                cancellationToken);
            if (metadata is null && delivery.ReplayDeadlineAt is { } deadline && now < deadline)
            {
                deliveries.PostponeDelivery(delivery.AttemptId, now.AddSeconds(30), "waiting_for_replay");
                return;
            }
            if (metadata is not null)
            {
                try
                {
                    IReadOnlyList<ShareMediaFile> replayMedia = ResolveDiscordReplayMedia(
                        attempt,
                        cancellationToken);
                    attachmentName = SafeReplayFileName(delivery.PlayerName, attempt);
                    attachmentPath = Path.Combine(
                        Path.GetTempPath(),
                        $"kumori-score-{Guid.NewGuid():N}.kumori");
                    await packages.ExportAsync(
                        delivery.AttemptId,
                        delivery.PlayerName,
                        attachmentPath,
                        replayMedia,
                        ["Backgrounds, videos, and custom hitsounds are omitted from Discord replay packages."],
                        KumoriPackageProfile.CompactDiscord,
                        cancellationToken);
                    _ = await packages.PreviewAsync(attachmentPath, cancellationToken);
                    if (new FileInfo(attachmentPath).Length <= DiscordAttachmentLimit)
                        replayStatus = "attached";
                    else
                    {
                        File.Delete(attachmentPath);
                        attachmentPath = null;
                        attachmentName = null;
                        replayStatus = "too_large";
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or InvalidOperationException)
                {
                    Log.Debug(exception, "Could not construct replay attachment for attempt {AttemptId}", delivery.AttemptId);
                    if (attachmentPath is not null)
                    {
                        try { File.Delete(attachmentPath); } catch { }
                    }
                    attachmentPath = null;
                    attachmentName = null;
                    replayStatus = "unavailable";
                }
            }

            string webhookUrl = settings.Current.DailyWebhook.ScoreAlertsWebhookUrl;
            await SendScoreCardAsync(
                webhookUrl,
                attempt,
                delivery.PlayerName,
                delivery.ConfirmedRank.Value,
                delivery.ConfirmedScoreId.Value,
                delivery.PlayerId,
                replayStatus == "attached",
                isTest: false,
                cancellationToken);
            if (attachmentPath is not null && attachmentName is not null)
            {
                try
                {
                    await SendWebhookAsync(
                        webhookUrl,
                        BuildAttachmentPayload(),
                        attachmentPath,
                        attachmentName,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is DiscordAttachmentRejectedException
                                                  or HttpRequestException
                                                  or TaskCanceledException)
                {
                    replayStatus = exception is DiscordAttachmentRejectedException
                        ? "rejected"
                        : "unavailable";
                    Log.Warning(exception, "PB card was delivered, but replay attachment upload failed for attempt {AttemptId}", delivery.AttemptId);
                    await TrySendAttachmentUnavailableNoticeAsync(webhookUrl, cancellationToken);
                }
            }
            deliveries.MarkDelivered(delivery.AttemptId, DateTimeOffset.UtcNow, replayStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException)
        {
            // Replay construction is best-effort. Deliver the confirmed card
            // without an attachment before treating this as a webhook failure.
            try
            {
                await SendWebhookAsync(
                    settings.Current.DailyWebhook.ScoreAlertsWebhookUrl,
                    BuildPayload(
                        attempt,
                        delivery.PlayerName,
                        delivery.ConfirmedRank.Value,
                        delivery.ConfirmedScoreId.Value,
                        replayAttached: false,
                        isTest: false),
                    null,
                    null,
                    cancellationToken);
                deliveries.MarkDelivered(delivery.AttemptId, DateTimeOffset.UtcNow, "unavailable");
            }
            catch (Exception sendException) when (sendException is HttpRequestException
                                                   or TaskCanceledException
                                                   or InvalidOperationException)
            {
                ScheduleTransientDelivery(delivery, DateTimeOffset.UtcNow, "discord");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ScheduleTransientDelivery(delivery, DateTimeOffset.UtcNow, "discord");
        }
        finally
        {
            if (attachmentPath is not null)
            {
                try { File.Delete(attachmentPath); } catch { }
            }
        }
    }

    internal static bool Matches(AttemptDetails local, OsuBeatmapUserScore online)
    {
        if (local.Summary.OsuBeatmapId != online.BeatmapId
            || local.Summary.Score != online.TotalScore
            || local.Summary.Combo != online.MaxCombo
            || local.N300 != online.N300
            || local.N100 != online.N100
            || local.N50 != online.N50
            || local.Summary.Misses != online.Misses
            || Math.Abs(local.Summary.Accuracy - online.Accuracy * 100d) > 0.02)
            return false;
        if (!DateTimeOffset.TryParse(local.Summary.EndedAt, out DateTimeOffset endedAt)
            || Math.Abs((endedAt.ToUniversalTime() - online.EndedAt.ToUniversalTime()).TotalMinutes) > 2)
            return false;
        static HashSet<string> Mods(IEnumerable<string> values) => values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => value is not "" and not "NM" and not "CL")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Mods(local.Mods.Select(mod => mod.Acronym)).SetEquals(Mods(online.Mods));
    }

    internal static object BuildPayload(
        AttemptDetails attempt,
        string playerName,
        int? rank,
        long scoreId,
        bool replayAttached,
        bool isTest)
    {
        AttemptSummary summary = attempt.Summary;
        string stars = (summary.AdjustedStars ?? summary.Stars) is { } value
            ? Invariant($"{value:0.00}★")
            : "?★";
        string mods = string.IsNullOrWhiteSpace(summary.ModsKey)
            || summary.ModsKey.Equals("NM", StringComparison.OrdinalIgnoreCase)
            ? "NM"
            : "+" + Escape(summary.ModsKey.Replace(",", "", StringComparison.Ordinal));
        string description =
            $"**{Escape(summary.Title)}** [{Escape(summary.Difficulty)}]\n" +
            $"🟢 ({stars}) {mods}\n" +
            Invariant($"▷ x{summary.Combo:N0}/{summary.BeatmapMaxCombo:N0}  ▷ [{attempt.N300}/{attempt.N100}/{attempt.N50}/{summary.Misses}]");
        string setTime = DateTimeOffset.TryParse(summary.EndedAt, out DateTimeOffset endedAt)
            ? endedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : summary.EndedAt ?? "unknown";
        string replayText = replayAttached
            ? "Replay attachment follows below"
            : "Replay attachment unavailable";
        object[] fields =
        [
            new { name = "PP", value = Invariant($"{summary.Pp:0.##}pp"), inline = true },
            new { name = "Accuracy", value = Invariant($"{summary.Accuracy:0.00}%"), inline = true },
        ];
        object? thumbnail = summary.BeatmapSetId is > 0
            ? new { url = $"https://assets.ppy.sh/beatmaps/{summary.BeatmapSetId}/covers/list@2x.jpg" }
            : null;
        return new
        {
            username = "Kumori",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = rank is > 0
                        ? $"{(isTest ? "TEST · " : "")}New #{rank:N0} for {Escape(playerName)} in osu!"
                        : $"PB alert test for {Escape(playerName)}",
                    url = scoreId > 0 ? $"https://osu.ppy.sh/scores/{scoreId}" : null,
                    description,
                    color = 0xFF5C66,
                    fields,
                    thumbnail,
                    footer = new { text = $"Score set · {setTime} · {replayText}" },
                },
            },
        };
    }

    internal static object BuildImagePayload(AttemptDetails attempt) => new
    {
        username = "Kumori",
        allowed_mentions = new { parse = Array.Empty<string>() },
        attachments = new[]
        {
            new
            {
                id = 0,
                filename = ScoreCardAttachmentName,
                description = $"Kumori personal-best score card for {attempt.Summary.Artist} — {attempt.Summary.Title} [{attempt.Summary.Difficulty}]",
            },
        },
    };

    private async Task SendScoreCardAsync(
        string webhookUrl,
        AttemptDetails attempt,
        string playerName,
        int? rank,
        long scoreId,
        long playerId,
        bool replayAttached,
        bool isTest,
        CancellationToken cancellationToken)
    {
        string scoreCardPath = Path.Combine(
            Path.GetTempPath(),
            $"kumori-pb-card-{Guid.NewGuid():N}.png");
        string? avatarPath = null;
        var rendered = false;
        try
        {
            try
            {
                if (playerId > 0)
                {
                    try
                    {
                        byte[]? avatar = await DailyProgressWebhookService.DownloadAvatarAsync(
                            playerId,
                            cancellationToken);
                        if (avatar is { Length: > 0 and <= 5 * 1024 * 1024 })
                        {
                            avatarPath = Path.Combine(
                                Path.GetTempPath(),
                                $"kumori-pb-avatar-{Guid.NewGuid():N}.img");
                            await File.WriteAllBytesAsync(avatarPath, avatar, cancellationToken);
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                                      && exception is HttpRequestException
                                                          or TaskCanceledException
                                                          or IOException
                                                          or InvalidDataException)
                    {
                        Log.Debug(exception, "Could not load osu! profile avatar for PB card");
                    }
                }
                await PbScoreCardRenderer.RenderAsync(
                    attempt,
                    playerName,
                    rank,
                    scoreId,
                    deliveries.GetProfileChange(attempt.Summary.Id),
                    replayAttached,
                    isTest,
                    ResolveScoreCardArtwork(attempt),
                    avatarPath,
                    scoreCardPath,
                    cancellationToken);
                rendered = new FileInfo(scoreCardPath).Length is > 0 and <= DiscordAttachmentLimit;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not render PB score card; using native Discord embed");
            }

            if (rendered)
            {
                try
                {
                    await SendWebhookAsync(
                        webhookUrl,
                        BuildImagePayload(attempt),
                        scoreCardPath,
                        ScoreCardAttachmentName,
                        cancellationToken);
                    return;
                }
                catch (DiscordAttachmentRejectedException exception)
                {
                    Log.Warning(exception, "Discord rejected the rendered PB score card; using native embed");
                }
            }

            await SendWebhookAsync(
                webhookUrl,
                BuildPayload(attempt, playerName, rank, scoreId, replayAttached, isTest),
                null,
                null,
                cancellationToken);
        }
        finally
        {
            try { File.Delete(scoreCardPath); } catch { }
            if (avatarPath is not null)
            {
                try { File.Delete(avatarPath); } catch { }
            }
        }
    }

    private static object BuildAttachmentPayload() => new
    {
        username = "Kumori",
        allowed_mentions = new { parse = Array.Empty<string>() },
    };

    internal async Task SendWebhookAsync(
        string webhookUrl,
        object payload,
        string? attachmentPath,
        string? attachmentName,
        CancellationToken cancellationToken)
    {
        if (!DailyProgressWebhookService.TryValidateWebhookUrl(webhookUrl, out Uri uri))
            throw new InvalidOperationException("Enter a valid HTTPS Discord webhook URL.");
        var builder = new UriBuilder(uri);
        string wait = "wait=true";
        builder.Query = string.IsNullOrWhiteSpace(builder.Query)
            ? wait
            : builder.Query.TrimStart('?') + "&" + wait;
        using var content = new MultipartFormDataContent();
        content.Add(
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            "payload_json");
        FileStream? stream = null;
        try
        {
            if (attachmentPath is not null && attachmentName is not null)
            {
                stream = new FileStream(
                    attachmentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                content.Add(new StreamContent(stream), "files[0]", attachmentName);
            }
            using HttpResponseMessage response = await http.PostAsync(
                builder.Uri,
                content,
                cancellationToken);
            if (attachmentPath is not null
                && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
                throw new DiscordAttachmentRejectedException();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Discord returned HTTP {(int)response.StatusCode}.");
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    private void ScheduleTransientVerification(ScoreWebhookDelivery delivery, DateTimeOffset now)
    {
        int index = Math.Min(delivery.ApiFailureAttempts, FailureDelays.Length - 1);
        deliveries.ScheduleApiFailure(delivery.AttemptId, now + FailureDelays[index], "osu_api");
    }

    private void ScheduleTransientDelivery(ScoreWebhookDelivery delivery, DateTimeOffset now, string category)
    {
        int index = Math.Min(delivery.DeliveryAttempts, FailureDelays.Length - 1);
        deliveries.ScheduleDelivery(delivery.AttemptId, now + FailureDelays[index], category);
    }

    private async Task TrySendAttachmentUnavailableNoticeAsync(
        string webhookUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendWebhookAsync(
                webhookUrl,
                new
                {
                    username = "Kumori",
                    content = "Replay attachment unavailable",
                    allowed_mentions = new { parse = Array.Empty<string>() },
                },
                null,
                null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TaskCanceledException
                                          or InvalidOperationException)
        {
            Log.Debug(exception, "Could not post replay-unavailable follow-up");
        }
    }

    private static string? ResolveScoreCardArtwork(AttemptDetails attempt)
    {
        if (!string.IsNullOrWhiteSpace(attempt.LocalBackgroundPath)
            && File.Exists(attempt.LocalBackgroundPath))
            return attempt.LocalBackgroundPath;
        try
        {
            return ShareMediaResolver.Resolve(attempt).Files
                .FirstOrDefault(file => file.Role == "background" && File.Exists(file.Path))
                ?.Path;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<ShareMediaFile> ResolveDiscordReplayMedia(
        AttemptDetails attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ShareMediaFile> local = ShareMediaResolver.Resolve(attempt).Files
                .Where(file => file.Role is "beatmap" or "audio")
                .ToArray();
            if (local.Count(file => file.Role == "beatmap") == 1
                && local.Count(file => file.Role == "audio") == 1)
                return local;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException)
        {
            Log.Debug(exception, "Local replay media was incomplete; trying configured mirrors");
        }

        BeatmapMediaResolution? resolved = BeatmapMediaResolver.Resolve(
            attempt.Summary.OsuBeatmapId ?? 0,
            attempt.Summary.BeatmapSetId ?? 0,
            attempt.Summary.Checksum,
            attempt.Summary.Difficulty,
            settings.Current.Media.PrimaryMirror,
            settings.Current.Media.FallbackMirrors,
            cancellationToken);
        if (resolved is null)
            throw new InvalidOperationException(
                "Kumori could not resolve the beatmap and audio for this replay.");
        return
        [
            new ShareMediaFile(
                ShareMediaResolver.PortableBeatmapName(resolved.BeatmapPath, attempt),
                "beatmap",
                resolved.BeatmapPath),
            new ShareMediaFile(resolved.AudioLogicalName, "audio", resolved.AudioPath),
        ];
    }

    private static string SafeReplayFileName(string playerName, AttemptDetails attempt)
    {
        string raw = $"{playerName} - {attempt.Summary.Title} [{attempt.Summary.Difficulty}]";
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(raw.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        safe = string.Join(" ", safe.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (safe.Length > 160)
            safe = safe[..160].TrimEnd();
        return safe + PlaySharePackageService.FileExtension;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal);

    private sealed class DiscordAttachmentRejectedException : Exception;
}
