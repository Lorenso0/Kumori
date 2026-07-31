using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed record FarmFinderCachePublishOptions(
    string SourceDatabasePath,
    string OutputRoot,
    Uri PublicBaseUri,
    string? MinimumAppVersion = null,
    bool RequireZeroFailures = false);

public sealed record FarmFinderCachePublishResult(
    string PackageDirectory,
    string DatabasePath,
    string ManifestPath,
    Uri DatabaseUri,
    string Sha256,
    long SizeBytes,
    DateTimeOffset GeneratedAt);

public sealed class FarmFinderCachePublisher
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<DateTimeOffset> utcNow;

    public FarmFinderCachePublisher(Func<DateTimeOffset>? utcNow = null)
    {
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public FarmFinderCachePublishResult Publish(
        FarmFinderCachePublishOptions options,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sourcePath = Path.GetFullPath(options.SourceDatabasePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                "The Farm Finder database does not exist. Build the full index first.",
                sourcePath);

        var baseUri = NormalizeBaseUri(options.PublicBaseUri);
        var minimumAppVersion = NormalizeVersion(options.MinimumAppVersion);
        var generatedAt = utcNow().ToUniversalTime();
        var stamp = generatedAt.ToString("yyyyMMddTHHmmssZ");
        var databaseName =
            $"farm-finder-v{FarmFinderRepository.CurrentSchemaVersion}-{stamp}.sqlite3";
        var outputRoot = Path.GetFullPath(options.OutputRoot);
        var packageDirectory = Path.Combine(outputRoot, stamp);
        var stagingPath = Path.Combine(packageDirectory, databaseName + ".building");
        var databasePath = Path.Combine(packageDirectory, databaseName);
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        var createdPackage = false;

        try
        {
            Directory.CreateDirectory(outputRoot);
            if (Directory.Exists(packageDirectory))
                throw new IOException(
                    $"A cache package already exists for timestamp {stamp}.");
            Directory.CreateDirectory(packageDirectory);
            createdPackage = true;

            progress?.Report("Creating a consistent SQLite snapshot…");
            CreateSnapshot(sourcePath, stagingPath);

            progress?.Report(
                $"Updating the upload copy to schema " +
                $"{FarmFinderRepository.CurrentSchemaVersion}…");
            var migrationRepository = new FarmFinderRepository(stagingPath);
            migrationRepository.InitializeAsync().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();

            progress?.Report("Selecting the newest completed index snapshot…");
            FindLatestCompletedIndex(
                stagingPath,
                options.RequireZeroFailures);
            progress?.Report("Finalizing unfinished job markers in the upload copy…");
            FinalizeUnfinishedJobs(stagingPath);
            FinalizeSnapshotJournal(stagingPath);
            progress?.Report("Running fast schema and integrity checks…");
            FarmFinderCacheInstaller.ValidateDatabase(
                stagingPath,
                FarmFinderRepository.CurrentSchemaVersion,
                exhaustiveIntegrityCheck: false);

            var sizeBytes = new FileInfo(stagingPath).Length;
            if (sizeBytes is <= 0 or > FarmFinderCacheInstaller.MaximumDatabaseBytes)
                throw new InvalidDataException(
                    "The generated database is empty or exceeds the 2 GB client limit.");

            progress?.Report("Calculating SHA-256…");
            string sha256;
            using (var stream = File.OpenRead(stagingPath))
                sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));

            File.Move(stagingPath, databasePath);
            var databaseUri = new Uri(baseUri, Uri.EscapeDataString(databaseName));
            var manifest = new
            {
                FormatVersion = 1,
                DatabaseUrl = databaseUri.AbsoluteUri,
                Sha256 = sha256,
                SizeBytes = sizeBytes,
                SchemaVersion = FarmFinderRepository.CurrentSchemaVersion,
                GeneratedAt = generatedAt,
                MinimumAppVersion = minimumAppVersion,
            };
            var manifestBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(manifest, jsonOptions));
            var manifestStaging = manifestPath + ".building";
            File.WriteAllBytes(manifestStaging, manifestBytes);
            File.Move(manifestStaging, manifestPath);

            progress?.Report("Cache package created and verified.");
            return new FarmFinderCachePublishResult(
                packageDirectory,
                databasePath,
                manifestPath,
                databaseUri,
                sha256,
                sizeBytes,
                generatedAt);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (createdPackage && Directory.Exists(packageDirectory))
                Directory.Delete(packageDirectory, recursive: true);
            throw;
        }
    }

    private static void CreateSnapshot(string sourcePath, string destinationPath)
    {
        SqliteConnection.ClearAllPools();
        using var source = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 15,
            }.ConnectionString);
        using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 15,
            }.ConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static long FindLatestCompletedIndex(
        string path,
        bool requireZeroFailures)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job.id,
                   job.players_failed,
                   (SELECT COUNT(*)
                    FROM farm_ranking_snapshot_members member
                    WHERE member.snapshot_id=job.id)
            FROM farm_index_jobs job
            JOIN farm_ranking_snapshots snapshot
              ON snapshot.snapshot_id=job.id
            WHERE job.status='completed'
              AND job.completed_at IS NOT NULL
              AND snapshot.completed_at IS NOT NULL
            ORDER BY job.id DESC
            LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException(
                "The database has no completed full-index job.");

        var id = reader.GetInt64(0);
        var failed = reader.GetInt32(1);
        var snapshotMembers = reader.GetInt32(2);
        if (snapshotMembers <= 0)
            throw new InvalidDataException(
                "The newest completed full-index snapshot contains no players.");
        if (requireZeroFailures && failed != 0)
            throw new InvalidDataException(
                $"The newest completed full-index job contains {failed:N0} failed players. " +
                "Resume it successfully before publishing.");
        return id;
    }

    private static void FinalizeUnfinishedJobs(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true,
            }.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE farm_index_jobs
            SET status='discarded',
                cursor_json=NULL,
                completed_at=COALESCE(completed_at, updated_at)
            WHERE status IN ('running', 'paused');

            UPDATE farm_ranking_snapshots
            SET completed_at=COALESCE(
                completed_at,
                (SELECT job.updated_at
                 FROM farm_index_jobs job
                 WHERE job.id=farm_ranking_snapshots.snapshot_id))
            WHERE snapshot_id IN (
                SELECT id
                FROM farm_index_jobs
                WHERE status='discarded'
            );
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void FinalizeSnapshotJournal(string path)
    {
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = path,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                       DefaultTimeout = 15,
                   }.ConnectionString))
        {
            connection.Open();
            using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }
            using var journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            journal.ExecuteScalar();
        }

        SqliteConnection.ClearAllPools();
        DeleteSidecar(path + "-wal");
        DeleteSidecar(path + "-shm");
    }

    private static void DeleteSidecar(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static Uri NormalizeBaseUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(
                "The public cache base URL must be an absolute HTTPS directory URL.",
                nameof(uri));
        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/",
        };
        return builder.Uri;
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(normalized, out var version))
            throw new ArgumentException(
                "The minimum app version must be numeric, for example 0.6.2.",
                nameof(value));
        return version.ToString();
    }
}
