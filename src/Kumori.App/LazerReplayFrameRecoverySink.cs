using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Kumori.Native;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Kumori.App;

/// <summary>Recovers lazer results from a persisted replay or retained live frames.</summary>
internal sealed class LazerReplayFrameRecoverySink : IAttemptSink
{
    private const int SearchPassCount = 240;
    private const int MaxCandidatesPerPass = 8;
    private const int PersistenceChunkSize = 4096;
    private static readonly TimeSpan SearchRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly Func<long?> attemptId;
    private readonly MovementCaptureStore movement;
    private readonly MovementRepository repository;
    private readonly ReplayResultRecoveryStore resultRecovery;
    private readonly Action<long>? movementReplaced;
    private readonly Action<ReplayResultRecoveryContext>? resultRecovered;
    private readonly Action<long>? resultTelemetryMissing;
    private readonly bool recoverMovement;
    private readonly CancellationToken cancellationToken;
    private readonly GameplayWorkCoordinator? workCoordinator;
    private readonly ConcurrentDictionary<long, byte> activeRecoveries = new();
    private AttemptStart? start;
    private bool sawGameplayResult;
    private long generation;

    public LazerReplayFrameRecoverySink(
        SqliteConnectionFactory factory,
        Func<long?> attemptId,
        Action<long>? movementReplaced = null,
        Action<ReplayResultRecoveryContext>? resultRecovered = null,
        Action<long>? resultTelemetryMissing = null,
        bool recoverMovement = true,
        CancellationToken cancellationToken = default,
        GameplayWorkCoordinator? workCoordinator = null)
    {
        this.attemptId = attemptId;
        movement = new MovementCaptureStore(factory);
        repository = new MovementRepository(factory);
        resultRecovery = new ReplayResultRecoveryStore(factory);
        this.movementReplaced = movementReplaced;
        this.resultRecovered = resultRecovered;
        this.resultTelemetryMissing = resultTelemetryMissing;
        this.recoverMovement = recoverMovement;
        this.cancellationToken = cancellationToken;
        this.workCoordinator = workCoordinator;
    }

