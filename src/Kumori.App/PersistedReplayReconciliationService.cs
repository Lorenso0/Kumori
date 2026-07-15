using System.Globalization;
using System.IO;
using System.Text.Json;
using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;

namespace Kumori.App;

/// <summary>
/// Repairs recent attempts whose persisted client replay appeared after the
/// live recovery window or while Kumori was closed.
/// </summary>
internal sealed class PersistedReplayReconciliationService
{
    private const int PersistenceChunkSize = 4096;
    private readonly SqliteConnectionFactory factory;
    private readonly MovementCaptureStore movement;
    private readonly MovementRepository movementRepository;
    private readonly ReplayResultRecoveryStore resultRecovery;
    private readonly Action<long>? movementReplaced;
    private readonly Action<ReplayResultRecoveryContext>? resultRecovered;
    private readonly bool recoverMovement;

    public PersistedReplayReconciliationService(
        SqliteConnectionFactory factory,
        Action<long>? movementReplaced = null,
        Action<ReplayResultRecoveryContext>? resultRecovered = null,
        bool recoverMovement = true)
    {
        this.factory = factory;
        movement = new MovementCaptureStore(factory);
        movementRepository = new MovementRepository(factory);
        resultRecovery = new ReplayResultRecoveryStore(factory);
        this.movementReplaced = movementReplaced;
        this.resultRecovered = resultRecovered;
        this.recoverMovement = recoverMovement;
    }

