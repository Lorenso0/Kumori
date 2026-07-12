using Kumori.Storage;
using Kumori.Tracking;
using Kumori.Native;
using System.Text.Json;
using Serilog;

namespace Kumori.App;

/// <summary>Replaces a completed lazer memory capture with its persisted Realm replay when available.</summary>
internal sealed class LazerReplayFrameRecoverySink : IAttemptSink
{
    private readonly Func<long?> attemptId;
    private readonly MovementCaptureStore movement;
    private readonly MovementRepository repository;
    private readonly Action<long>? movementReplaced;
    private readonly CancellationToken cancellationToken;
    private AttemptStart? start;
    private long generation;

    public LazerReplayFrameRecoverySink(
        SqliteConnectionFactory factory,
        Func<long?> attemptId,
        Action<long>? movementReplaced = null,
        CancellationToken cancellationToken = default)
    {
        this.attemptId = attemptId;
        movement = new MovementCaptureStore(factory);
        repository = new MovementRepository(factory);
        this.movementReplaced = movementReplaced;
        this.cancellationToken = cancellationToken;
    }

    public void StartAttempt(AttemptStart value)
    {
        start = value.ClientKind == OsuClientKind.Lazer ? value : null;
        if (start is not null)
            Interlocked.Increment(ref generation);
    }
    public void Checkpoint(AttemptCheckpoint checkpoint) { }
    public void DiscardIfEmpty(AttemptDiscard discard) => start = null;

    public void Finalize(AttemptFinalization finalization)
    {
        var capturedStart = start;
        var id = attemptId();
        long capturedGeneration = Volatile.Read(ref generation);
        start = null;
        if (capturedStart is null || id is null || string.IsNullOrWhiteSpace(capturedStart.Checksum)) return;
        var endedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(finalization.Snapshot.WallTime * 1000));
        _ = Task.Run(() => RecoverAsync(capturedStart, id.Value, capturedGeneration, endedAt), cancellationToken);
    }

    private async Task RecoverAsync(AttemptStart attempt, long id, long capturedGeneration, DateTimeOffset endedAt)
    {
        try
        {
            var beatmap = LazerStorage.ResolveBeatmapAssets(attempt.BeatmapId, attempt.BeatmapSetId, attempt.Difficulty);
            if (beatmap is null) return;
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(attempt.WallTime * 1000));
            // Realm score/file rows may be committed well after gameplay
            // finalizes. Match stable recovery's two-minute window.
            for (var pass = 0; pass < 240; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replay = LazerStorage.ResolveReplayFile(attempt.Checksum!, startedAt, attempt.GameFolder, endedAt);
                if (replay is not null && StableReplayFrameRecoverySink.TryRead(replay, beatmap.BeatmapPath, attempt.Checksum, out var samples))
                {
                    var existing = repository.GetMetadata(id);
                    if (existing is { SampleCount: > 0 } && existing.Source is not ("lazer_memory" or "lazer_replay_frame" or "lazer_replay")) return;
                    movement.Start(id);
                    movement.AddSamples(samples);
                    movement.Complete(0, "lazer_replay", JsonSerializer.Serialize(new
                    {
                        source = "lazer_replay",
                        replay_exact = true,
                        origin = "client.realm",
                        replay_path = replay,
                    }));
                    UpdateIfCurrent(capturedGeneration, status =>
                    {
                        status.LocalReplayState = "stored";
                        status.LocalReplayPath = replay;
                        status.LocalReplayFrames = samples.Count;
                        status.LocalReplayError = null;
                    });
                    Log.Information("Replaced lazer memory capture with {Count} persisted Realm replay frames for attempt {AttemptId}", samples.Count, id);
                    movementReplaced?.Invoke(id);
                    return;
                }
                UpdateIfCurrent(capturedGeneration, status => status.LocalReplayState = "waiting");
                await Task.Delay(500, cancellationToken);
            }
            var retained = repository.GetMetadata(id);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

    private void UpdateIfCurrent(long capturedGeneration, Action<LazerReplayFrameStatus> mutate)
    {
        if (capturedGeneration != Volatile.Read(ref generation))
            return;
        LazerReplayFrameDiagnostics.Update(mutate);
    }
}
