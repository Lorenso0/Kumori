using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class PlaySharePackageService
{
    public const string FormatName = "kumori-shared-play";
    public const int CurrentFormatVersion = 2;
    public const int LegacyFormatVersion = 1;
    public const string FullPortableProfile = "full_portable";
    public const string CompactDiscordProfile = "compact_discord";
    public const string FileExtension = ".kumori";

    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxPlayBytes = 16L * 1024 * 1024;
    private const long MaxAssetBytes = 100L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long CompressionRatioCheckThreshold = 1024 * 1024;
    private const double MaxCompressionRatio = 200;
    private const int MaxEntries = 4096;
    private const int MovementChunkSamples = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 64,
    };

    private readonly AttemptDetailsRepository details;
    private readonly MovementRepository movement;
    private readonly SessionRepository sessions;
    private readonly string importsDatabase;
    private readonly string assetsDirectory;
    private readonly string stagingDirectory;
    private readonly Func<SharedPlayV1, IReadOnlyList<string>>? localAssetCandidates;
    private readonly Func<SharedPlayV1, CancellationToken, Task<IReadOnlyList<ShareMediaFile>>>? compactMediaResolver;
    private readonly object schemaGate = new();
    private bool schemaReady;

    public PlaySharePackageService(
        AttemptDetailsRepository details,
        MovementRepository movement,
        SessionRepository sessions,
        string? importsDatabase = null,
        string? assetsDirectory = null,
        string? stagingDirectory = null,
        Func<SharedPlayV1, IReadOnlyList<string>>? localAssetCandidates = null,
        Func<SharedPlayV1, CancellationToken, Task<IReadOnlyList<ShareMediaFile>>>? compactMediaResolver = null)
    {
        this.details = details;
        this.movement = movement;
        this.sessions = sessions;
        this.importsDatabase = importsDatabase ?? AppPaths.ImportsDatabase;
        this.assetsDirectory = assetsDirectory ?? AppPaths.ImportedAssetsDir;
        this.stagingDirectory = stagingDirectory ?? AppPaths.ImportStagingDir;
        this.localAssetCandidates = localAssetCandidates;
        this.compactMediaResolver = compactMediaResolver;
    }

    public string? GetPlayerName(long attemptId) => sessions.GetPlayerNameForAttempt(attemptId);

    public void RememberPlayerName(long attemptId, string playerName) =>
        sessions.SetPlayerNameForAttempt(attemptId, playerName);

    public async Task<string> ExportAsync(
        long attemptId,
        string playerName,
        string destination,
        IReadOnlyList<ShareMediaFile> mediaFiles,
        IReadOnlyList<string>? optionalMediaOmissions = null,
        KumoriPackageProfile profile = KumoriPackageProfile.FullPortable,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new InvalidOperationException("A player name is required before this play can be shared.");
        if (!destination.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            destination += FileExtension;

        AttemptDetails playDetails = details.GetDetails(attemptId)
            ?? throw new InvalidOperationException($"Attempt {attemptId} was not found.");
        MovementMetadata metadata = movement.GetMetadata(attemptId)
            ?? throw new InvalidOperationException("This play does not contain a captured replay.");
        IReadOnlyList<MovementSample> samples = movement.GetSamples(attemptId, cancellationToken);
        if (samples.Count == 0)
            throw new InvalidOperationException("This play does not contain replay movement samples.");
        if (samples.Count != metadata.SampleCount)
            throw new InvalidDataException("The captured replay sample count is inconsistent.");

        var normalizedMedia = NormalizeExportMedia(mediaFiles);
        if (profile == KumoriPackageProfile.CompactDiscord)
        {
            normalizedMedia = normalizedMedia
                .Where(file => file.Role is "beatmap" or "audio")
                .ToList();
        }
        if (!normalizedMedia.Any(file => file.Role == "beatmap"))
            throw new InvalidOperationException("The .osu beatmap file is required for a portable share.");
        if (!normalizedMedia.Any(file => file.Role == "audio"))
            throw new InvalidOperationException("The beatmap audio is required for a portable share.");

        SharedPlayV1 play = SharedPlayV1.From(playDetails, metadata);
        byte[] playBytes = JsonSerializer.SerializeToUtf8Bytes(play, JsonOptions);
        string playHash = Hash(playBytes);

        var encodedMovement = new List<(KumoriPackageMovementEntryV1 Descriptor, byte[] Payload)>();
        for (int offset = 0, position = 0; offset < samples.Count; offset += MovementChunkSamples, position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MovementSample[] chunk = samples.Skip(offset).Take(MovementChunkSamples).ToArray();
            byte[] payload = MovementRepository.EncodeSamples(chunk);
            encodedMovement.Add((new KumoriPackageMovementEntryV1
            {
                Entry = $"movement/{position:D4}.bin",
                SampleCount = chunk.Length,
                Size = payload.LongLength,
                Sha256 = Hash(payload),
            }, payload));
        }

        var assetDescriptors = new List<KumoriPackageAssetV1>(normalizedMedia.Count);
        foreach (ShareMediaFile file in normalizedMedia)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.Path);
            if (!info.Exists)
                throw new FileNotFoundException($"Shared media file '{file.LogicalName}' was not found.", file.Path);
            if (info.Length is <= 0 or > MaxAssetBytes)
                throw new InvalidDataException($"Shared media file '{file.LogicalName}' has an unsupported size.");
            assetDescriptors.Add(new KumoriPackageAssetV1
            {
                Entry = $"media/{assetDescriptors.Count:D4}-{SafeLogicalName(file.LogicalName)}",
                LogicalName = SafeLogicalName(file.LogicalName),
                Role = file.Role,
                Size = info.Length,
                Sha256 = await HashFileAsync(file.Path, cancellationToken),
            });
        }

        string fingerprint = ComputeFingerprint(
            playHash,
            encodedMovement.Select(item => item.Descriptor),
            assetDescriptors);
        var manifest = new KumoriPackageManifestV1
        {
            ExportedAt = DateTimeOffset.UtcNow,
            AppVersion = typeof(PlaySharePackageService).Assembly.GetName().Version?.ToString() ?? "",
            PlayerName = playerName.Trim(),
            Fingerprint = fingerprint,
            PlaySha256 = playHash,
            Movement = encodedMovement.Select(item => item.Descriptor).ToArray(),
            Assets = assetDescriptors,
            OptionalMediaOmissions = optionalMediaOmissions?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray() ?? [],
            MediaProfile = profile == KumoriPackageProfile.CompactDiscord
                ? CompactDiscordProfile
                : FullPortableProfile,
        };

        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        string pending = Path.Combine(
            Path.GetDirectoryName(fullDestination)!,
            $".{Path.GetFileNameWithoutExtension(fullDestination)}.new-{Guid.NewGuid():N}{FileExtension}");
        try
        {
            await using (var output = new FileStream(
                             pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteEntryAsync(archive, "play.json", playBytes, cancellationToken);
                foreach (var item in encodedMovement)
                    await WriteEntryAsync(archive, item.Descriptor.Entry, item.Payload, cancellationToken);
                for (int index = 0; index < normalizedMedia.Count; index++)
                    await WriteFileEntryAsync(archive, assetDescriptors[index].Entry, normalizedMedia[index].Path, cancellationToken);
                await WriteEntryAsync(
                    archive,
                    "manifest.json",
                    JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = await PreviewAsync(pending, cancellationToken);
            File.Move(pending, fullDestination, overwrite: true);
            return fullDestination;
        }
        finally
        {
            TryDeleteFile(pending);
        }
    }

    public async Task<KumoriPackagePreview> PreviewAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ValidatedPackage package = await ValidatePackageAsync(packagePath, decodeMovement: true, cancellationToken);
        return new KumoriPackagePreview(
            Path.GetFullPath(packagePath),
            package.Manifest.Fingerprint,
            package.Manifest.PlayerName,
            package.Play,
            package.Manifest.ExportedAt,
            new FileInfo(packagePath).Length,
            package.Manifest.OptionalMediaOmissions,
            package.Manifest.MediaProfile);
    }

    public async Task<KumoriImportResult> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ValidatedPackage package = await ValidatePackageAsync(packagePath, decodeMovement: true, cancellationToken);
        var importAssets = package.Manifest.Assets.ToList();
        var externalAssetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (IsCompact(package.Manifest)
            && !package.Manifest.Assets.Any(asset => asset.Role == "audio"))
        {
            IReadOnlyList<ShareMediaFile> resolved = await ResolveCompactMediaAsync(
                packagePath,
                package,
                cancellationToken);
            foreach (ShareMediaFile file in resolved.Where(file => file.Role != "beatmap"))
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists || info.Length is <= 0 or > MaxAssetBytes)
                    throw new InvalidDataException($"Resolved media '{file.LogicalName}' has an unsupported size.");
                string hash = await HashFileAsync(info.FullName, cancellationToken);
                importAssets.Add(new KumoriPackageAssetV1
                {
                    Entry = "",
                    LogicalName = file.LogicalName,
                    Role = file.Role,
                    Size = info.Length,
                    Sha256 = hash,
                });
                externalAssetPaths[hash] = info.FullName;
            }
        }
        EnsureImportSchema();
        using (SqliteConnection connection = OpenImports(readOnly: false))
        {
            long? existing = FindImportId(connection, package.Manifest.Fingerprint);
            if (existing is { } existingId)
            {
                AttemptDetails existingDetails = GetImportedDetails(existingId)
                    ?? throw new InvalidDataException("The existing imported play could not be loaded.");
                return new KumoriImportResult(existingId, true, existingDetails);
            }
        }

        string stage = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var stagedAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newlyCreatedTargets = new List<string>();
        int reusedLocalAssetCount = 0;
        long reusedLocalAssetBytes = 0;
        try
        {
            IGrouping<string, KumoriPackageAssetV1>[] assetGroups = importAssets
                .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<string, string> localMatches =
                await FindMatchingLocalAssetsAsync(package.Play, assetGroups, cancellationToken);

            Directory.CreateDirectory(assetsDirectory);
            var assetTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assetManaged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var requiresExtraction = new List<IGrouping<string, KumoriPackageAssetV1>>();
            foreach (IGrouping<string, KumoriPackageAssetV1> assetGroup in assetGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                KumoriPackageAssetV1 asset = assetGroup.First();
                string extension = Path.GetExtension(asset.LogicalName);
                string target = Path.Combine(assetsDirectory, asset.Sha256 + extension.ToLowerInvariant());
                if (externalAssetPaths.TryGetValue(asset.Sha256, out string? externalPath))
                {
                    target = externalPath;
                    assetManaged[asset.Sha256] = false;
                    reusedLocalAssetCount++;
                    reusedLocalAssetBytes = checked(reusedLocalAssetBytes + asset.Size);
                }
                else if (File.Exists(target))
                {
                    if (!string.Equals(
                            await HashFileAsync(target, cancellationToken),
                            asset.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"The imported asset store contains a damaged file for '{asset.LogicalName}'.");
                    }
                }
                else if (localMatches.TryGetValue(asset.Sha256, out string? localPath)
                         && File.Exists(localPath))
                {
                    target = localPath;
                    assetManaged[asset.Sha256] = false;
                    reusedLocalAssetCount++;
                    reusedLocalAssetBytes = checked(reusedLocalAssetBytes + asset.Size);
                }
                else
                {
                    requiresExtraction.Add(assetGroup);
                }
                assetManaged.TryAdd(asset.Sha256, true);
                assetTargets[asset.Sha256] = target;
            }

            if (requiresExtraction.Count > 0)
            {
                using ZipArchive archive = ZipFile.OpenRead(packagePath);
                foreach (IGrouping<string, KumoriPackageAssetV1> assetGroup in requiresExtraction)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    KumoriPackageAssetV1 asset = assetGroup.First();
                    ZipArchiveEntry entry = archive.GetEntry(asset.Entry)
                        ?? throw new InvalidDataException($"Package asset '{asset.Entry}' is missing.");
                    string staged = Path.Combine(stage, asset.Sha256);
                    await ExtractEntryAsync(entry, staged, asset.Size, cancellationToken);
                    if (!string.Equals(await HashFileAsync(staged, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Package asset '{asset.LogicalName}' failed its integrity check.");
                    stagedAssets[asset.Sha256] = staged;
                }
            }

            foreach (IGrouping<string, KumoriPackageAssetV1> assetGroup in requiresExtraction)
            {
                KumoriPackageAssetV1 asset = assetGroup.First();
                string target = assetTargets[asset.Sha256];
                if (!File.Exists(target))
                {
                    if (!TryCreateHardLink(target, stagedAssets[asset.Sha256]))
                    {
                        File.Copy(stagedAssets[asset.Sha256], target);
                    }
                    newlyCreatedTargets.Add(target);
                }
            }

            long importId;
            string importedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using (SqliteConnection connection = OpenImports(readOnly: false))
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                long? racedExisting = FindImportId(connection, package.Manifest.Fingerprint, transaction);
                if (racedExisting is { } racedId)
                {
                    transaction.Rollback();
                    AttemptDetails racedDetails = GetImportedDetails(racedId)
                        ?? throw new InvalidDataException("The existing imported play could not be loaded.");
                    foreach (string target in newlyCreatedTargets)
                        TryDeleteFileIfUnreferenced(target);
                    return new KumoriImportResult(racedId, true, racedDetails);
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO imported_plays(
                            fingerprint, player_name, exported_at, imported_at, app_version,
                            play_json, package_size, optional_omissions_json)
                        VALUES(@fingerprint, @player, @exported, @imported, @app_version,
                               @play, @package_size, @omissions);
                        SELECT last_insert_rowid();
                        """;
                    insert.Parameters.AddWithValue("@fingerprint", package.Manifest.Fingerprint);
                    insert.Parameters.AddWithValue("@player", package.Manifest.PlayerName);
                    insert.Parameters.AddWithValue("@exported", package.Manifest.ExportedAt.ToString("O", CultureInfo.InvariantCulture));
                    insert.Parameters.AddWithValue("@imported", importedAt);
                    insert.Parameters.AddWithValue("@app_version", package.Manifest.AppVersion);
                    insert.Parameters.AddWithValue("@play", Encoding.UTF8.GetString(package.PlayBytes));
                    insert.Parameters.AddWithValue("@package_size", new FileInfo(packagePath).Length);
                    insert.Parameters.AddWithValue("@omissions", JsonSerializer.Serialize(package.Manifest.OptionalMediaOmissions, JsonOptions));
                    importId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                foreach (IGrouping<string, KumoriPackageAssetV1> assetGroup in
                         importAssets.GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase))
                {
                    KumoriPackageAssetV1 asset = assetGroup.First();
                    using var upsert = connection.CreateCommand();
                    upsert.Transaction = transaction;
                    upsert.CommandText = """
                        INSERT INTO imported_assets(hash, local_path, size, reference_count, is_managed)
                        VALUES(@hash, @path, @size, 1, @is_managed)
                        ON CONFLICT(hash) DO UPDATE SET
                            reference_count = imported_assets.reference_count + 1;
                        """;
                    upsert.Parameters.AddWithValue("@hash", asset.Sha256);
                    upsert.Parameters.AddWithValue("@path", assetTargets[asset.Sha256]);
                    upsert.Parameters.AddWithValue("@size", asset.Size);
                    upsert.Parameters.AddWithValue("@is_managed", assetManaged[asset.Sha256] ? 1 : 0);
                    upsert.ExecuteNonQuery();
                }
                foreach (KumoriPackageAssetV1 asset in importAssets)
                {
                    using var insertAsset = connection.CreateCommand();
                    insertAsset.Transaction = transaction;
                    insertAsset.CommandText = """
                        INSERT INTO imported_play_assets(import_id, logical_name, role, asset_hash)
                        VALUES(@import_id, @logical_name, @role, @hash);
                        """;
                    insertAsset.Parameters.AddWithValue("@import_id", importId);
                    insertAsset.Parameters.AddWithValue("@logical_name", asset.LogicalName);
                    insertAsset.Parameters.AddWithValue("@role", asset.Role);
                    insertAsset.Parameters.AddWithValue("@hash", asset.Sha256);
                    insertAsset.ExecuteNonQuery();
                }

                using (ZipArchive archive = ZipFile.OpenRead(packagePath))
                {
                    for (int position = 0; position < package.Manifest.Movement.Count; position++)
                    {
                        KumoriPackageMovementEntryV1 descriptor = package.Manifest.Movement[position];
                        ZipArchiveEntry entry = archive.GetEntry(descriptor.Entry)
                            ?? throw new InvalidDataException($"Movement entry '{descriptor.Entry}' is missing.");
                        byte[] payload = await ReadEntryAsync(entry, MovementRepository.MaxCompressedChunkBytes, cancellationToken);
                        using var insertChunk = connection.CreateCommand();
                        insertChunk.Transaction = transaction;
                        insertChunk.CommandText = """
                            INSERT INTO imported_movement_chunks(import_id, position, sample_count, payload_zlib)
                            VALUES(@import_id, @position, @sample_count, @payload);
                            """;
                        insertChunk.Parameters.AddWithValue("@import_id", importId);
                        insertChunk.Parameters.AddWithValue("@position", position);
                        insertChunk.Parameters.AddWithValue("@sample_count", descriptor.SampleCount);
                        insertChunk.Parameters.AddWithValue("@payload", payload);
                        insertChunk.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }

            AttemptDetails importedDetails = GetImportedDetails(importId)
                ?? throw new InvalidDataException("The imported play could not be loaded after it was saved.");
            return new KumoriImportResult(
                importId,
                false,
                importedDetails,
                reusedLocalAssetCount,
                reusedLocalAssetBytes);
        }
        catch
        {
            foreach (string target in newlyCreatedTargets)
                TryDeleteFileIfUnreferenced(target);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stage);
        }
    }

    public IReadOnlyList<AttemptSummary> GetImportedAttempts(string? search = null)
    {
        EnsureImportSchema();
        using SqliteConnection connection = OpenImports(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, player_name, imported_at, play_json
            FROM imported_plays
            WHERE @search IS NULL
               OR player_name LIKE @search ESCAPE '\'
               OR play_json LIKE @search ESCAPE '\'
            ORDER BY id DESC
            """;
        command.Parameters.AddWithValue("@search",
            string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{EscapeLike(search.Trim())}%");
        var storedRows = new List<(long Id, string Player, string ImportedAt, string PlayJson)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                storedRows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }
        var result = new List<AttemptSummary>();
        foreach (var stored in storedRows)
        {
            SharedPlayV1 play = DeserializePlay(stored.PlayJson);
            AssetLookup assets = LoadAssets(connection, stored.Id);
            result.Add(play.ToAttemptDetails(
                stored.Id,
                stored.Player,
                stored.ImportedAt,
                assets.BeatmapPath,
                assets.BackgroundPath,
                assets.MediaPaths).Summary);
        }
        return result;
    }

    public AttemptDetails? GetImportedDetails(long importId)
    {
        EnsureImportSchema();
        using SqliteConnection connection = OpenImports(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT player_name, imported_at, play_json
            FROM imported_plays WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", importId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        string player = reader.GetString(0);
        string importedAt = reader.GetString(1);
        SharedPlayV1 play = DeserializePlay(reader.GetString(2));
        reader.Close();
        AssetLookup assets = LoadAssets(connection, importId);
        return play.ToAttemptDetails(
            importId, player, importedAt, assets.BeatmapPath, assets.BackgroundPath, assets.MediaPaths);
    }

    public IReadOnlyList<MovementSample> GetImportedMovement(long importId)
    {
        EnsureImportSchema();
        using SqliteConnection connection = OpenImports(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sample_count, payload_zlib
            FROM imported_movement_chunks
            WHERE import_id = @id ORDER BY position
            """;
        command.Parameters.AddWithValue("@id", importId);
        using var reader = command.ExecuteReader();
        var samples = new List<MovementSample>();
        while (reader.Read())
        {
            int count = checked((int)reader.GetInt64(0));
            samples.AddRange(MovementRepository.DecodeSamples((byte[])reader.GetValue(1), count));
            if (samples.Count > MovementRepository.MaxSamplesPerAttempt)
                throw new InvalidDataException("Imported replay exceeds the movement sample limit.");
        }
        samples.Sort((left, right) =>
        {
            int mapTime = left.MapTimeMs.CompareTo(right.MapTimeMs);
            return mapTime != 0 ? mapTime : left.MonotonicMs.CompareTo(right.MonotonicMs);
        });
        return samples;
    }

    public bool DeleteImport(long importId)
    {
        EnsureImportSchema();
        var deleteCandidates = new List<string>();
        using (SqliteConnection connection = OpenImports(readOnly: false))
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT DISTINCT a.hash, a.local_path, a.reference_count, a.is_managed
                    FROM imported_assets a
                    JOIN imported_play_assets p ON p.asset_hash = a.hash
                    WHERE p.import_id = @id
                    """;
                select.Parameters.AddWithValue("@id", importId);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt64(2) <= 1 && reader.GetInt64(3) != 0)
                        deleteCandidates.Add(reader.GetString(1));
                }
            }

            using (var decrement = connection.CreateCommand())
            {
                decrement.Transaction = transaction;
                decrement.CommandText = """
                    UPDATE imported_assets
                    SET reference_count = reference_count - 1
                    WHERE hash IN (
                        SELECT asset_hash FROM imported_play_assets WHERE import_id = @id
                    )
                    AND reference_count > 1;
                    """;
                decrement.Parameters.AddWithValue("@id", importId);
                decrement.ExecuteNonQuery();
            }
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM imported_plays WHERE id = @id";
                delete.Parameters.AddWithValue("@id", importId);
                if (delete.ExecuteNonQuery() == 0)
                    return false;
            }
            using (var removeAssets = connection.CreateCommand())
            {
                removeAssets.Transaction = transaction;
                removeAssets.CommandText = """
                    DELETE FROM imported_assets
                    WHERE hash NOT IN (SELECT DISTINCT asset_hash FROM imported_play_assets)
                    """;
                removeAssets.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        foreach (string path in deleteCandidates)
            TryDeleteFile(path);
        return true;
    }

    private async Task<ValidatedPackage> ValidatePackageAsync(
        string packagePath,
        bool decodeMovement,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packagePath)
            || !packagePath.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a .kumori shared play file.");
        string fullPath = Path.GetFullPath(packagePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException("The .kumori file was not found.", fullPath);
        if (file.Length is <= 0 or > MaxPackageBytes)
            throw new InvalidDataException("The .kumori file has an unsupported size.");

        using ZipArchive archive = ZipFile.OpenRead(fullPath);
        if (archive.Entries.Count is < 4 or > MaxEntries)
            throw new InvalidDataException("The .kumori file contains an invalid number of entries.");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = NormalizeEntryName(entry.FullName);
            if (string.IsNullOrWhiteSpace(entry.Name) || !IsSafeEntryName(name) || !entries.TryAdd(name, entry))
                throw new InvalidDataException($"Package entry '{entry.FullName}' is not valid.");
            if (entry.Length >= CompressionRatioCheckThreshold
                && entry.Length / (double)Math.Max(1, entry.CompressedLength) > MaxCompressionRatio)
                throw new InvalidDataException($"Package entry '{entry.FullName}' has an unsafe compression ratio.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
                throw new InvalidDataException("The expanded package exceeds the size limit.");
        }

        if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry? manifestEntry)
            || !entries.TryGetValue("play.json", out ZipArchiveEntry? playEntry))
            throw new InvalidDataException("The .kumori file is missing its manifest or play data.");
        byte[] manifestBytes = await ReadEntryAsync(manifestEntry, MaxManifestBytes, cancellationToken);
        KumoriPackageManifestV1 manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<KumoriPackageManifestV1>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("The package manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The package manifest is not valid JSON.", ex);
        }
        ValidateManifest(manifest);

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "play.json" };
        foreach (KumoriPackageMovementEntryV1 item in manifest.Movement)
            if (!expected.Add(item.Entry))
                throw new InvalidDataException($"Package entry '{item.Entry}' is declared more than once.");
        foreach (KumoriPackageAssetV1 item in manifest.Assets)
            if (!expected.Add(item.Entry))
                throw new InvalidDataException($"Package entry '{item.Entry}' is declared more than once.");
        if (expected.Count != entries.Count || expected.Any(name => !entries.ContainsKey(name)))
            throw new InvalidDataException("The package contains missing or unexpected files.");

        byte[] playBytes = await ReadEntryAsync(playEntry, MaxPlayBytes, cancellationToken);
        if (!FixedHashEquals(Hash(playBytes), manifest.PlaySha256))
            throw new InvalidDataException("The play data failed its integrity check.");
        SharedPlayV1 play;
        try
        {
            play = JsonSerializer.Deserialize<SharedPlayV1>(playBytes, JsonOptions)
                ?? throw new InvalidDataException("The package play data is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The package play data is not valid JSON.", ex);
        }
        ValidatePlay(play);

        int sampleCount = 0;
        foreach (KumoriPackageMovementEntryV1 descriptor in manifest.Movement)
        {
            ValidateMovementDescriptor(descriptor);
            ZipArchiveEntry entry = entries[descriptor.Entry];
            if (entry.Length != descriptor.Size)
                throw new InvalidDataException($"Movement entry '{descriptor.Entry}' has an invalid size.");
            byte[] payload = await ReadEntryAsync(entry, MovementRepository.MaxCompressedChunkBytes, cancellationToken);
            if (!FixedHashEquals(Hash(payload), descriptor.Sha256))
                throw new InvalidDataException($"Movement entry '{descriptor.Entry}' failed its integrity check.");
            if (decodeMovement)
                _ = MovementRepository.DecodeSamples(payload, descriptor.SampleCount);
            sampleCount = checked(sampleCount + descriptor.SampleCount);
            if (sampleCount > MovementRepository.MaxSamplesPerAttempt)
                throw new InvalidDataException("The package replay exceeds the movement sample limit.");
        }
        if (sampleCount != play.Movement.SampleCount || sampleCount == 0)
            throw new InvalidDataException("The package replay sample count is inconsistent.");

        foreach (KumoriPackageAssetV1 asset in manifest.Assets)
        {
            ValidateAssetDescriptor(asset);
            ZipArchiveEntry entry = entries[asset.Entry];
            if (entry.Length != asset.Size)
                throw new InvalidDataException($"Asset '{asset.LogicalName}' has an invalid size.");
            using Stream stream = entry.Open();
            string actualHash = await HashStreamAsync(stream, cancellationToken);
            if (!FixedHashEquals(actualHash, asset.Sha256))
                throw new InvalidDataException($"Asset '{asset.LogicalName}' failed its integrity check.");
        }

        var compact = IsCompact(manifest);
        int audioCount = manifest.Assets.Count(asset => asset.Role == "audio");
        if (manifest.Assets.Count(asset => asset.Role == "beatmap") != 1
            || (!compact && audioCount != 1)
            || (compact && (audioCount > 1
                            || manifest.Assets.Any(asset => asset.Role is not ("beatmap" or "audio")))))
        {
            throw new InvalidDataException(compact
                ? "A compact package must contain one beatmap and at most one audio file."
                : "The package must contain exactly one beatmap and one audio file.");
        }
        KumoriPackageAssetV1 beatmap = manifest.Assets.Single(asset => asset.Role == "beatmap");
        byte[] beatmapBytes = await ReadEntryAsync(entries[beatmap.Entry], MaxAssetBytes, cancellationToken);
        string referencedAudio = ReadAudioFilename(beatmapBytes);
        if (string.IsNullOrWhiteSpace(referencedAudio))
            throw new InvalidDataException("The packaged .osu file does not declare an audio file.");
        if ((!compact || audioCount == 1)
            && !string.Equals(
                referencedAudio,
                manifest.Assets.Single(asset => asset.Role == "audio").LogicalName,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The packaged .osu file does not reference the packaged audio.");

        string fingerprint = ComputeFingerprint(manifest.PlaySha256, manifest.Movement, manifest.Assets);
        if (!FixedHashEquals(fingerprint, manifest.Fingerprint))
            throw new InvalidDataException("The package fingerprint is not valid.");
        return new ValidatedPackage(manifest, play, playBytes);
    }

    private static void ValidateManifest(KumoriPackageManifestV1 manifest)
    {
        if (manifest.Movement is null
            || manifest.Assets is null
            || manifest.OptionalMediaOmissions is null
            || manifest.Movement.Any(item => item is null)
            || manifest.Assets.Any(item => item is null))
            throw new InvalidDataException("The package manifest is incomplete.");
        if (!string.Equals(manifest.Format, FormatName, StringComparison.Ordinal)
            || manifest.Version is < LegacyFormatVersion or > CurrentFormatVersion)
            throw new InvalidDataException("This .kumori format version is not supported.");
        if (manifest.Version == LegacyFormatVersion
            && !string.Equals(manifest.MediaProfile, FullPortableProfile, StringComparison.Ordinal))
            throw new InvalidDataException("Legacy .kumori packages must be fully portable.");
        if (manifest.Version >= 2
            && manifest.MediaProfile is not FullPortableProfile and not CompactDiscordProfile)
            throw new InvalidDataException("The package media profile is not supported.");
        if (manifest.ExportedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("The package export time is invalid.");
        if (string.IsNullOrWhiteSpace(manifest.PlayerName) || manifest.PlayerName.Length > 80)
            throw new InvalidDataException("The package player name is invalid.");
        ValidateHash(manifest.PlaySha256, "play data");
        ValidateHash(manifest.Fingerprint, "package fingerprint");
        if (manifest.Movement.Count is < 1 or > MovementRepository.MaxChunksPerAttempt)
            throw new InvalidDataException("The package movement entry count is invalid.");
        var minimumAssets = IsCompact(manifest) ? 1 : 2;
        if (manifest.Assets.Count < minimumAssets || manifest.Assets.Count > MaxEntries - 3)
            throw new InvalidDataException("The package asset count is invalid.");
    }

    private static bool IsCompact(KumoriPackageManifestV1 manifest) =>
        manifest.Version >= 2
        && string.Equals(manifest.MediaProfile, CompactDiscordProfile, StringComparison.Ordinal);

    private static void ValidatePlay(SharedPlayV1 play)
    {
        if (play.Map is null
            || play.Results is null
            || play.Movement is null
            || play.Mods is null
            || play.Events is null
            || play.CapturedDifficulty is null
            || play.Mods.Any(mod => mod is null)
            || play.Events.Any(item => item is null)
            || play.CapturedDifficulty.Any(pair => pair.Value is null)
            || (play.Timing is not null && play.Timing.Offsets is null)
            || string.IsNullOrWhiteSpace(play.StartedAt)
            || !DateTimeOffset.TryParse(play.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            || string.IsNullOrWhiteSpace(play.Map.Title)
            || string.IsNullOrWhiteSpace(play.Map.Difficulty)
            || play.Map.Artist is null
            || play.Map.Mapper is null
            || play.Map.Title.Length > 512
            || play.Map.Artist.Length > 512
            || play.Map.Difficulty.Length > 512
            || play.Map.Mapper.Length > 512
            || play.Accuracy is < 0 or > 100
            || play.Score < 0
            || play.Combo < 0
            || play.Misses < 0
            || play.Progress is < 0 or > 1
            || !double.IsFinite(play.Accuracy)
            || !double.IsFinite(play.Pp)
            || play.Pp < 0
            || !double.IsFinite(play.Results.DurationSeconds)
            || play.Results.DurationSeconds is < 0 or > 24 * 60 * 60
            || InvalidOptional(play.Map.BaseStars, 0, 100)
            || InvalidOptional(play.Map.AdjustedStars, 0, 100)
            || InvalidOptional(play.Map.Ar, 0, 20)
            || InvalidOptional(play.Map.Cs, 0, 20)
            || InvalidOptional(play.Map.Od, 0, 20)
            || InvalidOptional(play.Map.Hp, 0, 20)
            || InvalidOptional(play.Map.Bpm, 0, 10_000)
            || play.Map.MaxCombo < 0
            || play.Results.N300 < 0
            || play.Results.N100 < 0
            || play.Results.N50 < 0
            || play.Results.Geki < 0
            || play.Results.Katu < 0
            || play.Results.SliderBreaks < 0
            || play.Results.LargeTickHits < 0
            || play.Results.LargeTickMisses < 0
            || play.Results.SmallTickHits < 0
            || play.Results.SmallTickMisses < 0
            || play.Results.SliderTailHits < 0
            || play.Results.SliderTailMisses < 0
            || !double.IsFinite(play.Results.UnstableRate)
            || !double.IsFinite(play.Results.FcPp)
            || !double.IsFinite(play.Results.MaxPp)
            || play.Results.FcPp < 0
            || play.Results.MaxPp < 0
            || !double.IsFinite(play.Movement.SampleRate)
            || play.Movement.SampleRate is < 0 or > 100_000
            || play.Movement.SampleCount <= 0
            || play.Movement.DroppedSamples < 0
            || string.IsNullOrWhiteSpace(play.Movement.Source)
            || play.Mods.Count > 128
            || play.Events.Count > 1_000_000
            || play.Timing?.Offsets.Count > 1_000_000)
            throw new InvalidDataException("The package contains invalid play values.");
        if (play.Mods.Any(mod => string.IsNullOrWhiteSpace(mod.Acronym)
                                 || mod.Acronym.Length > 32
                                 || mod.SettingsJson is null
                                 || mod.SettingsJson.Length > 64 * 1024))
            throw new InvalidDataException("The package contains invalid mod data.");
        if (play.Timing is { } timing
            && (!double.IsFinite(timing.Mean)
                || !double.IsFinite(timing.Median)
                || !double.IsFinite(timing.Deviation)
                || timing.HitCount < 0
                || timing.EarlyCount < 0
                || timing.LateCount < 0
                || timing.Offsets.Any(offset => !double.IsFinite(offset) || Math.Abs(offset) > 60_000)))
            throw new InvalidDataException("The package contains invalid timing data.");
        if (play.Input is { } input
            && (input.Key1Presses < 0
                || input.Key2Presses < 0
                || input.Alternations < 0
                || input.SimultaneousPresses < 0
                || input.Key1HoldMs < 0
                || input.Key2HoldMs < 0
                || input.PeakKps < 0
                || input.AverageKps < 0
                || !double.IsFinite(input.Key1HoldMs)
                || !double.IsFinite(input.Key2HoldMs)
                || !double.IsFinite(input.AverageKps)))
            throw new InvalidDataException("The package contains invalid input data.");
        if (play.Events.Any(item => string.IsNullOrWhiteSpace(item.EventType)
                                    || item.EventType.Length > 128
                                    || item.DataJson is null
                                    || item.DataJson.Length > 256 * 1024
                                    || (item.Value.HasValue && !double.IsFinite(item.Value.Value))))
            throw new InvalidDataException("The package contains invalid judgement data.");
        if (play.CapturedDifficulty.Values.Any(value =>
                InvalidOptional(value.Original, -100, 100_000)
                || InvalidOptional(value.Converted, -100, 100_000)))
            throw new InvalidDataException("The package contains invalid captured difficulty data.");
    }

    private static bool InvalidOptional(double? value, double minimum, double maximum) =>
        value is { } number
        && (!double.IsFinite(number) || number < minimum || number > maximum);

    private static void ValidateMovementDescriptor(KumoriPackageMovementEntryV1 descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Entry)
            || !descriptor.Entry.StartsWith("movement/", StringComparison.Ordinal)
            || !IsSafeEntryName(descriptor.Entry)
            || descriptor.SampleCount is <= 0 or > MovementRepository.MaxSamplesPerChunk
            || descriptor.Size is <= 0 or > MovementRepository.MaxCompressedChunkBytes)
            throw new InvalidDataException($"Movement entry '{descriptor.Entry}' is invalid.");
        ValidateHash(descriptor.Sha256, descriptor.Entry);
    }

    private static void ValidateAssetDescriptor(KumoriPackageAssetV1 asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Entry)
            || string.IsNullOrWhiteSpace(asset.LogicalName)
            || string.IsNullOrWhiteSpace(asset.Role)
            || !asset.Entry.StartsWith("media/", StringComparison.Ordinal)
            || !IsSafeEntryName(asset.Entry)
            || SafeLogicalName(asset.LogicalName) != asset.LogicalName
            || asset.Size is <= 0 or > MaxAssetBytes
            || asset.Role is not ("beatmap" or "audio" or "background" or "sample"))
            throw new InvalidDataException($"Package asset '{asset.LogicalName}' is invalid.");
        if (asset.Role == "beatmap" && !asset.LogicalName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The packaged beatmap must use the .osu extension.");
        ValidateHash(asset.Sha256, asset.LogicalName);
    }

    private static List<ShareMediaFile> NormalizeExportMedia(
        IReadOnlyList<ShareMediaFile> mediaFiles,
        bool requireAudio = true)
    {
        var result = new List<ShareMediaFile>();
        var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ShareMediaFile item in mediaFiles)
        {
            string logical = SafeLogicalName(item.LogicalName);
            string role = item.Role.Trim().ToLowerInvariant();
            if (role is not ("beatmap" or "audio" or "background" or "sample"))
                throw new InvalidDataException($"Media role '{item.Role}' is not supported.");
            if (!logicalNames.Add(logical))
                continue;
            result.Add(new ShareMediaFile(logical, role, Path.GetFullPath(item.Path)));
        }
        if (result.Count(file => file.Role == "beatmap") != 1
            || (requireAudio && result.Count(file => file.Role == "audio") != 1))
        {
            throw new InvalidDataException(requireAudio
                ? "A share must contain exactly one beatmap and one audio file."
                : "A compact share must contain exactly one beatmap file.");
        }
        return result;
    }

    private static string ComputeFingerprint(
        string playHash,
        IEnumerable<KumoriPackageMovementEntryV1> movement,
        IEnumerable<KumoriPackageAssetV1> assets)
    {
        var builder = new StringBuilder(playHash.ToLowerInvariant());
        foreach (KumoriPackageMovementEntryV1 item in movement.OrderBy(item => item.Entry, StringComparer.Ordinal))
            builder.Append('\n').Append(item.Entry).Append('|').Append(item.SampleCount).Append('|').Append(item.Sha256.ToLowerInvariant());
        foreach (KumoriPackageAssetV1 item in assets
                     .OrderBy(item => item.Role, StringComparer.Ordinal)
                     .ThenBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase))
            builder.Append('\n').Append(item.Role).Append('|').Append(item.LogicalName.ToLowerInvariant()).Append('|').Append(item.Sha256.ToLowerInvariant());
        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private void EnsureImportSchema()
    {
        if (schemaReady)
            return;
        lock (schemaGate)
        {
            if (schemaReady)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(importsDatabase)!);
            using SqliteConnection connection = OpenImports(readOnly: false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS imported_plays(
                    id INTEGER PRIMARY KEY,
                    fingerprint TEXT NOT NULL UNIQUE,
                    player_name TEXT NOT NULL,
                    exported_at TEXT NOT NULL,
                    imported_at TEXT NOT NULL,
                    app_version TEXT NOT NULL,
                    play_json TEXT NOT NULL,
                    package_size INTEGER NOT NULL,
                    optional_omissions_json TEXT NOT NULL DEFAULT '[]'
                );
                CREATE TABLE IF NOT EXISTS imported_assets(
                    hash TEXT PRIMARY KEY,
                    local_path TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    reference_count INTEGER NOT NULL CHECK(reference_count > 0),
                    is_managed INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE IF NOT EXISTS imported_play_assets(
                    import_id INTEGER NOT NULL REFERENCES imported_plays(id) ON DELETE CASCADE,
                    logical_name TEXT NOT NULL,
                    role TEXT NOT NULL,
                    asset_hash TEXT NOT NULL REFERENCES imported_assets(hash),
                    PRIMARY KEY(import_id, logical_name)
                );
                CREATE TABLE IF NOT EXISTS imported_movement_chunks(
                    import_id INTEGER NOT NULL REFERENCES imported_plays(id) ON DELETE CASCADE,
                    position INTEGER NOT NULL,
                    sample_count INTEGER NOT NULL,
                    payload_zlib BLOB NOT NULL,
                    PRIMARY KEY(import_id, position)
                );
                CREATE INDEX IF NOT EXISTS idx_imported_plays_imported_at
                    ON imported_plays(imported_at DESC);
                """;
            command.ExecuteNonQuery();
            if (!ImportTableHasColumn(connection, "imported_assets", "is_managed"))
            {
                using var migrateAssets = connection.CreateCommand();
                migrateAssets.CommandText =
                    "ALTER TABLE imported_assets ADD COLUMN is_managed INTEGER NOT NULL DEFAULT 1";
                migrateAssets.ExecuteNonQuery();
            }
            schemaReady = true;
        }
    }

    private SqliteConnection OpenImports(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = importsDatabase,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = true,
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static long? FindImportId(SqliteConnection connection, string fingerprint, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM imported_plays WHERE fingerprint = @fingerprint";
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        object? value = command.ExecuteScalar();
        return value is null || value == DBNull.Value ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool ImportTableHasColumn(
        SqliteConnection connection,
        string table,
        string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task<IReadOnlyDictionary<string, string>> FindMatchingLocalAssetsAsync(
        SharedPlayV1 play,
        IReadOnlyList<IGrouping<string, KumoriPackageAssetV1>> assetGroups,
        CancellationToken cancellationToken)
    {
        if (localAssetCandidates is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> candidates;
        try
        {
            candidates = localAssetCandidates(play) ?? [];
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or InvalidOperationException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var needed = assetGroups.ToDictionary(
            group => group.Key,
            group => group.First().Size,
            StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidates
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(MaxEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string path = Path.GetFullPath(candidate);
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is <= 0 or > MaxAssetBytes
                    || !needed.Values.Contains(file.Length))
                    continue;
                string hash = await HashFileAsync(path, cancellationToken);
                if (needed.TryGetValue(hash, out long expectedSize)
                    && expectedSize == file.Length)
                {
                    matches.TryAdd(hash, path);
                    if (matches.Count == needed.Count)
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                // A stale local cache entry should not make the portable import fail.
            }
        }
        return matches;
    }

    private async Task<IReadOnlyList<ShareMediaFile>> ResolveCompactMediaAsync(
        string packagePath,
        ValidatedPackage package,
        CancellationToken cancellationToken)
    {
        if (compactMediaResolver is null)
            throw new InvalidOperationException(
                "This compact replay needs beatmap media, but media resolution is unavailable.");

        IReadOnlyList<ShareMediaFile> resolved = NormalizeExportMedia(
            await compactMediaResolver(package.Play, cancellationToken));
        ShareMediaFile? audio = resolved.SingleOrDefault(file => file.Role == "audio");
        if (audio is null)
            throw new InvalidOperationException(
                "Kumori could not locate or download the audio required by this compact replay.");

        KumoriPackageAssetV1 beatmap = package.Manifest.Assets.Single(asset => asset.Role == "beatmap");
        ShareMediaFile resolvedBeatmap = resolved.Single(file => file.Role == "beatmap");
        if (!FixedHashEquals(
                await HashFileAsync(resolvedBeatmap.Path, cancellationToken),
                beatmap.Sha256))
        {
            throw new InvalidDataException(
                "Resolved beatmap media does not match the compact package.");
        }
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = archive.GetEntry(beatmap.Entry)
            ?? throw new InvalidDataException("The compact package beatmap is missing.");
        byte[] beatmapBytes = await ReadEntryAsync(entry, MaxAssetBytes, cancellationToken);
        string referencedAudio = ReadAudioFilename(beatmapBytes);
        if (!string.Equals(referencedAudio, audio.LogicalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Resolved beatmap audio does not match the compact package.");
        return resolved;
    }

    private static AssetLookup LoadAssets(SqliteConnection connection, long importId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.logical_name, p.role, a.local_path
            FROM imported_play_assets p
            JOIN imported_assets a ON a.hash = p.asset_hash
            WHERE p.import_id = @id
            """;
        command.Parameters.AddWithValue("@id", importId);
        using var reader = command.ExecuteReader();
        var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? beatmap = null;
        string? background = null;
        while (reader.Read())
        {
            string logical = reader.GetString(0);
            string role = reader.GetString(1);
            string path = reader.GetString(2);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Imported media '{logical}' is missing.", path);
            media[logical] = path;
            if (role == "beatmap")
                beatmap = path;
            else if (role == "background")
                background = path;
        }
        if (beatmap is null)
            throw new InvalidDataException("The imported play does not have a beatmap asset.");
        return new AssetLookup(beatmap, background, media);
    }

    private static SharedPlayV1 DeserializePlay(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SharedPlayV1>(json, JsonOptions)
                   ?? throw new InvalidDataException("Imported play data is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Imported play data is corrupt.", ex);
        }
    }

    private void TryDeleteFileIfUnreferenced(string path)
    {
        try
        {
            if (!File.Exists(importsDatabase))
            {
                TryDeleteFile(path);
                return;
            }
            using SqliteConnection connection = OpenImports(readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM imported_assets WHERE local_path = @path LIMIT 1";
            command.Parameters.AddWithValue("@path", path);
            if (command.ExecuteScalar() is null)
                TryDeleteFile(path);
        }
        catch
        {
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream output = entry.Open();
        await output.WriteAsync(payload, cancellationToken);
    }

    private static async Task WriteFileEntryAsync(
        ZipArchive archive,
        string name,
        string path,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream output = entry.Open();
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 64 * 1024, cancellationToken);
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length is < 0 || entry.Length > maximumBytes || entry.Length > int.MaxValue)
            throw new InvalidDataException($"Package entry '{entry.FullName}' exceeds its size limit.");
        byte[] result = new byte[(int)entry.Length];
        await using Stream stream = entry.Open();
        int offset = 0;
        while (offset < result.Length)
        {
            int read = await stream.ReadAsync(result.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new InvalidDataException($"Package entry '{entry.FullName}' is truncated.");
            offset += read;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"Package entry '{entry.FullName}' exceeds its declared size.");
        return result;
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length != expectedBytes || expectedBytes > MaxAssetBytes)
            throw new InvalidDataException($"Package entry '{entry.FullName}' has an invalid size.");
        await using Stream input = entry.Open();
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            written = checked(written + read);
            if (written > expectedBytes)
                throw new InvalidDataException($"Package entry '{entry.FullName}' exceeds its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (written != expectedBytes)
            throw new InvalidDataException($"Package entry '{entry.FullName}' is truncated.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashStreamAsync(stream, cancellationToken);
    }

    private static async Task<string> HashStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Hash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static bool FixedHashEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateHash(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"The {label} hash is invalid.");
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    private static bool IsSafeEntryName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith("/", StringComparison.Ordinal)
        && !name.Contains(':')
        && !name.Split('/').Any(part => part is "" or "." or "..");

    private static string SafeLogicalName(string value)
    {
        string name = Path.GetFileName(value.Replace('\\', '/')).Trim();
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.Length > 240
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Media name '{value}' is not safe.");
        return name;
    }

    private static string ReadAudioFilename(byte[] beatmapBytes)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(beatmapBytes));
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimStart().StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase))
                return SafeLogicalName(line.Split(':', 2)[1].Trim());
        }
        return "";
    }

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static bool TryCreateHardLink(string destination, string source)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return CreateHardLink(destination, source, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed record ValidatedPackage(
        KumoriPackageManifestV1 Manifest,
        SharedPlayV1 Play,
        byte[] PlayBytes);

    private sealed record AssetLookup(
        string BeatmapPath,
        string? BackgroundPath,
        IReadOnlyDictionary<string, string> MediaPaths);
}
