using Kumori.FarmFinder;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class FarmFinderRepositoryTests : IDisposable
{
    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"kumori-farm-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public async Task Initialize_CreatesVersionedSchemaAndRequiredIndexes()
    {
        var repository = new FarmFinderRepository(databasePath);
        await repository.InitializeAsync();

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'idx_farm_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var indexes = new List<string>();
        while (reader.Read())
            indexes.Add(reader.GetString(0));

        Assert.Contains("idx_farm_players_rank", indexes);
        Assert.Contains("idx_farm_players_scores_updated", indexes);
        Assert.Contains("idx_farm_scores_pp", indexes);
        Assert.Contains("idx_farm_scores_user", indexes);
        Assert.Contains("idx_farm_scores_beatmap_mods", indexes);
        Assert.Contains("idx_farm_beatmaps_status_date", indexes);
        Assert.Contains("idx_farm_snapshot_members_rank", indexes);
        Assert.Contains("idx_farm_country_coverage_gap", indexes);

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM farm_metadata WHERE key='schema_version'";
        Assert.Equal("5", version.ExecuteScalar());
    }

    [Fact]
    public async Task ExactStarRatingsPersistByBeatmapModsAndCalculatorVersion()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var player = new FarmPlayer(1, "Player", 10, 12_000, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        await repository.ReplacePlayerScoresAsync(Payload(player, now, 101));

        await repository.SaveAsync(500, "DT:{}", "osu-lazer/test-a", 7.123);
        await repository.SaveAsync(500, "DT:{}", "osu-lazer/test-b", 7.456);

        Assert.Equal(
            7.123,
            await repository.GetAsync(500, "DT:{}", "osu-lazer/test-a"));
        Assert.Equal(
            7.456,
            await repository.GetAsync(500, "DT:{}", "osu-lazer/test-b"));
        Assert.Null(
            await repository.GetAsync(500, "HR:{}", "osu-lazer/test-a"));
        var loaded = Assert.Single(
            await repository.LoadAsync("osu-lazer/test-a"));
        Assert.Equal(500, loaded.BeatmapId);
        Assert.Equal("DT:{}", loaded.ModsKey);
        Assert.Equal(7.123, loaded.StarRating);
    }

    [Fact]
    public async Task VersionFourDatabaseMigratesWithoutReindexingScores()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var player = new FarmPlayer(1, "Player", 10, 12_000, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        await repository.ReplacePlayerScoresAsync(Payload(player, now, 101));

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE farm_star_ratings;
                UPDATE farm_metadata SET value='4' WHERE key='schema_version';
                """;
            downgrade.ExecuteNonQuery();
        }

        var migrated = new FarmFinderRepository(databasePath);
        await migrated.InitializeAsync();

        Assert.Single(await migrated.QueryCandidatesAsync(new FarmFinderQuery()));
        using var migratedConnection = new SqliteConnection($"Data Source={databasePath}");
        migratedConnection.Open();
        using var version = migratedConnection.CreateCommand();
        version.CommandText = "SELECT value FROM farm_metadata WHERE key='schema_version'";
        Assert.Equal("5", version.ExecuteScalar());
        using var ratings = migratedConnection.CreateCommand();
        ratings.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='farm_star_ratings'";
        Assert.Equal(1L, ratings.ExecuteScalar());
    }

    [Fact]
    public async Task CountryCoverageGapsPersistWithSnapshotCoverage()
    {
        var repository = new FarmFinderRepository(databasePath);
        var job = await repository.BeginOrResumeJobAsync(20_000, 60_000);
        await repository.UpsertCountryCoverageAsync(
            job.Id,
            new CountryCoverage("US", 51_207, 60_000, false, true));
        await repository.UpsertCountryCoverageAsync(
            job.Id,
            new CountryCoverage("NL", 60_000, 60_000, true, false));

        var coverage = await repository.GetCoverageAsync(new FarmFinderQuery
        {
            MinimumGlobalRank = 20_000,
            MaximumGlobalRank = 60_000,
        });

        var gap = Assert.Single(coverage.CountryGaps!);
        Assert.Equal("US", gap.CountryCode);
        Assert.Equal(51_207, gap.CoveredThroughGlobalRank);
        Assert.True(coverage.IsPartial);
    }

    [Fact]
    public async Task ClassicEligibilityUpgradeMarksCachedPlayersStaleWithoutDiscardingScores()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var player = new FarmPlayer(1, "Player", 10, 12_000, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        await repository.ReplacePlayerScoresAsync(Payload(player, now, 101));

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                "UPDATE farm_metadata SET value='2' WHERE key='schema_version'";
            downgrade.ExecuteNonQuery();
        }

        var upgraded = new FarmFinderRepository(databasePath);
        await upgraded.InitializeAsync();

        var cachedPlayer = Assert.Single(
            await upgraded.GetPlayersInRangeAsync(1, 100));
        Assert.Null(cachedPlayer.ScoresUpdatedAt);
        Assert.Single(
            await upgraded.QueryCandidatesAsync(new FarmFinderQuery()));
    }

    [Fact]
    public async Task ReplacePlayerScores_IsAtomicAndRemovesStaleScores()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var player = new FarmPlayer(1, "Player", 10, 12_000, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var membership = connection.CreateCommand();
            membership.CommandText =
                "SELECT COUNT(*) FROM farm_ranking_snapshot_members WHERE snapshot_id=@snapshot";
            membership.Parameters.AddWithValue("@snapshot", job.Id);
            Assert.Equal(1L, (long)(membership.ExecuteScalar() ?? 0L));
        }
        await repository.ReplacePlayerScoresAsync(Payload(player, now, 101, 102));
        Assert.Equal(2, (await repository.QueryCandidatesAsync(new FarmFinderQuery())).Count);

        await repository.ReplacePlayerScoresAsync(Payload(player, now.AddMinutes(1), 103));
        var remaining = await repository.QueryCandidatesAsync(new FarmFinderQuery());

        Assert.Single(remaining);
        Assert.Equal(103, remaining[0].Score.ScoreId);
    }

    [Fact]
    public async Task CandidateQueryReusesIdenticalDeserializedModSets()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var player = new FarmPlayer(1, "Player", 10, 12_000, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        await repository.ReplacePlayerScoresAsync(Payload(player, now, 101, 102));

        var candidates = await repository.QueryCandidatesAsync(new FarmFinderQuery());

        Assert.Equal(2, candidates.Count);
        Assert.Same(candidates[0].Score.ActualMods, candidates[1].Score.ActualMods);
    }

    [Fact]
    public async Task CancelledReplacement_LeavesLastValidScoreSet()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var player = new FarmPlayer(1, "Player", 10, 12_000, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        await repository.ReplacePlayerScoresAsync(Payload(player, now, 101));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ReplacePlayerScoresAsync(Payload(player, now, 102), cancellation.Token));

        var remaining = await repository.QueryCandidatesAsync(new FarmFinderQuery());
        Assert.Single(remaining);
        Assert.Equal(101, remaining[0].Score.ScoreId);
    }

    [Fact]
    public async Task CandidateQueryPushesEffectiveAndModFiltersIntoSql()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var nightcorePlayer = new FarmPlayer(1, "Nightcore", 10, 12_000, now);
        var nomodPlayer = new FarmPlayer(2, "Nomod", 11, 11_900, now);
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [nightcorePlayer, nomodPlayer]);
        await repository.ReplacePlayerScoresAsync(
            PayloadWithMods(nightcorePlayer, now, 101, [new FarmMod("NC")]));
        await repository.ReplacePlayerScoresAsync(
            PayloadWithMods(nomodPlayer, now, 102, []));

        var candidates = await repository.QueryCandidatesAsync(new FarmFinderQuery
        {
            MinimumEffectiveBpm = 260,
            MaximumEffectiveBpm = 280,
            Mods = [new FarmModFilter("DT", ModRequirement.Required)],
            TreatNightcoreAsDoubleTime = true,
        });

        var candidate = Assert.Single(candidates);
        Assert.Equal(nightcorePlayer.UserId, candidate.Player.UserId);
        Assert.Equal(270, candidate.Beatmap.BaseBpm * candidate.Score.ClockRate, 6);
    }

    [Fact]
    public async Task PausedJob_IsResumableWithCountersAndCursor()
    {
        var repository = new FarmFinderRepository(databasePath);
        var job = await repository.BeginOrResumeJobAsync(100, 200);
        await repository.UpdateJobCursorAsync(job.Id, "{\"page\":2}", 50);
        await repository.CompleteJobAsync(job.Id, cancelled: true);

        var resumed = await repository.BeginOrResumeJobAsync(100, 200);

        Assert.Equal(job.Id, resumed.Id);
        Assert.Equal("running", resumed.Status);
        Assert.Equal("{\"page\":2}", resumed.CursorJson);
        Assert.Equal(50, resumed.PlayersTotal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
            File.Delete(databasePath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(databasePath + suffix))
                File.Delete(databasePath + suffix);
        }
    }

    private static PlayerScoresPayload Payload(
        FarmPlayer player,
        DateTimeOffset updated,
        params long[] scoreIds)
    {
        var map = new FarmBeatmap(
            500, 50, "Artist", "Title", "Insane", "Mapper",
            180, 100, 120, 6.2, "ranked", updated, "");
        return new PlayerScoresPayload(
            player with { ScoresUpdatedAt = updated },
            scoreIds.Select(id => new FarmScore(
                id, player.UserId, map.BeatmapId, id, 0.98, 0, 500,
                true, updated, [new FarmMod("HD")], "HD", 1)).ToArray(),
            [map]);
    }

    private static PlayerScoresPayload PayloadWithMods(
        FarmPlayer player,
        DateTimeOffset updated,
        long scoreId,
        IReadOnlyList<FarmMod> mods)
    {
        var map = new FarmBeatmap(
            500 + scoreId, 50, "Artist", "Title", "Insane", "Mapper",
            180, 100, 120, 6.2, "ranked", updated, "");
        var clockRate = new ClockRateCalculator().Calculate(mods);
        var normalized = new ModNormalizer(new ClockRateCalculator()).Normalize(
            mods,
            new ModNormalizationOptions(true, false));
        return new PlayerScoresPayload(
            player with { ScoresUpdatedAt = updated },
            [new FarmScore(
                scoreId, player.UserId, map.BeatmapId, 350, 0.98, 0, 500,
                true, updated, mods, normalized.Signature, clockRate)],
            [map]);
    }
}