    public void StartAttempt(AttemptStart value)
    {
        sawGameplayResult = false;
        start = value.ClientKind == OsuClientKind.Lazer ? value : null;
        if (start is not null)
        {
            Interlocked.Increment(ref generation);
            if (recoverMovement)
            {
                LazerReplayFrameDiagnostics.Update(status =>
                {
                    status.LocalReplayState = "armed";
                    status.LocalReplayPath = null;
                    status.LocalReplayFrames = 0;
                    status.LocalReplayError = null;
                });
            }
        }
    }
    public void Checkpoint(AttemptCheckpoint checkpoint)
        => sawGameplayResult |= HasGameplayResult(checkpoint);

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        start = null;
        sawGameplayResult = false;
    }

    public void Finalize(AttemptFinalization finalization)
    {
        var capturedStart = start;
        var id = attemptId();
        bool capturedGameplayResult = sawGameplayResult;
        long capturedGeneration = Volatile.Read(ref generation);
        start = null;
        sawGameplayResult = false;
        if (capturedStart is null || id is null) return;
        bool tosuResultWasMissing = HasMissingTosuResult(finalization, capturedGameplayResult);
        if (tosuResultWasMissing)
        {
            Log.Warning(
                "Detected missing tosu result telemetry for attempt {AttemptId}; restarting tosu while replay recovery continues",
                id.Value);
            resultTelemetryMissing?.Invoke(id.Value);
        }
        var endedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(finalization.Snapshot.WallTime * 1000));
        if (!activeRecoveries.TryAdd(id.Value, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverAsync(
                    capturedStart,
                    finalization,
                    id.Value,
                    capturedGeneration,
                    endedAt,
                    tosuResultWasMissing,
                    simulationOwnsCoreResult: tosuResultWasMissing && !capturedGameplayResult).ConfigureAwait(false);
            }
            finally
            {
                activeRecoveries.TryRemove(id.Value, out _);
            }
        }, CancellationToken.None);
    }

    internal static bool HasMissingTosuResult(
        AttemptFinalization finalization,
        bool priorGameplayResult = false)
    {
        AttemptSnapshot snapshot = finalization.Snapshot;
        return !finalization.Outcome.Equals("active", StringComparison.OrdinalIgnoreCase)
               && (snapshot.TimingOffsets.Count > 0 || priorGameplayResult)
               && snapshot.Score == 0
               && snapshot.Combo <= 0
               && snapshot.N300 <= 0
               && snapshot.N100 <= 0
               && snapshot.N50 <= 0
               && snapshot.Misses <= 0;
    }

    internal static bool HasGameplayResult(AttemptCheckpoint checkpoint)
    {
        AttemptSnapshot snapshot = checkpoint.Snapshot;
        if (snapshot.Score > 0
            || snapshot.Combo > 0
            || snapshot.N300 + snapshot.N100 + snapshot.N50 + snapshot.Misses > 0
            || snapshot.TimingOffsets.Count > 0)
            return true;

        foreach (JudgementCapture.CapturedEvent capturedEvent in checkpoint.Events)
        {
            if (!capturedEvent.EventType.Equals("checkpoint", StringComparison.Ordinal))
                continue;
            try
            {
                using var document = JsonDocument.Parse(capturedEvent.DataJson);
                JsonElement root = document.RootElement;
                if (Count("n300") + Count("n100") + Count("n50") + Count("misses") > 0)
                    return true;

                int Count(string name)
                    => root.TryGetProperty(name, out JsonElement value)
                       && value.TryGetDouble(out double parsed)
                        ? Math.Max(0, (int)Math.Round(parsed))
                        : 0;
            }
            catch (JsonException)
            {
            }
        }
        return false;
    }

    private async Task RecoverAsync(
        AttemptStart attempt,
        AttemptFinalization finalization,
        long id,
        long capturedGeneration,
        DateTimeOffset endedAt,
        bool tosuResultWasMissing,
        bool simulationOwnsCoreResult)
    {
        try
        {
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(attempt.WallTime * 1000));
            var state = new RecoveryState();

            Task<bool> RunPass(int pass, CancellationToken token)
                => RecoverOnePassAsync(
                    attempt,
                    finalization,
                    id,
                    capturedGeneration,
                    startedAt,
                    endedAt,
                    state,
                    tosuResultWasMissing,
                    simulationOwnsCoreResult,
                    pass == SearchPassCount - 1,
                    token);

            if (workCoordinator is not null)
            {
                await workCoordinator.RunFairRetryLoopAsync(
                    $"lazer-replay-recovery-{id}",
                    SearchPassCount,
                    SearchRetryDelay,
                    RunPass,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunLocalRetryLoopAsync(RunPass, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown abandons optional replay recovery.
        }
        catch (Exception ex)
        {
            UpdateIfCurrent(capturedGeneration, status =>
            {
                status.LocalReplayState = "error";
                status.LocalReplayError = ex.Message;
            });
            Log.Warning(ex, "Lazer Realm replay recovery failed for attempt {AttemptId}", id);
        }
    }

    private Task<bool> RecoverOnePassAsync(
        AttemptStart attempt,
        AttemptFinalization finalization,
        long id,
        long capturedGeneration,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        RecoveryState state,
        bool tosuResultWasMissing,
        bool simulationOwnsCoreResult,
        bool isLastPass,
        CancellationToken operationToken)
    {
        operationToken.ThrowIfCancellationRequested();
        var beatmap = state.Beatmap;
        if (beatmap is null)
        {
            beatmap = LazerStorage.ResolveBeatmapAssets(
                attempt.BeatmapId,
                attempt.BeatmapSetId,
                attempt.Difficulty,
                attempt.GameFolder);
            state.Beatmap = beatmap;
            if (beatmap is null)
                return Task.FromResult(true);
        }

        var matched = state.Matched;
        if (matched is null && !IsPartialOutcome(finalization.Outcome))
        {
            var candidatesChecked = 0;
            IEnumerable<string> replayFiles = string.IsNullOrWhiteSpace(attempt.Checksum)
                ? []
                : LazerStorage.ResolveReplayFiles(
                    attempt.Checksum,
                    startedAt,
                    attempt.GameFolder,
                    endedAt);
            foreach (var replay in replayFiles)
            {
                operationToken.ThrowIfCancellationRequested();
                if (!TryGetReplayVersion(replay, out var version) ||
                    state.CheckedVersions.TryGetValue(replay, out var checkedVersion) && checkedVersion == version)
                    continue;
                if (version.Length is <= 0 or > StableReplayFrameRecoverySink.MaximumReplayFileBytes)
                {
                    state.CheckedVersions[replay] = version;
                    continue;
                }
                if (candidatesChecked++ >= MaxCandidatesPerPass)
                    break;

                if (StableReplayFrameRecoverySink.TryRead(
                        replay,
                        beatmap.BeatmapPath,
                        attempt.Checksum,
                        out var samples,
                        out var replayResult,
                        operationToken))
                {
                    matched = new RecoveredReplay(
                        replay,
                        samples as MovementSample[] ?? samples.ToArray(),
                        replayResult);
                    state.Matched = matched;
                    break;
                }

                // Retry a partial/invalid replay only after it changes.
                state.CheckedVersions[replay] = version;
            }
        }

        if (matched is null)
        {
            if ((IsPartialOutcome(finalization.Outcome)
                 || string.IsNullOrWhiteSpace(attempt.Checksum)
                 || tosuResultWasMissing && isLastPass)
                && TryNotifyRetainedSimulation(
                    id,
                    beatmap,
                    state,
                    tosuResultWasMissing,
                    simulationOwnsCoreResult,
                    operationToken))
            {
                UpdateIfCurrent(capturedGeneration, status =>
                {
                    status.LocalReplayState = "existing_capture_preserved";
                    status.LocalReplayError = null;
                });
                return Task.FromResult(true);
            }

            UpdateIfCurrent(capturedGeneration, status => status.LocalReplayState = "waiting");
            if (!isLastPass)
                return Task.FromResult(false);
            if (operationToken.IsCancellationRequested)
                return Task.FromResult(false);

            CompleteNotFound(id, capturedGeneration, operationToken);
            return Task.FromResult(true);
        }

        var interruptedAfterApply = false;
        if (state.Recovery is null)
        {
            operationToken.ThrowIfCancellationRequested();
            var recovery = resultRecovery.Apply(
                id,
                matched.Result,
                "lazer_replay",
                operationToken);
            interruptedAfterApply = operationToken.IsCancellationRequested;
            if (!recovery.AttemptReady && !isLastPass)
                return Task.FromResult(false);
            state.Recovery = recovery;
        }

        if (state.Recovery.Applied && !state.ResultRecoveryNotified)
        {
            // Result recovery is already committed. Notify immediately so the
            // mandatory tosu restart survives cancellation of optional movement
            // replacement when a new play starts.
            NotifyResultRecovery(
                state.Recovery,
                id,
                matched.ReplayPath,
                beatmap.BeatmapPath,
                beatmap.Files,
                matched.Samples);
            state.ResultRecoveryNotified = true;
        }
        if (interruptedAfterApply || operationToken.IsCancellationRequested)
            return Task.FromResult(false);

        if (!recoverMovement)
            return Task.FromResult(true);

        if (state.MovementCommitted)
            return Task.FromResult(true);

        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        var existing = repository.GetMetadata(id, operationToken);
        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        if (existing is { SampleCount: > 0 } && existing.Source is not ("lazer_memory" or "lazer_replay_frame" or "lazer_replay"))
            return Task.FromResult(true);

        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        movement.Start(id);
        for (var offset = 0; offset < matched.Samples.Length; offset += PersistenceChunkSize)
        {
            if (operationToken.IsCancellationRequested)
                return Task.FromResult(false);
            int count = Math.Min(PersistenceChunkSize, matched.Samples.Length - offset);
            movement.AddSamples(new ArraySegment<MovementSample>(matched.Samples, offset, count));
        }
        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        movement.Complete(0, "lazer_replay", JsonSerializer.Serialize(new
        {
            source = "lazer_replay",
            replay_exact = true,
            origin = "client.realm",
            replay_path = matched.ReplayPath,
        }), operationToken);
        state.MovementCommitted = true;
        UpdateIfCurrent(capturedGeneration, status =>
        {
            status.LocalReplayState = "stored";
            status.LocalReplayPath = matched.ReplayPath;
            status.LocalReplayFrames = matched.Samples.Length;
            status.LocalReplayError = null;
        });
        Log.Information(
            "Replaced lazer memory capture with {Count} persisted Realm replay frames for attempt {AttemptId}",
            matched.Samples.Length,
            id);
        movementReplaced?.Invoke(id);
        return Task.FromResult(true);
    }

    private async Task<bool> RunLocalRetryLoopAsync(
        Func<int, CancellationToken, Task<bool>> attempt,
        CancellationToken operationToken)
    {
        for (var pass = 0; pass < SearchPassCount; pass++)
        {
            if (await attempt(pass, operationToken).ConfigureAwait(false))
                return true;
            if (pass + 1 < SearchPassCount)
                await Task.Delay(SearchRetryDelay, operationToken).ConfigureAwait(false);
        }
        return false;
    }

    private void CompleteNotFound(
        long id,
        long capturedGeneration,
        CancellationToken operationToken)
    {
        var retained = repository.GetMetadata(id, operationToken);
        if (retained is { SampleCount: > 0 } && retained.Source is "lazer_memory" or "lazer_replay_frame")
        {
            UpdateIfCurrent(capturedGeneration, status =>
            {
                status.LocalReplayState = "existing_capture_preserved";
                status.LocalReplayError = null;
            });
            return;
        }
        UpdateIfCurrent(capturedGeneration, status =>
        {
            status.LocalReplayState = "not_found";
            status.LocalReplayError = "No matching persisted lazer replay appeared.";
        });
    }

    private static bool TryGetReplayVersion(string path, out (long Length, long WriteTicks) version)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                version = default;
                return false;
            }
            version = (info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (IOException)
        {
            version = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            version = default;
            return false;
        }
    }

    private void UpdateIfCurrent(long capturedGeneration, Action<LazerReplayFrameStatus> mutate)
    {
        if (!recoverMovement || capturedGeneration != Volatile.Read(ref generation))
            return;
        LazerReplayFrameDiagnostics.Update(mutate);
    }

    private void NotifyResultRecovery(
        ReplayResultRecoveryOutcome recovery,
        long id,
        string replayPath,
        string beatmapPath,
        IReadOnlyDictionary<string, string> mediaPaths,
        IReadOnlyList<Kumori.Core.Models.MovementSample> samples)
    {
        if (!recovery.Applied) return;
        resultRecovered?.Invoke(new ReplayResultRecoveryContext(
            id,
            recovery,
            replayPath,
            beatmapPath,
            null,
            mediaPaths,
            samples));
    }

    private bool TryNotifyRetainedSimulation(
        long id,
        LazerBeatmapAssets beatmap,
        RecoveryState state,
        bool tosuResultWasMissing,
        bool simulationOwnsCoreResult,
        CancellationToken operationToken)
    {
        if (state.PartialSimulationNotified)
            return true;

        var retained = repository.GetMetadata(id, operationToken);
        if (retained is not { SampleCount: > 0 }
            || retained.Source is not ("lazer_memory" or "lazer_replay_frame"))
            return false;

        IReadOnlyList<MovementSample> samples = repository.GetSamples(id, operationToken);
        if (samples.Count == 0)
            return false;

        resultRecovered?.Invoke(new ReplayResultRecoveryContext(
            id,
            ReplayResultRecoveryOutcome.NoChanges,
            null,
            beatmap.BeatmapPath,
            null,
            beatmap.Files,
            samples,
            RequiresSimulation: true,
            RequiresTosuRestart: false,
            SimulationOwnsCoreResult: simulationOwnsCoreResult,
            TosuResultWasMissing: tosuResultWasMissing));
        state.PartialSimulationNotified = true;
        Log.Information(
            "Queued retained-frame ruleset simulation from {Count} lazer frames for attempt {AttemptId}",
            samples.Count,
            id);
        return true;
    }

    internal static bool IsPartialOutcome(string outcome)
        => outcome.Equals("failed", StringComparison.OrdinalIgnoreCase)
           || outcome.Equals("retried", StringComparison.OrdinalIgnoreCase)
           || outcome.Equals("quit", StringComparison.OrdinalIgnoreCase)
           || outcome.Equals("abandoned", StringComparison.OrdinalIgnoreCase);

    private sealed record RecoveredReplay(
        string ReplayPath,
        MovementSample[] Samples,
        ReplayResultData Result);

    private sealed class RecoveryState
    {
        public LazerBeatmapAssets? Beatmap { get; set; }
        public RecoveredReplay? Matched { get; set; }
        public ReplayResultRecoveryOutcome? Recovery { get; set; }
        public bool ResultRecoveryNotified { get; set; }
        public bool PartialSimulationNotified { get; set; }
        public bool MovementCommitted { get; set; }
        public Dictionary<string, (long Length, long WriteTicks)> CheckedVersions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
