using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumori.FarmFinder;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class FarmFinderCacheInstallerTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"kumori-farm-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task ValidManifest_AtomicallyInstallsVerifiedPopulatedDatabase()
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.sqlite3");
        var destinationPath = Path.Combine(root, "destination.sqlite3");
        await CreatePopulatedDatabaseAsync(sourcePath);
        await CreatePopulatedDatabaseAsync(destinationPath);
        var destination = new FarmFinderRepository(destinationPath);
        await destination.SaveAsync(
            500,
            "DT:{}",
            "osu-lazer/test",
            7.123);
        var payload = File.ReadAllBytes(sourcePath);
        var handler = new CacheHandler(
            Manifest(payload),
            payload);
        using var installer = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "https://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(handler));
        var progress = new CapturedProgress();

        var result = await installer.FetchAndInstallAsync(progress);

        Assert.Equal(payload.Length, result.BytesInstalled);
        Assert.True(result.PreviousCacheRetained);
        Assert.Contains(
            progress.Updates,
            update => update.BytesPerSecond > 0 &&
                      update.BytesReceived == update.TotalBytes);
        Assert.Contains(
            progress.Updates,
            update => update.Text.Contains(
                "Preserving local calculations",
                StringComparison.Ordinal));
        Assert.Contains(
            progress.Updates,
            update => update.Text.Contains(
                "Running final checks",
                StringComparison.Ordinal));
        Assert.Contains(
            progress.Updates,
            update => update.Text.Contains(
                "Installing verified cache",
                StringComparison.Ordinal));
        Assert.Contains(
            progress.Updates,
            update => update.Text.Contains(
                "Opening the new cache",
                StringComparison.Ordinal));
        Assert.True(File.Exists(destinationPath + ".previous"));
        Assert.Single(
            await destination.QueryCandidatesAsync(new FarmFinderQuery()));
        Assert.Equal(
            7.123,
            await destination.GetAsync(
                500,
                "DT:{}",
                "osu-lazer/test"));
        using var connection = OpenReadOnly(destinationPath);
        using var metadata = connection.CreateCommand();
        metadata.CommandText =
            "SELECT value FROM farm_metadata WHERE key='cache_source_sha256'";
        Assert.Equal(result.Sha256, metadata.ExecuteScalar());
    }

    [Fact]
    public async Task InvalidDigest_LeavesExistingCacheUntouched()
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.sqlite3");
        var destinationPath = Path.Combine(root, "destination.sqlite3");
        await CreatePopulatedDatabaseAsync(sourcePath);
        var destination = new FarmFinderRepository(destinationPath);
        await destination.InitializeAsync();
        var payload = File.ReadAllBytes(sourcePath);
        var manifest = Manifest(payload, sha256: new string('0', 64));
        using var installer = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "https://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(new CacheHandler(manifest, payload)));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.FetchAndInstallAsync());

        using var connection = OpenReadOnly(destinationPath);
        using var version = connection.CreateCommand();
        version.CommandText =
            "SELECT value FROM farm_metadata WHERE key='schema_version'";
        Assert.Equal("5", version.ExecuteScalar());
        Assert.False(File.Exists(destinationPath + ".previous"));
    }

    [Fact]
    public async Task CorruptSqlitePayload_IsRejectedBeforeReplacement()
    {
        Directory.CreateDirectory(root);
        var destinationPath = Path.Combine(root, "destination.sqlite3");
        var destination = new FarmFinderRepository(destinationPath);
        await destination.InitializeAsync();
        var payload = Encoding.UTF8.GetBytes("not a sqlite database");
        using var installer = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "https://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(new CacheHandler(Manifest(payload), payload)));

        await Assert.ThrowsAnyAsync<Exception>(
            () => installer.FetchAndInstallAsync());

        using var connection = OpenReadOnly(destinationPath);
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        Assert.Equal("ok", check.ExecuteScalar());
    }

    [Fact]
    public async Task FutureSchemaOrAppRequirement_IsRejectedFromManifest()
    {
        Directory.CreateDirectory(root);
        var destinationPath = Path.Combine(root, "destination.sqlite3");
        var destination = new FarmFinderRepository(destinationPath);
        await destination.InitializeAsync();
        var payload = new byte[] { 1 };
        var futureSchema = Manifest(payload, schemaVersion: 6);
        using var schemaInstaller = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "https://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(new CacheHandler(futureSchema, payload)));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => schemaInstaller.FetchAndInstallAsync());

        var futureApp = Manifest(payload, minimumAppVersion: "2.0");
        using var appInstaller = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "https://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(new CacheHandler(futureApp, payload)));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => appInstaller.FetchAndInstallAsync());
        Assert.Contains("Update Kumori", exception.Message);
    }

    [Fact]
    public async Task MissingOrInsecureManifestUrl_IsNotConfigured()
    {
        Directory.CreateDirectory(root);
        var destinationPath = Path.Combine(root, "destination.sqlite3");
        var destination = new FarmFinderRepository(destinationPath);
        using var missing = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "",
            new Version(1, 0),
            new HttpClient(new CacheHandler("{}", [])));
        using var insecure = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "http://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(new CacheHandler("{}", [])));

        Assert.False(missing.IsConfigured);
        Assert.False(insecure.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => missing.FetchAndInstallAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => insecure.FetchAndInstallAsync());
    }

    [Fact]
    public async Task OlderServerCache_DoesNotDowngradeInstalledCache()
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.sqlite3");
        var destinationPath = Path.Combine(root, "destination.sqlite3");
        await CreatePopulatedDatabaseAsync(sourcePath);
        await CreatePopulatedDatabaseAsync(destinationPath);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = destinationPath,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ConnectionString))
        {
            connection.Open();
            using var metadata = connection.CreateCommand();
            metadata.CommandText = """
                INSERT INTO farm_metadata(key, value)
                VALUES('cache_generated_at', '2026-07-31T12:00:00Z')
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            metadata.ExecuteNonQuery();
        }

        var destination = new FarmFinderRepository(destinationPath);
        var payload = File.ReadAllBytes(sourcePath);
        using var installer = new FarmFinderCacheInstaller(
            destinationPath,
            destination,
            "https://cache.example.test/manifest.json",
            new Version(1, 0),
            new HttpClient(new CacheHandler(Manifest(payload), payload)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.FetchAndInstallAsync());

        Assert.Contains("older", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destinationPath + ".previous"));
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
        await repository.ReplacePlayerScoresAsync(new PlayerScoresPayload(
            player with { ScoresUpdatedAt = now },
            [new FarmScore(
                100, player.UserId, map.BeatmapId, 350, .98, 0, 500,
                true, now, [new FarmMod("HD")], "HD", 1)],
            [map]));
        await repository.MarkPlayerCompletedAsync(job.Id, player.UserId);
        await repository.CompleteJobAsync(job.Id, cancelled: false);
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        checkpoint.ExecuteNonQuery();
    }

    private static string Manifest(
        byte[] database,
        string? sha256 = null,
        int schemaVersion = 5,
        string? minimumAppVersion = null) =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            databaseUrl = "https://cache.example.test/farm.sqlite3",
            sha256 = sha256 ??
                     Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant(),
            sizeBytes = database.LongLength,
            schemaVersion,
            generatedAt = "2026-07-30T12:00:00Z",
            minimumAppVersion,
        });

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class CacheHandler(string manifest, byte[] database)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpContent content = request.RequestUri?.AbsolutePath.EndsWith(
                                      "manifest.json",
                                      StringComparison.Ordinal) == true
                ? new StringContent(manifest, Encoding.UTF8, "application/json")
                : new ByteArrayContent(database);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    private sealed class CapturedProgress : IProgress<FarmCacheDownloadProgress>
    {
        public List<FarmCacheDownloadProgress> Updates { get; } = [];

        public void Report(FarmCacheDownloadProgress value) =>
            Updates.Add(value);
    }
}
