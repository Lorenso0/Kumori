using System.Globalization;
using System.IO;
using System.Text.Json;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;

namespace Kumori.App;

/// <summary>
/// Repairs recent attempts whose persisted client replay appeared after the
/// live recovery window or while Kumori was closed.
/// </summary>
public sealed class PersistedReplayReconciliationService
{
    private readonly SqliteConnectionFactory factory;
    private readonly MovementCaptureStore movement;
    private readonly MovementRepository movementRepository;
    private readonly Action<long>? movementReplaced;

    public PersistedReplayReconciliationService(SqliteConnectionFactory factory, Action<long>? movementReplaced = null)
    {
        this.factory = factory;
        movement = new MovementCaptureStore(factory);
        movementRepository = new MovementRepository(factory);
        this.movementReplaced = movementReplaced;
    }

    public void Run(CancellationToken cancellationToken = default)
    {
        foreach (Candidate candidate in LoadCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (candidate.ClientKind.Equals("stable", StringComparison.OrdinalIgnoreCase))
                    ReconcileStable(candidate, cancellationToken);
                else if (candidate.ClientKind.Equals("lazer", StringComparison.OrdinalIgnoreCase))
                    ReconcileLazer(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Persisted replay reconciliation failed for attempt {AttemptId}", candidate.AttemptId);
            }
        }
    }

    private void ReconcileStable(Candidate candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.GameFolder)
            || string.IsNullOrWhiteSpace(candidate.BeatmapPath)
            || !File.Exists(candidate.BeatmapPath))
            return;
        string gameFolder = candidate.GameFolder;
        string beatmapPath = candidate.BeatmapPath;

        DateTimeOffset earliest = candidate.StartedAt.AddSeconds(-10);
        DateTimeOffset latest = candidate.EndedAt.AddMinutes(5);
        foreach (string replay in ReplayFiles(gameFolder)
                     .Select(path => (Path: path, Time: ReplayFileTime(path)))
                     .Where(file => file.Time >= earliest && file.Time <= latest)
                     .OrderBy(file => Math.Abs((file.Time - candidate.EndedAt).TotalMilliseconds))
                     .Select(file => file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StableReplayFrameRecoverySink.TryRead(replay, beatmapPath, candidate.Checksum, out var samples))
                continue;
            var existing = movementRepository.GetMetadata(candidate.AttemptId);
            if (existing is { SampleCount: > 0 } && existing.Source is "stable_memory" or "stable_live")
            {
                var memorySamples = movementRepository.GetSamples(candidate.AttemptId);
                StableReplayComparisonResult comparison = StableReplayComparisonArchive.Save(
                    candidate.AttemptId, memorySamples, samples, replay, candidate.Checksum);
                StableReplayFrameDiagnostics.Update(status =>
                {
                    status.ComparisonReportPath = comparison.ReportPath;
                    status.ComparisonSummary = comparison.Summary;
                });
            }
            Store(candidate.AttemptId, samples, "stable_replay", replay, "Data/r reconciliation");
            return;
        }
    }

    private void ReconcileLazer(Candidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var beatmap = LazerStorage.ResolveBeatmapAssets(candidate.BeatmapId, candidate.BeatmapSetId, candidate.Difficulty);
        if (beatmap is null)
            return;
        string? replay = LazerStorage.ResolveReplayFile(candidate.Checksum, candidate.StartedAt, candidate.GameFolder, candidate.EndedAt);
        if (replay is null || !StableReplayFrameRecoverySink.TryRead(replay, beatmap.BeatmapPath, candidate.Checksum, out var samples))
            return;
        Store(candidate.AttemptId, samples, "lazer_replay", replay, "client.realm reconciliation");
    }

    private void Store(long attemptId, IReadOnlyList<Kumori.Core.Models.MovementSample> samples, string source, string replayPath, string origin)
    {
        movement.Start(attemptId);
        movement.AddSamples(samples);
        movement.Complete(0, source, JsonSerializer.Serialize(new
        {
            source,
            replay_exact = true,
            replay_path = replayPath,
            origin,
        }));
        movementReplaced?.Invoke(attemptId);
        Log.Information("Reconciled {Count} persisted replay frames for attempt {AttemptId} from {Origin}", samples.Count, attemptId, origin);
    }

    private IReadOnlyList<Candidate> LoadCandidates()
    {
        if (!factory.DatabaseExists)
            return [];
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.started_at, COALESCE(a.ended_at, a.started_at),
                   COALESCE(b.checksum, ''), COALESCE(b.beatmap_id, 0),
                   COALESCE(b.set_id, 0), COALESCE(b.difficulty, ''),
                   c.source_json, COALESCE(m.source, '')
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            JOIN attempt_context c ON c.attempt_id = a.id
            LEFT JOIN attempt_movement m ON m.attempt_id = a.id
            WHERE a.outcome <> 'active'
              AND a.started_at >= @cutoff
              AND COALESCE(m.source, '') IN ('', 'stable_memory', 'stable_live', 'lazer_memory', 'lazer_replay_frame')
            ORDER BY a.id DESC
            LIMIT 200
            """;
        command.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-14).ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var result = new List<Candidate>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                || !DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ended))
                continue;
            try
            {
                using var source = JsonDocument.Parse(reader.GetString(7));
                string client = Property(source.RootElement, "client_kind") ?? "unknown";
                string? beatmapPath = Property(source.RootElement, "beatmap_path");
                string? gameFolder = Property(source.RootElement, "game_folder");
                string checksum = reader.GetString(3);
                if (string.IsNullOrWhiteSpace(checksum)) continue;
                result.Add(new Candidate(reader.GetInt64(0), started, ended, checksum,
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), client,
                    beatmapPath, gameFolder));
            }
            catch (JsonException) { }
        }
        return result;
    }

    internal static IEnumerable<string> ReplayFiles(string gameFolder)
    {
        foreach (string directory in new[] { Path.Combine(gameFolder, "Data", "r"), Path.Combine(gameFolder, "Replays") })
        {
            if (!Directory.Exists(directory)) continue;
            var pattern = directory.EndsWith(Path.Combine("Data", "r"), StringComparison.OrdinalIgnoreCase)
                ? "*"
                : "*.osr";
            foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                yield return file;
        }
    }

    public static DateTimeOffset ReplayFileTime(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int separator = name.LastIndexOf('-');
        if (separator >= 0 && long.TryParse(name[(separator + 1)..], out long fileTime))
        {
            try { return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { }
        }
        return File.GetLastWriteTimeUtc(path);
    }

    private static string? Property(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Candidate(long AttemptId, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
        string Checksum, long BeatmapId, long BeatmapSetId, string Difficulty, string ClientKind,
        string? BeatmapPath, string? GameFolder);
}
