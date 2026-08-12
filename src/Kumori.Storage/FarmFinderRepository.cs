using System.Globalization;
using System.Text.Json;
using Kumori.FarmFinder;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class FarmFinderRepository : IFarmFinderRepository, IFarmStarRatingCache
{
    public const int CurrentSchemaVersion = 6;

    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SqliteConnectionFactory factory;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private readonly SemaphoreSlim starRatingWriteGate = new(1, 1);
    private volatile bool initialized;

    public FarmFinderRepository(string databasePath)
    {
        factory = new SqliteConnectionFactory(databasePath, readOnly: false);
    }

    public string DatabasePath => factory.DatabasePath;

    public async Task<IReadOnlyList<FarmCachedStarRating>> LoadAsync(
        string calculatorVersion,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<FarmCachedStarRating>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT beatmap_id, mods_key, star_rating
                FROM farm_star_ratings
                WHERE calculator_version=@calculator_version
                """;
            command.Parameters.AddWithValue("@calculator_version", calculatorVersion);
            using var reader = command.ExecuteReader();
            var results = new List<FarmCachedStarRating>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new FarmCachedStarRating(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetDouble(2)));
            }
            return results;
        }, cancellationToken);
    }

    public async Task<double?> GetAsync(
        long beatmapId,
        string modsKey,
        string calculatorVersion,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run<double?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT star_rating
                FROM farm_star_ratings
                WHERE beatmap_id=@beatmap_id
                  AND mods_key=@mods_key
                  AND calculator_version=@calculator_version
                """;
            command.Parameters.AddWithValue("@beatmap_id", beatmapId);
            command.Parameters.AddWithValue("@mods_key", modsKey);
            command.Parameters.AddWithValue("@calculator_version", calculatorVersion);
            var value = command.ExecuteScalar();
            return value is null or DBNull
                ? null
                : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public async Task SaveAsync(
        long beatmapId,
        string modsKey,
        string calculatorVersion,
        double starRating,
        CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0
            || string.IsNullOrWhiteSpace(modsKey)
            || string.IsNullOrWhiteSpace(calculatorVersion)
            || starRating <= 0
            || !double.IsFinite(starRating))
            throw new ArgumentException("The calculated star rating is invalid.");

        await InitializeAsync(cancellationToken);
        await starRatingWriteGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = factory.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO farm_star_ratings(
                        beatmap_id, mods_key, calculator_version, star_rating, updated_at)
                    VALUES(
                        @beatmap_id, @mods_key, @calculator_version, @star_rating, @updated_at)
                    ON CONFLICT(beatmap_id, mods_key, calculator_version) DO UPDATE SET
                        star_rating=excluded.star_rating,
                        updated_at=excluded.updated_at
                    """;
                command.Parameters.AddWithValue("@beatmap_id", beatmapId);
                command.Parameters.AddWithValue("@mods_key", modsKey);
                command.Parameters.AddWithValue("@calculator_version", calculatorVersion);
                command.Parameters.AddWithValue("@star_rating", starRating);
                command.Parameters.AddWithValue("@updated_at", Now());
                command.ExecuteNonQuery();
            }, cancellationToken);
        }
        finally
        {
            starRatingWriteGate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
            return;
        await initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
                return;
            var directory = Path.GetDirectoryName(factory.DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await Task.Run(() => InitializeSchema(cancellationToken), cancellationToken);
            initialized = true;
        }
        finally
        {
            initializeGate.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await initializeGate.WaitAsync(cancellationToken);
        try
        {
            SqliteConnection.ClearAllPools();
            initialized = false;
        }
        finally
        {
            initializeGate.Release();
        }
        await InitializeAsync(cancellationToken);
    }

    public async Task<IndexJob?> GetResumableJobAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, minimum_rank, maximum_rank, status, cursor_json,
                       players_total, players_completed, players_failed, started_at, updated_at
                FROM farm_index_jobs
                WHERE status IN ('running', 'paused')
                ORDER BY updated_at DESC, id DESC
                LIMIT 1
                """;
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }, cancellationToken);
    }

    public async Task<IndexJob> BeginOrResumeJobAsync(
        int minimumRank,
        int maximumRank,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT id, minimum_rank, maximum_rank, status, cursor_json,
                           players_total, players_completed, players_failed, started_at, updated_at
                    FROM farm_index_jobs
                    WHERE minimum_rank=@minimum AND maximum_rank=@maximum
                      AND status IN ('running', 'paused')
                    ORDER BY updated_at DESC, id DESC
                    LIMIT 1
                    """;
                select.Parameters.AddWithValue("@minimum", minimumRank);
                select.Parameters.AddWithValue("@maximum", maximumRank);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    var existing = ReadJob(reader);
                    reader.Close();
                    using var resume = connection.CreateCommand();
                    resume.Transaction = transaction;
                    resume.CommandText = "UPDATE farm_index_jobs SET status='running', updated_at=@now WHERE id=@id";
                    resume.Parameters.AddWithValue("@now", Now());
                    resume.Parameters.AddWithValue("@id", existing.Id);
                    resume.ExecuteNonQuery();
                    using var resumeSnapshot = connection.CreateCommand();
                    resumeSnapshot.Transaction = transaction;
                    resumeSnapshot.CommandText = """
                        INSERT OR IGNORE INTO farm_ranking_snapshots(
                            snapshot_id, minimum_rank, maximum_rank, created_at)
                        VALUES(@id, @minimum, @maximum, @created)
                        """;
                    resumeSnapshot.Parameters.AddWithValue("@id", existing.Id);
                    resumeSnapshot.Parameters.AddWithValue("@minimum", existing.MinimumRank);
                    resumeSnapshot.Parameters.AddWithValue("@maximum", existing.MaximumRank);
                    resumeSnapshot.Parameters.AddWithValue(
                        "@created",
                        existing.StartedAt.ToUniversalTime().ToString("O"));
                    resumeSnapshot.ExecuteNonQuery();
                    transaction.Commit();
                    return existing with { Status = "running", UpdatedAt = DateTimeOffset.UtcNow };
                }
            }

            var now = Now();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO farm_index_jobs(
                    minimum_rank, maximum_rank, status, players_total,
                    players_completed, players_failed, started_at, updated_at)
                VALUES(@minimum, @maximum, 'running', 0, 0, 0, @now, @now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@minimum", minimumRank);
            insert.Parameters.AddWithValue("@maximum", maximumRank);
            insert.Parameters.AddWithValue("@now", now);
            var id = (long)(insert.ExecuteScalar() ?? 0L);
            using var snapshot = connection.CreateCommand();
            snapshot.Transaction = transaction;
            snapshot.CommandText = """
                INSERT INTO farm_ranking_snapshots(
                    snapshot_id, minimum_rank, maximum_rank, created_at)
                VALUES(@id, @minimum, @maximum, @now)
                """;
            snapshot.Parameters.AddWithValue("@id", id);
            snapshot.Parameters.AddWithValue("@minimum", minimumRank);
            snapshot.Parameters.AddWithValue("@maximum", maximumRank);
            snapshot.Parameters.AddWithValue("@now", now);
            snapshot.ExecuteNonQuery();
            transaction.Commit();
            var timestamp = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture);
            return new IndexJob(id, minimumRank, maximumRank, "running", null, 0, 0, 0, timestamp, timestamp);
        }, cancellationToken);
    }

    public async Task UpdateJobCursorAsync(
        long jobId,
        string? cursorJson,
        int playersTotal,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("""
            UPDATE farm_index_jobs
            SET cursor_json=@cursor, players_total=@total, updated_at=@now
            WHERE id=@id
            """,
            command =>
            {
                command.Parameters.AddWithValue("@cursor", (object?)cursorJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@total", playersTotal);
                command.Parameters.AddWithValue("@now", Now());
                command.Parameters.AddWithValue("@id", jobId);
            },
            cancellationToken);
    }

    public async Task UpsertRankingPlayersAsync(
        long jobId,
        IReadOnlyList<FarmPlayer> players,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var player in players)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO farm_players(user_id, username, global_rank, total_pp, rank_updated_at)
                    VALUES(@user_id, @username, @global_rank, @total_pp, @updated)
                    ON CONFLICT(user_id) DO UPDATE SET
                        username=excluded.username,
                        global_rank=excluded.global_rank,
                        total_pp=excluded.total_pp,
                        rank_updated_at=excluded.rank_updated_at
                    """;
                upsert.Parameters.AddWithValue("@user_id", player.UserId);
                upsert.Parameters.AddWithValue("@username", player.Username);
                upsert.Parameters.AddWithValue("@global_rank", player.GlobalRank);
                upsert.Parameters.AddWithValue("@total_pp", player.TotalPp);
                upsert.Parameters.AddWithValue("@updated", player.RankUpdatedAt.ToUniversalTime().ToString("O"));
                upsert.ExecuteNonQuery();

                using var membership = connection.CreateCommand();
                membership.Transaction = transaction;
                membership.CommandText = """
                    INSERT INTO farm_index_job_players(job_id, user_id, global_rank, status)
                    VALUES(@job_id, @user_id, @global_rank, 'pending')
                    ON CONFLICT(job_id, user_id) DO UPDATE SET global_rank=excluded.global_rank
                    """;
                membership.Parameters.AddWithValue("@job_id", jobId);
                membership.Parameters.AddWithValue("@user_id", player.UserId);
                membership.Parameters.AddWithValue("@global_rank", player.GlobalRank);
                membership.ExecuteNonQuery();

                using var snapshotMembership = connection.CreateCommand();
                snapshotMembership.Transaction = transaction;
                snapshotMembership.CommandText = """
                    INSERT INTO farm_ranking_snapshot_members(
                        snapshot_id, user_id, global_rank, total_pp)
                    VALUES(@snapshot_id, @user_id, @global_rank, @total_pp)
                    ON CONFLICT(snapshot_id, user_id) DO UPDATE SET
                        global_rank=excluded.global_rank,
                        total_pp=excluded.total_pp
                    """;
                snapshotMembership.Parameters.AddWithValue("@snapshot_id", jobId);
                snapshotMembership.Parameters.AddWithValue("@user_id", player.UserId);
                snapshotMembership.Parameters.AddWithValue("@global_rank", player.GlobalRank);
                snapshotMembership.Parameters.AddWithValue("@total_pp", player.TotalPp);
                snapshotMembership.ExecuteNonQuery();
            }
            transaction.Commit();
        }, cancellationToken);
    }

    public Task UpsertCountryCoverageAsync(
        long jobId,
        CountryCoverage coverage,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("""
            INSERT INTO farm_country_coverage(
                snapshot_id, country_code, covered_through_global_rank,
                requested_maximum_rank, is_complete, hit_api_limit, updated_at)
            VALUES(
                @snapshot_id, @country_code, @covered_through,
                @requested_maximum, @is_complete, @hit_api_limit, @updated_at)
            ON CONFLICT(snapshot_id, country_code) DO UPDATE SET
                covered_through_global_rank=excluded.covered_through_global_rank,
                requested_maximum_rank=excluded.requested_maximum_rank,
                is_complete=excluded.is_complete,
                hit_api_limit=excluded.hit_api_limit,
                updated_at=excluded.updated_at
            """,
            command =>
            {
                command.Parameters.AddWithValue("@snapshot_id", jobId);
                command.Parameters.AddWithValue(
                    "@country_code",
                    coverage.CountryCode.Trim().ToUpperInvariant());
                command.Parameters.AddWithValue(
                    "@covered_through",
                    coverage.CoveredThroughGlobalRank);
                command.Parameters.AddWithValue(
                    "@requested_maximum",
                    coverage.RequestedMaximumRank);
                command.Parameters.AddWithValue(
                    "@is_complete",
                    coverage.IsComplete ? 1 : 0);
                command.Parameters.AddWithValue(
                    "@hit_api_limit",
                    coverage.HitApiLimit ? 1 : 0);
                command.Parameters.AddWithValue("@updated_at", Now());
            },
            cancellationToken);

    public async Task<IReadOnlyList<FarmPlayer>> GetPlayersInRangeAsync(
        int minimumRank,
        int maximumRank,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<FarmPlayer>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, username, global_rank, total_pp, rank_updated_at,
                       scores_updated_at, score_metadata_version
                FROM farm_players
                WHERE global_rank BETWEEN @minimum AND @maximum
                ORDER BY global_rank, user_id
                """;
            command.Parameters.AddWithValue("@minimum", minimumRank);
            command.Parameters.AddWithValue("@maximum", maximumRank);
            using var reader = command.ExecuteReader();
            var results = new List<FarmPlayer>();
            while (reader.Read())
            {
                results.Add(new FarmPlayer(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetDouble(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.GetInt32(6)));
            }
            return results;
        }, cancellationToken);
    }

    public async Task<FarmScoreMetadataRepairStatus> GetScoreMetadataRepairStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN score_metadata_version < @version THEN 1 ELSE 0 END), 0)
                FROM farm_players
                """;
            command.Parameters.AddWithValue("@version", FarmScoreMetadata.CurrentVersion);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new FarmScoreMetadataRepairStatus(
                reader.GetInt32(0),
                reader.GetInt32(1));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FarmPlayer>> GetPlayersNeedingScoreMetadataRepairAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<FarmPlayer>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, username, global_rank, total_pp, rank_updated_at,
                       scores_updated_at, score_metadata_version
                FROM farm_players
                WHERE score_metadata_version < @version
                ORDER BY global_rank, user_id
                """;
            command.Parameters.AddWithValue("@version", FarmScoreMetadata.CurrentVersion);
            using var reader = command.ExecuteReader();
            var players = new List<FarmPlayer>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                players.Add(new FarmPlayer(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetDouble(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.GetInt32(6)));
            }
            return players;
        }, cancellationToken);
    }

    public async Task ReplacePlayerScoresAsync(
        PlayerScoresPayload payload,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var beatmap in payload.Beatmaps)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO farm_beatmaps(
                        beatmap_id, beatmapset_id, artist, title, difficulty, mapper,
                        base_bpm, hit_length_seconds, total_length_seconds, star_rating,
                        status, ranked_at, cover_url, circle_size, approach_rate,
                        overall_difficulty, drain_rate, updated_at)
                    VALUES(
                        @beatmap_id, @beatmapset_id, @artist, @title, @difficulty, @mapper,
                        @base_bpm, @hit_length, @total_length, @stars,
                        @status, @ranked_at, @cover_url, @circle_size, @approach_rate,
                        @overall_difficulty, @drain_rate, @updated_at)
                    ON CONFLICT(beatmap_id) DO UPDATE SET
                        beatmapset_id=excluded.beatmapset_id,
                        artist=excluded.artist,
                        title=excluded.title,
                        difficulty=excluded.difficulty,
                        mapper=excluded.mapper,
                        base_bpm=excluded.base_bpm,
                        hit_length_seconds=excluded.hit_length_seconds,
                        total_length_seconds=excluded.total_length_seconds,
                        star_rating=excluded.star_rating,
                        status=excluded.status,
                        ranked_at=excluded.ranked_at,
                        cover_url=excluded.cover_url,
                        circle_size=excluded.circle_size,
                        approach_rate=excluded.approach_rate,
                        overall_difficulty=excluded.overall_difficulty,
                        drain_rate=excluded.drain_rate,
                        updated_at=excluded.updated_at
                    """;
                AddBeatmapParameters(command, beatmap);
                command.Parameters.AddWithValue("@updated_at", Now());
                command.ExecuteNonQuery();
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM farm_scores WHERE user_id=@user_id";
                delete.Parameters.AddWithValue("@user_id", payload.Player.UserId);
                delete.ExecuteNonQuery();
            }
            foreach (var score in payload.Scores)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO farm_scores(
                        score_id, user_id, beatmap_id, pp, accuracy, miss_count,
                        max_combo, is_full_combo, ended_at, actual_mods_json,
                        canonical_mod_signature, clock_rate, score_origin,
                        legacy_score_id, total_score, legacy_total_score, build_id,
                        source_type)
                    VALUES(
                        @score_id, @user_id, @beatmap_id, @pp, @accuracy, @miss_count,
                        @max_combo, @is_full_combo, @ended_at, @actual_mods_json,
                        @canonical_mod_signature, @clock_rate, @score_origin,
                        @legacy_score_id, @total_score, @legacy_total_score, @build_id,
                        @source_type)
                    """;
                command.Parameters.AddWithValue("@score_id", score.ScoreId);
                command.Parameters.AddWithValue("@user_id", score.UserId);
                command.Parameters.AddWithValue("@beatmap_id", score.BeatmapId);
                command.Parameters.AddWithValue("@pp", score.Pp);
                command.Parameters.AddWithValue("@accuracy", score.Accuracy);
                command.Parameters.AddWithValue("@miss_count", score.MissCount);
                command.Parameters.AddWithValue("@max_combo", score.MaxCombo);
                command.Parameters.AddWithValue("@is_full_combo", score.IsFullCombo ? 1 : 0);
                command.Parameters.AddWithValue("@ended_at", score.EndedAt.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("@actual_mods_json", JsonSerializer.Serialize(score.ActualMods, jsonOptions));
                command.Parameters.AddWithValue("@canonical_mod_signature", score.CanonicalModSignature);
                command.Parameters.AddWithValue("@clock_rate", score.ClockRate);
                command.Parameters.AddWithValue("@score_origin", score.Origin.ToString().ToLowerInvariant());
                AddNullable(command, "@legacy_score_id", score.LegacyScoreId);
                AddNullable(command, "@total_score", score.TotalScore);
                AddNullable(command, "@legacy_total_score", score.LegacyTotalScore);
                AddNullable(command, "@build_id", score.BuildId);
                AddNullable(command, "@source_type", score.SourceType);
                command.ExecuteNonQuery();
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE farm_players
                    SET scores_updated_at=@updated,
                        score_metadata_version=@metadata_version
                    WHERE user_id=@user_id
                    """;
                update.Parameters.AddWithValue("@updated", (payload.Player.ScoresUpdatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"));
                update.Parameters.AddWithValue("@user_id", payload.Player.UserId);
                update.Parameters.AddWithValue("@metadata_version", FarmScoreMetadata.CurrentVersion);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
        }, cancellationToken);
    }

    public Task RecordPlayerFailureAsync(
        long jobId,
        long userId,
        string error,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("""
            INSERT INTO farm_index_failures(job_id, user_id, error, failed_at)
            VALUES(@job_id, @user_id, @error, @now)
            ON CONFLICT(job_id, user_id) DO UPDATE SET
                error=excluded.error, failed_at=excluded.failed_at;
            UPDATE farm_index_job_players
            SET status='failed'
            WHERE job_id=@job_id AND user_id=@user_id;
            UPDATE farm_index_jobs
            SET players_failed=(SELECT COUNT(*) FROM farm_index_job_players WHERE job_id=@job_id AND status='failed'),
                updated_at=@now
            WHERE id=@job_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("@job_id", jobId);
                command.Parameters.AddWithValue("@user_id", userId);
                command.Parameters.AddWithValue("@error", error.Length <= 1000 ? error : error[..1000]);
                command.Parameters.AddWithValue("@now", Now());
            },
            cancellationToken);

    public Task MarkPlayerCompletedAsync(
        long jobId,
        long userId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("""
            UPDATE farm_index_job_players
            SET status='completed'
            WHERE job_id=@job_id AND user_id=@user_id;
            DELETE FROM farm_index_failures WHERE job_id=@job_id AND user_id=@user_id;
            UPDATE farm_index_jobs
            SET players_completed=(SELECT COUNT(*) FROM farm_index_job_players WHERE job_id=@job_id AND status='completed'),
                players_failed=(SELECT COUNT(*) FROM farm_index_job_players WHERE job_id=@job_id AND status='failed'),
                updated_at=@now
            WHERE id=@job_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("@job_id", jobId);
                command.Parameters.AddWithValue("@user_id", userId);
                command.Parameters.AddWithValue("@now", Now());
            },
            cancellationToken);

    public Task CompleteJobAsync(
        long jobId,
        bool cancelled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("""
            UPDATE farm_index_jobs
            SET status=@status, updated_at=@now,
                completed_at=CASE WHEN @status='completed' THEN @now ELSE completed_at END
            WHERE id=@id;
            UPDATE farm_ranking_snapshots
            SET completed_at=CASE WHEN @status='completed' THEN @now ELSE completed_at END
            WHERE snapshot_id=@id
            """,
            command =>
            {
                command.Parameters.AddWithValue("@status", cancelled ? "paused" : "completed");
                command.Parameters.AddWithValue("@now", Now());
                command.Parameters.AddWithValue("@id", jobId);
            },
            cancellationToken);

    public async Task<IReadOnlyList<FarmScoreCandidate>> QueryCandidatesAsync(
        FarmFinderQuery query,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<FarmScoreCandidate>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            var predicates = new List<string>();
            void AddPredicate(string sql, string parameter, object value)
            {
                predicates.Add(sql);
                command.Parameters.AddWithValue(parameter, value);
            }

            if (query.MinimumGlobalRank is { } minimumRank)
                AddPredicate("p.global_rank >= @rank_min", "@rank_min", minimumRank);
            if (query.MaximumGlobalRank is { } maximumRank)
                AddPredicate("p.global_rank <= @rank_max", "@rank_max", maximumRank);
            if (query.MinimumPp is { } minimumPp)
                AddPredicate("s.pp >= @pp_min", "@pp_min", minimumPp);
            if (query.MaximumPp is { } maximumPp)
                AddPredicate("s.pp <= @pp_max", "@pp_max", maximumPp);
            if (query.MinimumEffectiveBpm is { } minimumBpm)
                AddPredicate("b.base_bpm * s.clock_rate >= @bpm_min", "@bpm_min", minimumBpm);
            if (query.MaximumEffectiveBpm is { } maximumBpm)
                AddPredicate("b.base_bpm * s.clock_rate <= @bpm_max", "@bpm_max", maximumBpm);
            if (query.MinimumEffectiveLengthSeconds is { } minimumLength)
                AddPredicate(
                    "b.hit_length_seconds / s.clock_rate >= @length_min",
                    "@length_min",
                    minimumLength);
            if (query.MaximumEffectiveLengthSeconds is { } maximumLength)
                AddPredicate(
                    "b.hit_length_seconds / s.clock_rate <= @length_max",
                    "@length_max",
                    maximumLength);
            if (query.MapStatus != FarmMapStatus.Any)
                AddPredicate(
                    "b.status = @status COLLATE NOCASE",
                    "@status",
                    query.MapStatus.ToString());
            if (query.RankedFrom is { } rankedFrom)
                AddPredicate(
                    "b.ranked_at >= @ranked_from",
                    "@ranked_from",
                    rankedFrom.ToUniversalTime().ToString("O"));
            if (query.RankedTo is { } rankedTo)
                AddPredicate(
                    "b.ranked_at <= @ranked_to",
                    "@ranked_to",
                    rankedTo.ToUniversalTime().ToString("O"));
            if (!string.IsNullOrWhiteSpace(query.TextSearch))
            {
                predicates.Add("""
                    (b.artist LIKE @search ESCAPE '\' OR b.title LIKE @search ESCAPE '\' OR
                     b.difficulty LIKE @search ESCAPE '\' OR b.mapper LIKE @search ESCAPE '\')
                    """);
                command.Parameters.AddWithValue(
                    "@search",
                    $"%{EscapeLike(query.TextSearch.Trim())}%");
            }

            AppendSafeModPredicates(command, predicates, query);
            var where = predicates.Count == 0
                ? string.Empty
                : $"WHERE {string.Join(Environment.NewLine + "  AND ", predicates)}";
            command.CommandText = $"""
                SELECT
                    p.user_id, p.username, p.global_rank, p.total_pp, p.rank_updated_at,
                    p.scores_updated_at, p.score_metadata_version,
                    s.score_id, s.beatmap_id, s.pp, s.accuracy, s.miss_count, s.max_combo,
                    s.is_full_combo, s.ended_at, s.actual_mods_json, s.canonical_mod_signature, s.clock_rate,
                    s.score_origin, s.legacy_score_id, s.total_score, s.legacy_total_score,
                    s.build_id, s.source_type,
                    b.beatmapset_id, b.artist, b.title, b.difficulty, b.mapper, b.base_bpm,
                    b.hit_length_seconds, b.total_length_seconds, b.star_rating, b.status, b.ranked_at, b.cover_url,
                    b.circle_size, b.approach_rate, b.overall_difficulty, b.drain_rate
                FROM farm_scores s
                JOIN farm_players p ON p.user_id=s.user_id
                JOIN farm_beatmaps b ON b.beatmap_id=s.beatmap_id
                {where}
                """;
            using var reader = command.ExecuteReader();
            var results = new List<FarmScoreCandidate>();
            var players = new Dictionary<long, FarmPlayer>();
            var beatmaps = new Dictionary<long, FarmBeatmap>();
            var modSets = new Dictionary<string, IReadOnlyList<FarmMod>>(
                StringComparer.Ordinal);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var userId = reader.GetInt64(0);
                if (!players.TryGetValue(userId, out var player))
                {
                    player = new FarmPlayer(
                        userId, reader.GetString(1), reader.GetInt32(2), reader.GetDouble(3),
                        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                        reader.IsDBNull(5)
                            ? null
                            : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                        reader.GetInt32(6));
                    players.Add(userId, player);
                }
                var modsJson = reader.GetString(15);
                if (!modSets.TryGetValue(modsJson, out var mods))
                {
                    mods = JsonSerializer.Deserialize<FarmMod[]>(modsJson, jsonOptions) ?? [];
                    modSets.Add(modsJson, mods);
                }
                var score = new FarmScore(
                    reader.GetInt64(7), player.UserId, reader.GetInt64(8), reader.GetDouble(9),
                    reader.GetDouble(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13) != 0,
                    DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
                    mods, reader.GetString(16), reader.GetDouble(17),
                    ParseScoreOrigin(reader.GetString(18)),
                    reader.IsDBNull(19) ? null : reader.GetInt64(19),
                    reader.IsDBNull(20) ? null : reader.GetInt64(20),
                    reader.IsDBNull(21) ? null : reader.GetInt64(21),
                    reader.IsDBNull(22) ? null : reader.GetInt32(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23));
                if (!beatmaps.TryGetValue(score.BeatmapId, out var beatmap))
                {
                    beatmap = new FarmBeatmap(
                        score.BeatmapId, reader.GetInt64(24), reader.GetString(25), reader.GetString(26),
                        reader.GetString(27), reader.GetString(28), reader.GetDouble(29), reader.GetInt32(30),
                        reader.GetInt32(31), reader.GetDouble(32), reader.GetString(33),
                        reader.IsDBNull(34)
                            ? null
                            : DateTimeOffset.Parse(reader.GetString(34), CultureInfo.InvariantCulture),
                        reader.GetString(35))
                    {
                        CircleSize = reader.IsDBNull(36) ? null : reader.GetDouble(36),
                        ApproachRate = reader.IsDBNull(37) ? null : reader.GetDouble(37),
                        OverallDifficulty = reader.IsDBNull(38) ? null : reader.GetDouble(38),
                        DrainRate = reader.IsDBNull(39) ? null : reader.GetDouble(39),
                    };
                    beatmaps.Add(score.BeatmapId, beatmap);
                }
                results.Add(new FarmScoreCandidate(player, score, beatmap));
            }
            return results;
        }, cancellationToken);
    }

    public async Task<CoverageSummary> GetCoverageAsync(
        FarmFinderQuery query,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COUNT(*),
                    COUNT(scores_updated_at),
                    MAX(scores_updated_at),
                    MIN(global_rank),
                    MAX(global_rank)
                FROM farm_players
                WHERE (@minimum IS NULL OR global_rank >= @minimum)
                  AND (@maximum IS NULL OR global_rank <= @maximum)
                """;
            AddNullable(command, "@minimum", query.MinimumGlobalRank);
            AddNullable(command, "@maximum", query.MaximumGlobalRank);
            using var reader = command.ExecuteReader();
            reader.Read();
            var available = reader.GetInt32(0);
            var scanned = reader.GetInt32(1);
            DateTimeOffset? updated = reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            int? coveredMinimum = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            int? coveredMaximum = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            reader.Close();

            using var failures = connection.CreateCommand();
            failures.CommandText = """
                SELECT COUNT(*)
                FROM farm_index_failures f
                JOIN farm_index_jobs j ON j.id=f.job_id
                WHERE (@minimum IS NULL OR j.maximum_rank >= @minimum)
                  AND (@maximum IS NULL OR j.minimum_rank <= @maximum)
                  AND j.id=(SELECT MAX(id) FROM farm_index_jobs)
                """;
            AddNullable(failures, "@minimum", query.MinimumGlobalRank);
            AddNullable(failures, "@maximum", query.MaximumGlobalRank);
            var failed = Convert.ToInt32(failures.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var countryGaps = connection.CreateCommand();
            countryGaps.CommandText = """
                SELECT c.country_code, c.covered_through_global_rank,
                       c.requested_maximum_rank
                FROM farm_country_coverage c
                JOIN farm_index_jobs j ON j.id=c.snapshot_id
                WHERE c.is_complete=0 AND c.hit_api_limit=1
                  AND (@minimum IS NULL OR j.maximum_rank >= @minimum)
                  AND (@maximum IS NULL OR j.minimum_rank <= @maximum)
                  AND j.id=(
                      SELECT MAX(candidate.id)
                      FROM farm_index_jobs candidate
                      WHERE (@minimum IS NULL OR candidate.maximum_rank >= @minimum)
                        AND (@maximum IS NULL OR candidate.minimum_rank <= @maximum)
                  )
                ORDER BY c.covered_through_global_rank, c.country_code
                """;
            AddNullable(countryGaps, "@minimum", query.MinimumGlobalRank);
            AddNullable(countryGaps, "@maximum", query.MaximumGlobalRank);
            using var countryReader = countryGaps.ExecuteReader();
            var gaps = new List<CountryCoverageGap>();
            while (countryReader.Read())
            {
                gaps.Add(new CountryCoverageGap(
                    countryReader.GetString(0),
                    countryReader.GetInt32(1),
                    countryReader.GetInt32(2)));
            }
            return new CoverageSummary(
                available, scanned, scanned, 0, failed, 0, 0, 0,
                updated, coveredMinimum, coveredMaximum, gaps);
        }, cancellationToken);
    }

    public Task UpdateBeatmapDifficultyAsync(
        FarmBeatmap beatmap,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            """
            UPDATE farm_beatmaps
            SET circle_size=@circle_size,
                approach_rate=@approach_rate,
                overall_difficulty=@overall_difficulty,
                drain_rate=@drain_rate
            WHERE beatmap_id=@beatmap_id
            """,
            command =>
            {
                command.Parameters.AddWithValue("@beatmap_id", beatmap.BeatmapId);
                AddNullable(command, "@circle_size", beatmap.CircleSize);
                AddNullable(command, "@approach_rate", beatmap.ApproachRate);
                AddNullable(command, "@overall_difficulty", beatmap.OverallDifficulty);
                AddNullable(command, "@drain_rate", beatmap.DrainRate);
            },
            cancellationToken);

    private async Task ExecuteAsync(
        string sql,
        Action<SqliteCommand> parameters,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            parameters(command);
            command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }, cancellationToken);
    }

    private void InitializeSchema(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = factory.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS farm_metadata(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS farm_players(
                user_id INTEGER PRIMARY KEY,
                username TEXT NOT NULL,
                global_rank INTEGER NOT NULL,
                total_pp REAL NOT NULL,
                rank_updated_at TEXT NOT NULL,
                scores_updated_at TEXT,
                score_metadata_version INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS farm_beatmaps(
                beatmap_id INTEGER PRIMARY KEY,
                beatmapset_id INTEGER NOT NULL,
                artist TEXT NOT NULL,
                title TEXT NOT NULL,
                difficulty TEXT NOT NULL,
                mapper TEXT NOT NULL,
                base_bpm REAL NOT NULL,
                hit_length_seconds INTEGER NOT NULL,
                total_length_seconds INTEGER NOT NULL,
                star_rating REAL NOT NULL,
                status TEXT NOT NULL,
                ranked_at TEXT,
                cover_url TEXT NOT NULL,
                circle_size REAL,
                approach_rate REAL,
                overall_difficulty REAL,
                drain_rate REAL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS farm_scores(
                score_id INTEGER PRIMARY KEY,
                user_id INTEGER NOT NULL REFERENCES farm_players(user_id) ON DELETE CASCADE,
                beatmap_id INTEGER NOT NULL REFERENCES farm_beatmaps(beatmap_id) ON DELETE CASCADE,
                pp REAL NOT NULL,
                accuracy REAL NOT NULL,
                miss_count INTEGER NOT NULL,
                max_combo INTEGER NOT NULL,
                is_full_combo INTEGER NOT NULL,
                ended_at TEXT NOT NULL,
                actual_mods_json TEXT NOT NULL,
                canonical_mod_signature TEXT NOT NULL,
                clock_rate REAL NOT NULL,
                score_origin TEXT NOT NULL DEFAULT 'unknown',
                legacy_score_id INTEGER,
                total_score INTEGER,
                legacy_total_score INTEGER,
                build_id INTEGER,
                source_type TEXT
            );
            CREATE TABLE IF NOT EXISTS farm_star_ratings(
                beatmap_id INTEGER NOT NULL REFERENCES farm_beatmaps(beatmap_id) ON DELETE CASCADE,
                mods_key TEXT NOT NULL,
                calculator_version TEXT NOT NULL,
                star_rating REAL NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(beatmap_id, mods_key, calculator_version)
            );
            CREATE TABLE IF NOT EXISTS farm_index_jobs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                minimum_rank INTEGER NOT NULL,
                maximum_rank INTEGER NOT NULL,
                status TEXT NOT NULL,
                cursor_json TEXT,
                players_total INTEGER NOT NULL DEFAULT 0,
                players_completed INTEGER NOT NULL DEFAULT 0,
                players_failed INTEGER NOT NULL DEFAULT 0,
                started_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS farm_index_job_players(
                job_id INTEGER NOT NULL REFERENCES farm_index_jobs(id) ON DELETE CASCADE,
                user_id INTEGER NOT NULL REFERENCES farm_players(user_id) ON DELETE CASCADE,
                global_rank INTEGER NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                PRIMARY KEY(job_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS farm_index_failures(
                job_id INTEGER NOT NULL REFERENCES farm_index_jobs(id) ON DELETE CASCADE,
                user_id INTEGER NOT NULL,
                error TEXT NOT NULL,
                failed_at TEXT NOT NULL,
                PRIMARY KEY(job_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS farm_ranking_snapshots(
                snapshot_id INTEGER PRIMARY KEY,
                minimum_rank INTEGER NOT NULL,
                maximum_rank INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS farm_ranking_snapshot_members(
                snapshot_id INTEGER NOT NULL REFERENCES farm_ranking_snapshots(snapshot_id) ON DELETE CASCADE,
                user_id INTEGER NOT NULL REFERENCES farm_players(user_id) ON DELETE CASCADE,
                global_rank INTEGER NOT NULL,
                total_pp REAL NOT NULL,
                PRIMARY KEY(snapshot_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS farm_country_coverage(
                snapshot_id INTEGER NOT NULL REFERENCES farm_ranking_snapshots(snapshot_id) ON DELETE CASCADE,
                country_code TEXT NOT NULL,
                covered_through_global_rank INTEGER NOT NULL,
                requested_maximum_rank INTEGER NOT NULL,
                is_complete INTEGER NOT NULL,
                hit_api_limit INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(snapshot_id, country_code)
            );
            CREATE INDEX IF NOT EXISTS idx_farm_players_rank ON farm_players(global_rank, user_id);
            CREATE INDEX IF NOT EXISTS idx_farm_players_scores_updated ON farm_players(scores_updated_at);
            CREATE INDEX IF NOT EXISTS idx_farm_scores_pp ON farm_scores(pp, user_id);
            CREATE INDEX IF NOT EXISTS idx_farm_scores_user ON farm_scores(user_id);
            CREATE INDEX IF NOT EXISTS idx_farm_scores_beatmap_mods ON farm_scores(beatmap_id, canonical_mod_signature, clock_rate);
            CREATE INDEX IF NOT EXISTS idx_farm_beatmaps_status_date ON farm_beatmaps(status, ranked_at);
            CREATE INDEX IF NOT EXISTS idx_farm_job_players_rank ON farm_index_job_players(job_id, global_rank);
            CREATE INDEX IF NOT EXISTS idx_farm_snapshot_members_rank
                ON farm_ranking_snapshot_members(snapshot_id, global_rank, user_id);
            CREATE INDEX IF NOT EXISTS idx_farm_country_coverage_gap
                ON farm_country_coverage(snapshot_id, is_complete, covered_through_global_rank);
            UPDATE farm_players
            SET scores_updated_at=NULL
            WHERE COALESCE((
                SELECT CAST(value AS INTEGER)
                FROM farm_metadata
                WHERE key='schema_version'
            ), 0) < 3;
            INSERT INTO farm_metadata(key, value) VALUES('schema_version', '6')
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, transaction, "farm_beatmaps", "circle_size", "REAL");
        EnsureColumn(connection, transaction, "farm_beatmaps", "approach_rate", "REAL");
        EnsureColumn(connection, transaction, "farm_beatmaps", "overall_difficulty", "REAL");
        EnsureColumn(connection, transaction, "farm_beatmaps", "drain_rate", "REAL");
        EnsureColumn(connection, transaction, "farm_players", "score_metadata_version", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "farm_scores", "score_origin", "TEXT NOT NULL DEFAULT 'unknown'");
        EnsureColumn(connection, transaction, "farm_scores", "legacy_score_id", "INTEGER");
        EnsureColumn(connection, transaction, "farm_scores", "total_score", "INTEGER");
        EnsureColumn(connection, transaction, "farm_scores", "legacy_total_score", "INTEGER");
        EnsureColumn(connection, transaction, "farm_scores", "build_id", "INTEGER");
        EnsureColumn(connection, transaction, "farm_scores", "source_type", "TEXT");
        using (var metadataIndex = connection.CreateCommand())
        {
            metadataIndex.Transaction = transaction;
            metadataIndex.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_farm_players_score_metadata
                ON farm_players(score_metadata_version, global_rank)
                """;
            metadataIndex.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static IndexJob ReadJob(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static FarmScoreOrigin ParseScoreOrigin(string value) =>
        Enum.TryParse<FarmScoreOrigin>(value, ignoreCase: true, out var origin)
            ? origin
            : FarmScoreOrigin.Unknown;

    private static void AddBeatmapParameters(SqliteCommand command, FarmBeatmap beatmap)
    {
        command.Parameters.AddWithValue("@beatmap_id", beatmap.BeatmapId);
        command.Parameters.AddWithValue("@beatmapset_id", beatmap.BeatmapSetId);
        command.Parameters.AddWithValue("@artist", beatmap.Artist);
        command.Parameters.AddWithValue("@title", beatmap.Title);
        command.Parameters.AddWithValue("@difficulty", beatmap.Difficulty);
        command.Parameters.AddWithValue("@mapper", beatmap.Mapper);
        command.Parameters.AddWithValue("@base_bpm", beatmap.BaseBpm);
        command.Parameters.AddWithValue("@hit_length", beatmap.HitLengthSeconds);
        command.Parameters.AddWithValue("@total_length", beatmap.TotalLengthSeconds);
        command.Parameters.AddWithValue("@stars", beatmap.StarRating);
        command.Parameters.AddWithValue("@status", beatmap.Status);
        command.Parameters.AddWithValue("@ranked_at", (object?)beatmap.RankedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("@cover_url", beatmap.CoverUrl);
        AddNullable(command, "@circle_size", beatmap.CircleSize);
        AddNullable(command, "@approach_rate", beatmap.ApproachRate);
        AddNullable(command, "@overall_difficulty", beatmap.OverallDifficulty);
        AddNullable(command, "@drain_rate", beatmap.DrainRate);
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private static void AppendSafeModPredicates(
        SqliteCommand command,
        ICollection<string> predicates,
        FarmFinderQuery query)
    {
        var index = 0;
        foreach (var filter in query.Mods)
        {
            if (filter.Requirement is ModRequirement.Ignore or ModRequirement.Wildcard)
                continue;

            var acronym = filter.Acronym.Trim().ToUpperInvariant();
            if (acronym is "" or "NM")
                continue;

            var family = query.TreatNightcoreAsDoubleTime && acronym is ("DT" or "NC")
                ? new[] { "DT", "NC" }
                : new[] { acronym };
            var alias = $"candidate_mod_{index}";
            var parameters = new string[family.Length];
            for (var familyIndex = 0; familyIndex < family.Length; familyIndex++)
            {
                var parameter = $"@candidate_mod_{index}_{familyIndex}";
                command.Parameters.AddWithValue(parameter, family[familyIndex]);
                parameters[familyIndex] = parameter;
            }

            var exists = $"""
                EXISTS (
                    SELECT 1
                    FROM json_each(s.actual_mods_json) AS {alias}
                    WHERE upper(COALESCE(json_extract({alias}.value, '$.acronym'), ''))
                          IN ({string.Join(", ", parameters)})
                )
                """;
            predicates.Add(filter.Requirement == ModRequirement.Excluded
                ? $"NOT ({exists})"
                : exists);
            index++;
        }
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
