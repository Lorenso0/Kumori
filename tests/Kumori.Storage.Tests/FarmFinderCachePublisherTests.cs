using System.Security.Cryptography;
using System.Text.Json;
using Kumori.FarmFinder;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class FarmFinderCachePublisherTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"kumori-farm-publish-{Guid.NewGuid():N}");

    [Fact]
    public async Task Publish_CreatesACompleteVerifiedTwoFilePackage()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.sqlite3");
        var output = Path.Combine(root, "output");
        var repository = new FarmFinderRepository(source);
        var staleJob = await repository.BeginOrResumeJobAsync(200, 300);
        await repository.CompleteJobAsync(staleJob.Id, cancelled: true);
        await CreatePopulatedDatabaseAsync(source);
        var newerIncomplete = await repository.BeginOrResumeJobAsync(400, 500);
        var now = DateTimeOffset.Parse("2026-07-30T12:30:00Z");
        await repository.UpsertRankingPlayersAsync(
            newerIncomplete.Id,
            [
                new FarmPlayer(1, "Player", 450, 13_000, now),
                new FarmPlayer(2, "Partial", 451, 12_999, now),
            ]);
        await repository.SaveAsync(
            500,
            "DT:{}",
            "osu-lazer/test",
            7.123);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = source,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ConnectionString))
        {
            connection.Open();
            using var inconsistentHistoricalTotal = connection.CreateCommand();
            inconsistentHistoricalTotal.CommandText = """
                UPDATE farm_index_jobs
                SET players_total=999
                WHERE status='completed'
                """;
            inconsistentHistoricalTotal.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var generatedAt = DateTimeOffset.Parse("2026-07-30T13:14:15Z");
        var publisher = new FarmFinderCachePublisher(() => generatedAt);

        var result = publisher.Publish(new FarmFinderCachePublishOptions(
            source,
            output,
            new Uri("https://cache.example.test/farm-finder"),
            "0.6.2"));

        Assert.Equal(
            "farm-finder-v6-20260730T131415Z.sqlite3",
            Path.GetFileName(result.DatabasePath));
        Assert.Equal(2, Directory.GetFiles(result.PackageDirectory).Length);
        Assert.False(File.Exists(result.DatabasePath + "-wal"));
        Assert.False(File.Exists(result.DatabasePath + "-shm"));
        using (var published = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = result.DatabasePath,
                       Mode = SqliteOpenMode.ReadOnly,
                       Pooling = false,
                   }.ConnectionString))
        {
            published.Open();
            using var jobs = published.CreateCommand();
            jobs.CommandText =
                "SELECT COUNT(*) FROM farm_index_jobs WHERE status IN ('running','paused')";
            Assert.Equal(0L, (long)jobs.ExecuteScalar()!);

            using var discarded = published.CreateCommand();
            discarded.CommandText =
                "SELECT COUNT(*) FROM farm_index_jobs WHERE status='discarded'";
            Assert.Equal(2L, (long)discarded.ExecuteScalar()!);

            using var cachedPlayers = published.CreateCommand();
            cachedPlayers.CommandText =
                "SELECT global_rank FROM farm_players WHERE user_id=1";
            Assert.Equal(450L, (long)cachedPlayers.ExecuteScalar()!);

            using var cachedRating = published.CreateCommand();
            cachedRating.CommandText = """
                SELECT star_rating
                FROM farm_star_ratings
                WHERE beatmap_id=500
                  AND mods_key='DT:{}'
                  AND calculator_version='osu-lazer/test'
                """;
            Assert.Equal(7.123, Convert.ToDouble(cachedRating.ExecuteScalar()));
        }
        using var stream = File.OpenRead(result.DatabasePath);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(stream)),
            result.Sha256);

        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(result.ManifestPath));
        var rootElement = manifest.RootElement;
        Assert.Equal(1, rootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(6, rootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(result.SizeBytes, rootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(result.Sha256, rootElement.GetProperty("sha256").GetString());
        Assert.Equal(
            result.DatabaseUri.AbsoluteUri,
            rootElement.GetProperty("databaseUrl").GetString());
        Assert.Equal(
            "0.6.2",
            rootElement.GetProperty("minimumAppVersion").GetString());
    }

    [Fact]
    public async Task Publish_RejectsWhenNoCompletedIndexExistsAndRemovesPartialPackage()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.sqlite3");
        var output = Path.Combine(root, "output");
        var repository = new FarmFinderRepository(source);
        await repository.InitializeAsync();
        await repository.BeginOrResumeJobAsync(1, 100);
        SqliteConnection.ClearAllPools();
        var publisher = new FarmFinderCachePublisher(
            () => DateTimeOffset.Parse("2026-07-30T13:14:15Z"));

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            publisher.Publish(new FarmFinderCachePublishOptions(
                source,
                output,
                new Uri("https://cache.example.test/farm-finder")))));

        Assert.Empty(Directory.GetDirectories(output));
    }

    [Fact]
    public async Task Publish_MigratesAVersionFourSourceToCurrentSchema()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source-v4.sqlite3");
        var output = Path.Combine(root, "output-v6");
        await CreatePopulatedDatabaseAsync(source);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = source,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ConnectionString))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE farm_star_ratings;
                UPDATE farm_metadata SET value='4' WHERE key='schema_version';
                """;
            downgrade.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var publisher = new FarmFinderCachePublisher(
            () => DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

        var result = publisher.Publish(new FarmFinderCachePublishOptions(
            source,
            output,
            new Uri("https://cache.example.test/farm-finder")));

        using var published = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = result.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString);
        published.Open();
        using var schema = published.CreateCommand();
        schema.CommandText =
            "SELECT value FROM farm_metadata WHERE key='schema_version'";
        Assert.Equal("6", schema.ExecuteScalar());
        using var ratings = published.CreateCommand();
        ratings.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='farm_star_ratings'";
        Assert.Equal(1L, ratings.ExecuteScalar());
    }

    private static async Task CreatePopulatedDatabaseAsync(string path)
    {
        var repository = new FarmFinderRepository(path);
        var now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");
        var player = new FarmPlayer(1, "Player", 50, 12_000, now);
        var map = new FarmBeatmap(
            500, 50, "Artist", "Title", "Insane", "Mapper",
            180, 100, 120, 6.2, "ranked", now, "");
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(job.Id, [player]);
        await repository.UpdateJobCursorAsync(job.Id, null, playersTotal: 1);
        await repository.ReplacePlayerScoresAsync(new PlayerScoresPayload(
            player with { ScoresUpdatedAt = now },
            [new FarmScore(
                100, player.UserId, map.BeatmapId, 350, .98, 0, 500,
                true, now, [new FarmMod("HD")], "HD", 1)],
            [map]));
        await repository.MarkPlayerCompletedAsync(job.Id, player.UserId);
        await repository.CompleteJobAsync(job.Id, cancelled: false);
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
