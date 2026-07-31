using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumori.FarmFinder;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class FarmFinderCacheInstaller : IFarmFinderCacheInstaller, IDisposable
{
    internal const long MaximumDatabaseBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions jsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string databasePath;
    private readonly IFarmFinderRepository repository;
    private readonly Uri? manifestUri;
    private readonly Version currentAppVersion;
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;

    public FarmFinderCacheInstaller(
        string databasePath,
        IFarmFinderRepository repository,
        string? manifestUrl,
        Version? currentAppVersion = null,
        HttpClient? httpClient = null)
    {
        this.databasePath = Path.GetFullPath(databasePath);
        this.repository = repository;
        manifestUri = TryCreateHttpsUri(manifestUrl);
        this.currentAppVersion = currentAppVersion
                                 ?? Assembly.GetEntryAssembly()?.GetName().Version
                                 ?? new Version(0, 0);
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        ownsHttpClient = httpClient is null;
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Kumori-FarmFinder-Cache/1.0");
    }

    public bool IsConfigured => manifestUri is not null;

    public async Task<FarmCacheInstallResult> FetchAndInstallAsync(
        IProgress<FarmCacheDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (manifestUri is null)
            throw new InvalidOperationException(
                "The pre-built Farm Finder cache URL has not been configured yet.");

        progress?.Report(new FarmCacheDownloadProgress(
            0,
            1,
            "Checking cache availability…",
            Detail: "Reading the manifest and checking compatibility with this Kumori build."));
        var manifest = await FetchManifestAsync(manifestUri, cancellationToken)
            .ConfigureAwait(false);
        ValidateManifest(manifest, manifestUri);
        EnsureNotOlderThanInstalledCache(manifest.GeneratedAt);

        var directory = Path.GetDirectoryName(databasePath)
                        ?? throw new InvalidOperationException(
                            "The Farm Finder cache path has no parent directory.");
        Directory.CreateDirectory(directory);
        var stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(databasePath)}.download-{Guid.NewGuid():N}.tmp");
        var backupPath = databasePath + ".previous";
        var replacedExisting = false;
        var installed = false;

        try
        {
            var downloaded = await DownloadDatabaseAsync(
                    manifest,
                    stagingPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(databasePath))
            {
                ReportStage(
                    progress,
                    "Download complete · Preserving local calculations…",
                    "Copying locally calculated difficulty values into the downloaded cache.");
                await Task.Run(
                        () => MergeExistingStarRatings(stagingPath, databasePath),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ReportStage(
                progress,
                "Download complete · Running final checks…",
                "Checking the database structure, references, contents, and schema before installation.");
            WriteInstallMetadata(stagingPath, manifest, downloaded.Sha256);
            await Task.Run(
                    () => ValidateDatabase(
                        stagingPath,
                        manifest.SchemaVersion,
                        exhaustiveIntegrityCheck: false),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // The commit is deliberately non-cancellable. Once replacement
            // starts, Kumori either finishes it or restores the previous cache.
            ReportStage(
                progress,
                "Installing verified cache…",
                "Replacing the local database safely and retaining the previous copy for rollback.");
            replacedExisting = File.Exists(databasePath);
            CheckpointExistingDatabase();
            SqliteConnection.ClearAllPools();
            DeleteIfExists(databasePath + "-wal");
            DeleteIfExists(databasePath + "-shm");
            DeleteIfExists(backupPath);

            if (replacedExisting)
                File.Replace(stagingPath, databasePath, backupPath, true);
            else
                File.Move(stagingPath, databasePath);
            installed = true;

            ReportStage(
                progress,
                "Opening the new cache…",
                "Refreshing Farm Finder's database connection. Results will load next.");
            await repository.ReloadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            progress?.Report(new FarmCacheDownloadProgress(
                downloaded.Bytes,
                downloaded.Bytes,
                "Pre-built cache installed and verified.",
                Detail: "Starting your search with the new cache."));
            return new FarmCacheInstallResult(
                downloaded.Bytes,
                downloaded.Sha256,
                manifest.SchemaVersion,
                manifest.GeneratedAt,
                replacedExisting);
        }
        catch
        {
            if (installed)
            {
                SqliteConnection.ClearAllPools();
                DeleteIfExists(databasePath + "-wal");
                DeleteIfExists(databasePath + "-shm");
                if (replacedExisting && File.Exists(backupPath))
                    File.Move(backupPath, databasePath, true);
                else
                    DeleteIfExists(databasePath);

                try
                {
                    await repository.ReloadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original failure; the previous database is
                    // already back in place for the next app start.
                }
            }
            throw;
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    private static void ReportStage(
        IProgress<FarmCacheDownloadProgress>? progress,
        string text,
        string detail) =>
        progress?.Report(new FarmCacheDownloadProgress(
            1,
            1,
            text,
            Detail: detail));

    private async Task<CacheManifest> FetchManifestAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
                source,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri, "Cache manifest");
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
            throw new InvalidDataException("The cache manifest is unexpectedly large.");

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            if (memory.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("The cache manifest is unexpectedly large.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            return JsonSerializer.Deserialize<CacheManifest>(
                       memory.ToArray(),
                       jsonOptions)
                   ?? throw new InvalidDataException("The cache manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The cache manifest is not valid JSON.",
                exception);
        }
    }

    private void ValidateManifest(CacheManifest manifest, Uri source)
    {
        if (manifest.FormatVersion != 1)
            throw new InvalidDataException(
                $"Unsupported cache manifest format {manifest.FormatVersion}.");
        if (!Uri.TryCreate(manifest.DatabaseUrl, UriKind.Absolute, out var databaseUri))
            throw new InvalidDataException("The cache manifest has no valid database URL.");
        EnsureHttps(databaseUri, "Cache database");
        if (manifest.SizeBytes is <= 0 or > MaximumDatabaseBytes)
            throw new InvalidDataException("The cache manifest declares an invalid database size.");
        if (manifest.SchemaVersion != FarmFinderRepository.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Cache schema {manifest.SchemaVersion} is incompatible with this Kumori build " +
                $"(expected {FarmFinderRepository.CurrentSchemaVersion}).");
        if (manifest.GeneratedAt == default ||
            manifest.GeneratedAt > DateTimeOffset.UtcNow.AddHours(24))
            throw new InvalidDataException("The cache manifest has an invalid generation time.");
        if (!TryDecodeSha256(manifest.Sha256, out _))
            throw new InvalidDataException("The cache manifest has an invalid SHA-256 digest.");
        if (!string.IsNullOrWhiteSpace(manifest.MinimumAppVersion))
        {
            var normalized = manifest.MinimumAppVersion.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(normalized, out var required))
                throw new InvalidDataException(
                    "The cache manifest has an invalid minimum app version.");
            if (currentAppVersion < required)
                throw new InvalidDataException(
                    $"This cache requires Kumori {required} or newer. Update Kumori first.");
        }

        // Prevent a configured HTTPS endpoint from quietly redirecting the
        // manifest itself to an insecure transport.
        EnsureHttps(source, "Configured cache manifest");
    }

    private async Task<(long Bytes, string Sha256)> DownloadDatabaseAsync(
        CacheManifest manifest,
        string destination,
        IProgress<FarmCacheDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = new Uri(manifest.DatabaseUrl, UriKind.Absolute);
        using var response = await http.GetAsync(
                source,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri, "Cache database");
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != manifest.SizeBytes)
            throw new InvalidDataException(
                "The cache download size does not match its manifest.");

        await using var input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var timer = Stopwatch.StartNew();
        var sampleStartedAt = TimeSpan.Zero;
        var lastReportedAt = TimeSpan.Zero;
        long sampleStartedBytes = 0;
        double smoothedBytesPerSecond = 0;
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > manifest.SizeBytes || total > MaximumDatabaseBytes)
                throw new InvalidDataException(
                    "The cache download exceeded its declared size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            var elapsed = timer.Elapsed;
            var completed = total == manifest.SizeBytes;
            if (completed ||
                elapsed - lastReportedAt >= TimeSpan.FromMilliseconds(150))
            {
                var sampleSeconds = (elapsed - sampleStartedAt).TotalSeconds;
                if (sampleSeconds > 0)
                {
                    var currentRate =
                        (total - sampleStartedBytes) / sampleSeconds;
                    smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                        ? currentRate
                        : smoothedBytesPerSecond * 0.7 + currentRate * 0.3;
                }

                var remainingBytes = manifest.SizeBytes - total;
                TimeSpan? estimatedRemaining =
                    smoothedBytesPerSecond > 0 && remainingBytes > 0
                    ? TimeSpan.FromSeconds(
                        remainingBytes / smoothedBytesPerSecond)
                    : completed
                        ? TimeSpan.Zero
                        : null;
                progress?.Report(new FarmCacheDownloadProgress(
                    total,
                    manifest.SizeBytes,
                    "Downloading pre-built cache…",
                    smoothedBytesPerSecond,
                    estimatedRemaining));
                sampleStartedAt = elapsed;
                sampleStartedBytes = total;
                lastReportedAt = elapsed;
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);

        if (total != manifest.SizeBytes)
            throw new InvalidDataException(
                "The cache download ended before its declared size.");
        var actualDigest = hash.GetHashAndReset();
        TryDecodeSha256(manifest.Sha256, out var expectedDigest);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            throw new InvalidDataException(
                "The cache download failed its SHA-256 verification.");
        return (total, Convert.ToHexString(actualDigest).ToLowerInvariant());
    }

    internal static void ValidateDatabase(
        string path,
        int expectedSchema,
        bool exhaustiveIntegrityCheck = true)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                ForeignKeys = true,
            }.ConnectionString);
        connection.Open();

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = exhaustiveIntegrityCheck
                ? "PRAGMA integrity_check"
                : "PRAGMA quick_check";
            if (!string.Equals(
                    integrity.ExecuteScalar() as string,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The downloaded Farm Finder database failed its integrity check.");
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read())
                throw new InvalidDataException(
                    "The downloaded Farm Finder database has broken references.");
        }

        var requiredTables = new[]
        {
            "farm_metadata",
            "farm_players",
            "farm_beatmaps",
            "farm_scores",
            "farm_star_ratings",
            "farm_index_jobs",
            "farm_ranking_snapshots",
            "farm_ranking_snapshot_members",
        };
        foreach (var table in requiredTables)
        {
            using var exists = connection.CreateCommand();
            exists.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            exists.Parameters.AddWithValue("@name", table);
            if (Convert.ToInt32(exists.ExecuteScalar()) != 1)
                throw new InvalidDataException(
                    $"The downloaded cache is missing required table '{table}'.");
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText =
                "SELECT value FROM farm_metadata WHERE key='schema_version'";
            if (!int.TryParse(version.ExecuteScalar() as string, out var actual) ||
                actual != expectedSchema)
                throw new InvalidDataException(
                    "The downloaded cache schema does not match its manifest.");
        }

        foreach (var table in new[] { "farm_players", "farm_beatmaps", "farm_scores" })
        {
            using var populated = connection.CreateCommand();
            populated.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} LIMIT 1)";
            if (Convert.ToInt32(populated.ExecuteScalar()) != 1)
                throw new InvalidDataException(
                    $"The downloaded cache contains no rows in '{table}'.");
        }

        using var activeJobs = connection.CreateCommand();
        activeJobs.CommandText =
            "SELECT COUNT(*) FROM farm_index_jobs WHERE status IN ('running', 'paused')";
        if (Convert.ToInt32(activeJobs.ExecuteScalar()) != 0)
            throw new InvalidDataException(
                "The downloaded cache contains an unfinished server-side index job.");
    }

    private void CheckpointExistingDatabase()
    {
        if (!File.Exists(databasePath))
            return;
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        checkpoint.ExecuteNonQuery();
    }

    private void EnsureNotOlderThanInstalledCache(DateTimeOffset candidate)
    {
        if (!File.Exists(databasePath))
            return;

        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT value FROM farm_metadata WHERE key='cache_generated_at'";
            var value = command.ExecuteScalar() as string;
            if (DateTimeOffset.TryParse(value, out var installed) &&
                candidate < installed)
                throw new InvalidDataException(
                    "The server cache is older than the cache already installed.");
        }
        catch (SqliteException)
        {
            // A broken local database must not prevent Fetch cache from
            // repairing it. Full validation still runs before replacement.
        }
    }

    private static void MergeExistingStarRatings(
        string stagingPath,
        string existingPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;";
            journal.ExecuteNonQuery();
        }
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE @existing AS existing_cache";
            attach.Parameters.AddWithValue("@existing", existingPath);
            attach.ExecuteNonQuery();
        }
        try
        {
            using var hasTable = connection.CreateCommand();
            hasTable.CommandText = """
                SELECT COUNT(*)
                FROM existing_cache.sqlite_master
                WHERE type='table' AND name='farm_star_ratings'
                """;
            if (Convert.ToInt32(hasTable.ExecuteScalar()) != 1)
                return;

            using var transaction = connection.BeginTransaction();
            using var merge = connection.CreateCommand();
            merge.Transaction = transaction;
            merge.CommandText = """
                INSERT OR IGNORE INTO farm_star_ratings(
                    beatmap_id, mods_key, calculator_version, star_rating, updated_at)
                SELECT existing.beatmap_id,
                       existing.mods_key,
                       existing.calculator_version,
                       existing.star_rating,
                       existing.updated_at
                FROM existing_cache.farm_star_ratings existing
                JOIN farm_beatmaps target
                  ON target.beatmap_id=existing.beatmap_id
                """;
            merge.ExecuteNonQuery();
            transaction.Commit();
        }
        finally
        {
            using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE existing_cache";
            detach.ExecuteNonQuery();
        }
    }

    private static void WriteInstallMetadata(
        string path,
        CacheManifest manifest,
        string sha256)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var pair in new Dictionary<string, string>
        {
            ["cache_manifest_format"] = manifest.FormatVersion.ToString(),
            ["cache_source_sha256"] = sha256,
            ["cache_generated_at"] =
                         manifest.GeneratedAt.ToUniversalTime().ToString("O"),
            ["cache_installed_at"] =
                         DateTimeOffset.UtcNow.ToUniversalTime().ToString("O"),
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO farm_metadata(key, value) VALUES(@key, @value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            command.Parameters.AddWithValue("@key", pair.Key);
            command.Parameters.AddWithValue("@value", pair.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static Uri? TryCreateHttpsUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static void EnsureHttps(Uri? uri, string name)
    {
        if (uri is null ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name} must use HTTPS.");
    }

    private static bool TryDecodeSha256(string? value, out byte[] digest)
    {
        digest = [];
        if (value is null || value.Length != 64)
            return false;
        try
        {
            digest = Convert.FromHexString(value);
            return digest.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void Dispose()
    {
        if (ownsHttpClient)
            http.Dispose();
    }

    private sealed record CacheManifest
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; init; }

        [JsonPropertyName("databaseUrl")]
        public string DatabaseUrl { get; init; } = "";

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = "";

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("generatedAt")]
        public DateTimeOffset GeneratedAt { get; init; }

        [JsonPropertyName("minimumAppVersion")]
        public string? MinimumAppVersion { get; init; }
    }
}