    public void Run(CancellationToken cancellationToken = default)
    {
        foreach (Candidate candidate in LoadCandidates(cancellationToken))
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
        if (LazerReplayFrameRecoverySink.IsPartialOutcome(candidate.Outcome)
            || string.IsNullOrWhiteSpace(candidate.Checksum))
        {
            ReconcileRetainedStableCapture(candidate, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(candidate.GameFolder)
            || string.IsNullOrWhiteSpace(candidate.BeatmapPath)
            || !File.Exists(candidate.BeatmapPath))
            return;
        string gameFolder = candidate.GameFolder;
        string beatmapPath = candidate.BeatmapPath;

        DateTimeOffset earliest = candidate.StartedAt.AddSeconds(-10);
        DateTimeOffset latest = candidate.EndedAt.AddMinutes(5);
        foreach (string replay in ReplayFiles(gameFolder)
                     .Select(path =>
                     {
                         cancellationToken.ThrowIfCancellationRequested();
                         return (Path: path, Time: ReplayFileTime(path));
                     })
                     .Where(file => file.Time >= earliest && file.Time <= latest)
                     .OrderBy(file => Math.Abs((file.Time - candidate.EndedAt).TotalMilliseconds))
                     .Select(file => file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StableReplayFrameRecoverySink.TryRead(
                    replay,
                    beatmapPath,
                    candidate.Checksum,
                    out var samples,
                    out var replayResult,
                    cancellationToken))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            var existing = movementRepository.GetMetadata(candidate.AttemptId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (existing is { SampleCount: > 0 } && existing.Source is "stable_memory" or "stable_live")
            {
                var memorySamples = movementRepository.GetSamples(candidate.AttemptId, cancellationToken);
                StableReplayComparisonResult comparison = StableReplayComparisonArchive.Save(
                    candidate.AttemptId,
                    memorySamples,
                    samples,
                    replay,
                    candidate.Checksum,
                    cancellationToken: cancellationToken);
                StableReplayFrameDiagnostics.Update(status =>
                {
                    status.ComparisonReportPath = comparison.ReportPath;
                    status.ComparisonSummary = comparison.Summary;
                });
            }
            ReplayResultRecoveryOutcome recovery = resultRecovery.Apply(
                candidate.AttemptId,
                replayResult,
                "stable_replay_reconciliation",
                cancellationToken);
            var requiresSimulation = candidate.NeedsResultResimulation || !candidate.NeedsAccuracyAuthorityRepair;
            var resultNotified = false;
            if (recovery.Applied)
            {
                // Apply is already committed and intentionally does not observe
                // cancellation afterward. Notify immediately so the mandatory
                // tosu restart cannot be lost if gameplay interrupts optional
                // comparison/movement work next.
                resultRecovered?.Invoke(new ReplayResultRecoveryContext(
                    candidate.AttemptId,
                    recovery,
                    replay,
                    beatmapPath,
                    Path.GetDirectoryName(beatmapPath),
                    null,
                    samples,
                    requiresSimulation));
                resultNotified = true;
            }
            if (recoverMovement
                && !candidate.MovementSource.Equals("stable_replay", StringComparison.OrdinalIgnoreCase))
                Store(candidate.AttemptId, samples, "stable_replay", replay, "Data/r reconciliation", cancellationToken);
            if (!resultNotified && candidate.NeedsResultResimulation)
            {
                resultRecovered?.Invoke(new ReplayResultRecoveryContext(
                    candidate.AttemptId,
                    recovery,
                    replay,
                    beatmapPath,
                    Path.GetDirectoryName(beatmapPath),
                    null,
                    samples,
                    requiresSimulation));
            }
            return;
        }
    }

    private void ReconcileRetainedStableCapture(Candidate candidate, CancellationToken cancellationToken)
    {
        if (!candidate.NeedsPartialCaptureSimulation
            || candidate.MovementSource is not ("stable_memory" or "stable_live")
            || string.IsNullOrWhiteSpace(candidate.BeatmapPath)
            || !File.Exists(candidate.BeatmapPath))
            return;

        NotifyPartialSimulation(
            candidate,
            candidate.BeatmapPath,
            Path.GetDirectoryName(candidate.BeatmapPath),
            null,
            cancellationToken);
    }

    private void ReconcileLazer(Candidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var beatmap = LazerStorage.ResolveBeatmapAssets(candidate.BeatmapId, candidate.BeatmapSetId, candidate.Difficulty);
        if (beatmap is null)
            return;
        if (LazerReplayFrameRecoverySink.IsPartialOutcome(candidate.Outcome)
            || string.IsNullOrWhiteSpace(candidate.Checksum))
        {
            ReconcileRetainedLazerCapture(candidate, beatmap, cancellationToken);
            return;
        }
        foreach (string replay in LazerStorage.ResolveReplayFiles(candidate.Checksum, candidate.StartedAt, candidate.GameFolder, candidate.EndedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StableReplayFrameRecoverySink.TryRead(
                    replay,
                    beatmap.BeatmapPath,
                    candidate.Checksum,
                    out var samples,
                    out var replayResult,
                    cancellationToken))
                continue;
            ReplayResultRecoveryOutcome recovery = resultRecovery.Apply(
                candidate.AttemptId,
                replayResult,
                "lazer_replay_reconciliation",
                cancellationToken);
            var requiresSimulation = candidate.NeedsResultResimulation || !candidate.NeedsAccuracyAuthorityRepair;
            var resultNotified = false;
            if (recovery.Applied)
            {
                resultRecovered?.Invoke(new ReplayResultRecoveryContext(
                    candidate.AttemptId,
                    recovery,
                    replay,
                    beatmap.BeatmapPath,
                    null,
                    beatmap.Files,
                    samples,
                    requiresSimulation));
                resultNotified = true;
            }
            if (recoverMovement
                && !candidate.MovementSource.Equals("lazer_replay", StringComparison.OrdinalIgnoreCase))
                Store(candidate.AttemptId, samples, "lazer_replay", replay, "client.realm reconciliation", cancellationToken);
            if (!resultNotified && candidate.NeedsResultResimulation)
            {
                resultRecovered?.Invoke(new ReplayResultRecoveryContext(
                    candidate.AttemptId,
                    recovery,
                    replay,
                    beatmap.BeatmapPath,
                    null,
                    beatmap.Files,
                    samples,
                    requiresSimulation));
            }
            return;
        }
    }

    private void ReconcileRetainedLazerCapture(
        Candidate candidate,
        LazerBeatmapAssets beatmap,
        CancellationToken cancellationToken)
    {
        if (!candidate.NeedsPartialCaptureSimulation
            || candidate.MovementSource is not ("lazer_memory" or "lazer_replay_frame"))
            return;

        NotifyPartialSimulation(candidate, beatmap.BeatmapPath, null, beatmap.Files, cancellationToken);
    }

    private void NotifyPartialSimulation(
        Candidate candidate,
        string beatmapPath,
        string? mediaDirectory,
        IReadOnlyDictionary<string, string>? mediaPaths,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MovementSample> retained = movementRepository.GetSamples(candidate.AttemptId, cancellationToken);
        if (retained.Count == 0)
            return;

        resultRecovered?.Invoke(new ReplayResultRecoveryContext(
            candidate.AttemptId,
            ReplayResultRecoveryOutcome.NoChanges,
            null,
            beatmapPath,
            mediaDirectory,
            mediaPaths,
            retained,
            RequiresSimulation: true,
            RequiresTosuRestart: false,
            SimulationOwnsCoreResult: candidate.PartialTosuResultWasMissing,
            TosuResultWasMissing: candidate.PartialTosuResultWasMissing));
        Log.Information(
            "Queued startup retained-frame simulation from {Count} {Source} frames for attempt {AttemptId}",
            retained.Count,
            candidate.MovementSource,
            candidate.AttemptId);
    }

    private void Store(
        long attemptId,
        IReadOnlyList<MovementSample> samples,
        string source,
        string replayPath,
        string origin,
        CancellationToken cancellationToken)
    {
        movement.Start(attemptId);
        MovementSample[] sampleArray;
        if (samples is MovementSample[] array)
        {
            sampleArray = array;
        }
        else
        {
            sampleArray = new MovementSample[samples.Count];
            for (var index = 0; index < samples.Count; index++)
            {
                if ((index & (PersistenceChunkSize - 1)) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                sampleArray[index] = samples[index];
            }
        }
        for (var offset = 0; offset < sampleArray.Length; offset += PersistenceChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(PersistenceChunkSize, sampleArray.Length - offset);
            movement.AddSamples(new ArraySegment<MovementSample>(
                sampleArray,
                offset,
                count));
        }
        movement.Complete(0, source, JsonSerializer.Serialize(new
        {
            source,
            replay_exact = true,
            replay_path = replayPath,
            origin,
        }), cancellationToken);
        movementReplaced?.Invoke(attemptId);
        Log.Information("Reconciled {Count} persisted replay frames for attempt {AttemptId} from {Origin}", samples.Count, attemptId, origin);
    }

    private IReadOnlyList<Candidate> LoadCandidates(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!factory.DatabaseExists)
            return [];
        try
        {
            using var connection = factory.Open();
            if (cancellationToken.CanBeCanceled)
                connection.DefaultTimeout = 1;
            cancellationToken.ThrowIfCancellationRequested();
            using var interruptRegistration = cancellationToken.Register(
                static state => SQLitePCL.raw.sqlite3_interrupt(((Microsoft.Data.Sqlite.SqliteConnection)state!).Handle),
                connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT a.id, a.started_at, COALESCE(a.ended_at, a.started_at),
                   COALESCE(b.checksum, ''), COALESCE(b.beatmap_id, 0),
                   COALESCE(b.set_id, 0), COALESCE(b.difficulty, ''),
                   c.source_json, COALESCE(m.source, ''),
                   a.accuracy, a.n100, a.n50, a.misses,
                   a.outcome, a.score, a.combo, a.n300,
                   COALESCE((SELECT hit_count FROM attempt_timing t WHERE t.attempt_id=a.id), 0)
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            JOIN attempt_context c ON c.attempt_id = a.id
            LEFT JOIN attempt_movement m ON m.attempt_id = a.id
            WHERE a.outcome <> 'active'
              AND a.started_at >= @cutoff
            ORDER BY a.id DESC
            LIMIT 1000
            """;
            command.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-14).ToString("O", CultureInfo.InvariantCulture));
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            var result = new List<Candidate>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                if (!DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                || !DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ended))
                    continue;
                try
                {
                    using var source = JsonDocument.Parse(reader.GetString(7));
                    string client = Property(source.RootElement, "client_kind") ?? "unknown";
                    string? beatmapPath = Property(source.RootElement, "beatmap_path");
                    string? gameFolder = Property(source.RootElement, "game_folder");
                    string movementSource = reader.GetString(8);
                    bool needsResultResimulation = NeedsCurrentResultSimulation(source.RootElement);
                    bool needsAccuracyAuthorityRepair = ReplayResultRecoveryStore.NeedsAccuracyAuthorityRepair(
                        source.RootElement,
                        reader.GetDouble(9),
                        reader.GetInt32(10),
                        reader.GetInt32(11),
                        reader.GetInt32(12));
                    long score = reader.GetInt64(14);
                    int combo = reader.GetInt32(15);
                    int n300 = reader.GetInt32(16);
                    int n100 = reader.GetInt32(10);
                    int n50 = reader.GetInt32(11);
                    int misses = reader.GetInt32(12);
                    int timingCount = reader.GetInt32(17);
                    string checksum = reader.GetString(3);
                    PartialSimulationDecision partial = DecideRetainedCaptureSimulation(
                        reader.GetString(13), movementSource, checksum, score, combo,
                        n300, n100, n50, misses, timingCount, source.RootElement);
                    bool needsPartialCaptureSimulation = partial.ShouldSimulate;
                    bool partialTosuResultWasMissing = partial.SimulationOwnsCoreResult;
                    bool needsMovementRecovery = movementSource is "" or "stable_memory" or "stable_live" or "lazer_memory" or "lazer_replay_frame";
                    if (!needsMovementRecovery && !needsResultResimulation && !needsAccuracyAuthorityRepair
                        && !needsPartialCaptureSimulation)
                        continue;
                    // Persisted replay matching requires a checksum. Partial
                    // and otherwise checksum-less retained-frame simulation do not.
                    if (string.IsNullOrWhiteSpace(checksum) && !needsPartialCaptureSimulation) continue;
                    result.Add(new Candidate(reader.GetInt64(0), started, ended, checksum,
                        reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), client,
                        reader.GetString(13), beatmapPath, gameFolder, movementSource, needsResultResimulation,
                        needsAccuracyAuthorityRepair, needsPartialCaptureSimulation, partialTosuResultWasMissing));
                }
                catch (JsonException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Persisted replay candidate loading was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
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

    internal static bool NeedsCurrentResultSimulation(JsonElement source)
    {
        if (!source.TryGetProperty("result_recovery", out var recovery))
            return false;

        if (recovery.TryGetProperty("simulation_schema", out var schema)
            && schema.TryGetInt32(out int version))
            return version < ReplayResultRecoveryStore.CurrentSimulationSchema;

        return recovery.TryGetProperty("reason", out var reason)
               && string.Equals(reason.GetString(), "tosu_gameplay_values_missing", StringComparison.Ordinal);
    }

    internal static bool HasCurrentResultSimulation(JsonElement source)
        => source.TryGetProperty("result_recovery", out var recovery)
           && recovery.TryGetProperty("simulation_schema", out var schema)
           && schema.TryGetInt32(out int version)
           && version >= ReplayResultRecoveryStore.CurrentSimulationSchema;

    internal static bool ResultWasMissing(JsonElement source)
        => source.TryGetProperty("result_recovery", out var recovery)
           && recovery.TryGetProperty("reason", out var reason)
           && string.Equals(reason.GetString(), "tosu_gameplay_values_missing", StringComparison.Ordinal);

    internal static PartialSimulationDecision DecidePartialSimulation(
        string outcome,
        string movementSource,
        long score,
        int combo,
        int n300,
        int n100,
        int n50,
        int misses,
        int timingCount,
        JsonElement source)
    {
        return DecideRetainedCaptureSimulation(
            outcome, movementSource, "checksum-present", score, combo,
            n300, n100, n50, misses, timingCount, source);
    }

    internal static PartialSimulationDecision DecideRetainedCaptureSimulation(
        string outcome,
        string movementSource,
        string checksum,
        long score,
        int combo,
        int n300,
        int n100,
        int n50,
        int misses,
        int timingCount,
        JsonElement source)
    {
        bool cannotUsePersistedReplay = LazerReplayFrameRecoverySink.IsPartialOutcome(outcome)
                                        || string.IsNullOrWhiteSpace(checksum);
        bool retainedReplayFrames = movementSource is "lazer_memory" or "lazer_replay_frame"
            or "stable_memory" or "stable_live";
        int coreTotal = n300 + n100 + n50 + misses;
        bool hasGameplayEvidence = score > 0 || combo > 0 || coreTotal > 0 || timingCount > 0;
        bool resultWasMissing = ResultWasMissing(source)
                                || score == 0 && combo == 0 && coreTotal == 0 && timingCount > 0;
        bool shouldSimulate = cannotUsePersistedReplay
                              && retainedReplayFrames
                              && hasGameplayEvidence
                              && !HasCurrentResultSimulation(source);
        return new PartialSimulationDecision(shouldSimulate, shouldSimulate && resultWasMissing);
    }

    private sealed record Candidate(long AttemptId, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
        string Checksum, long BeatmapId, long BeatmapSetId, string Difficulty, string ClientKind,
        string Outcome, string? BeatmapPath, string? GameFolder, string MovementSource, bool NeedsResultResimulation,
        bool NeedsAccuracyAuthorityRepair, bool NeedsPartialCaptureSimulation, bool PartialTosuResultWasMissing);

    internal readonly record struct PartialSimulationDecision(bool ShouldSimulate, bool SimulationOwnsCoreResult);
}
