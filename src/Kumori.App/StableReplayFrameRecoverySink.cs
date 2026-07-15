using Kumori.Core.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Kumori.Storage;
using Kumori.Tracking;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring.Legacy;
using Serilog;

namespace Kumori.App;

/// <summary>
/// Recovers exact osu!stable replay frames from the client's local replay store.
/// Stable writes successful local plays beneath Data/r; unlike sampled cursor
/// telemetry these frames retain the coordinates and actions used by osu! itself.
/// </summary>
internal sealed class StableReplayFrameRecoverySink : IAttemptSink
{
    private const int SearchPassCount = 240;
    private const int MaxCandidatesPerPass = 8;
    private const int MaxDirectoryEntriesPerPass = 256;
    private const int PersistenceChunkSize = 4096;
    internal const long MaximumReplayFileBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan SearchRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly Func<long?> _attemptId;
    private readonly MovementCaptureStore _movement;
    private readonly MovementRepository _repository;
    private readonly ReplayResultRecoveryStore resultRecovery;
    private readonly Action<long>? movementReplaced;
    private readonly Action<ReplayResultRecoveryContext>? resultRecovered;
    private readonly Action<long>? resultTelemetryMissing;
    private readonly bool recoverMovement;
    private readonly CancellationToken cancellationToken;
    private readonly GameplayWorkCoordinator? workCoordinator;
    private readonly ConcurrentDictionary<long, byte> activeRecoveries = new();
    private AttemptStart? _start;
    private bool sawGameplayResult;
    private DateTime _startedUtc;

    public StableReplayFrameRecoverySink(
        SqliteConnectionFactory factory,
        Func<long?> attemptId,
        Action<long>? movementReplaced = null,
        Action<ReplayResultRecoveryContext>? resultRecovered = null,
        Action<long>? resultTelemetryMissing = null,
        bool recoverMovement = true,
        CancellationToken cancellationToken = default,
        GameplayWorkCoordinator? workCoordinator = null)
    {
        _attemptId = attemptId;
        _movement = new MovementCaptureStore(factory);
        _repository = new MovementRepository(factory);
        resultRecovery = new ReplayResultRecoveryStore(factory);
        this.movementReplaced = movementReplaced;
        this.resultRecovered = resultRecovered;
        this.resultTelemetryMissing = resultTelemetryMissing;
        this.recoverMovement = recoverMovement;
        this.cancellationToken = cancellationToken;
        this.workCoordinator = workCoordinator;
        if (recoverMovement)
            StableReplayFrameDiagnostics.Update(status =>
            {
                status.Enabled = true;
                status.State = "waiting_for_stable_play";
                status.Detail = "Stable replay recovery is enabled and waiting for an osu!stable attempt.";
                status.LastError = null;
            });
    }

    public void StartAttempt(AttemptStart start)
    {
        sawGameplayResult = false;
        _start = start.ClientKind == OsuClientKind.Stable ? start : null;
        _startedUtc = DateTime.UtcNow;
        if (_start is not null && recoverMovement)
        {
            StableReplayFrameDiagnostics.Update(status =>
            {
                status.State = "armed";
                status.Detail = "Stable attempt active; native replay-memory capture is running. Local replay recovery starts after finalization.";
                status.ActiveAttemptId = _attemptId();
                status.GameFolder = start.GameFolder;
                // Diagnostics only need a useful hint here. Probing candidate
                // files belongs to deferred recovery, never the first live
                // gameplay packet.
                status.BeatmapPath = BeatmapPathHint(start);
                status.ExpectedChecksum = start.Checksum;
                status.CandidateReplayPath = null;
                status.CandidatesChecked = 0;
                status.FramesDecoded = 0;
                status.LiveSnapshotPath = null;
                status.LastError = null;
            });
        }
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
        => sawGameplayResult |= LazerReplayFrameRecoverySink.HasGameplayResult(checkpoint);

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        if (_start is not null && recoverMovement)
            StableReplayFrameDiagnostics.Update(status =>
            {
                status.State = "discarded";
                status.Detail = $"Stable attempt was discarded before replay recovery: {discard.Reason}.";
                status.ActiveAttemptId = null;
            });
        _start = null;
        sawGameplayResult = false;
    }

    public void Finalize(AttemptFinalization finalization)
    {
        var start = _start;
        var attemptId = _attemptId();
        var startedUtc = _startedUtc;
        bool capturedGameplayResult = sawGameplayResult;
        _start = null;
        sawGameplayResult = false;
        if (start is null || attemptId is null)
            return;
        bool tosuResultWasMissing = LazerReplayFrameRecoverySink.HasMissingTosuResult(
            finalization,
            capturedGameplayResult);
        if (tosuResultWasMissing)
            resultTelemetryMissing?.Invoke(attemptId.Value);
        bool requiresRetainedSimulation = LazerReplayFrameRecoverySink.IsPartialOutcome(finalization.Outcome)
                                          || string.IsNullOrWhiteSpace(start.Checksum);
        if (requiresRetainedSimulation)
        {
            if (recoverMovement)
                StableReplayFrameDiagnostics.Update(status =>
                {
                    status.State = "existing_capture_preserved";
                    status.Detail = string.IsNullOrWhiteSpace(start.Checksum)
                        ? $"Attempt {attemptId} had no replay checksum; retained stable memory frames instead of risking a mismatched replay."
                        : $"Attempt {attemptId} was partial; retained stable memory frames without matching a later saved score.";
                    status.ActiveAttemptId = null;
                });
            StartRetainedSimulationRecovery(
                start,
                attemptId.Value,
                tosuResultWasMissing,
                simulationOwnsCoreResult: tosuResultWasMissing && !capturedGameplayResult);
            return;
        }

        if (recoverMovement)
            StableReplayFrameDiagnostics.Update(status =>
            {
                status.State = "searching";
                status.Detail = $"Attempt {attemptId} finalized; searching stable's local replay store.";
                status.ActiveAttemptId = attemptId;
            });

        if (!activeRecoveries.TryAdd(attemptId.Value, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverAsync(start, attemptId.Value, startedUtc).ConfigureAwait(false);
            }
            finally
            {
                activeRecoveries.TryRemove(attemptId.Value, out _);
            }
        }, CancellationToken.None);
    }

    private void StartRetainedSimulationRecovery(
        AttemptStart start,
        long attemptId,
        bool tosuResultWasMissing,
        bool simulationOwnsCoreResult)
    {
        if (!activeRecoveries.TryAdd(attemptId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                Task<bool> RunPass(int pass, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    string? beatmapPath = ResolveBeatmapPath(start);
                    if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
                        return Task.FromResult(true);

                    var metadata = _repository.GetMetadata(attemptId, token);
                    if (metadata is not { SampleCount: > 0 }
                        || metadata.Source is not ("stable_memory" or "stable_live"))
                        return Task.FromResult(pass == SearchPassCount - 1);

                    IReadOnlyList<MovementSample> samples = _repository.GetSamples(attemptId, token);
                    if (samples.Count == 0)
                        return Task.FromResult(pass == SearchPassCount - 1);

                    resultRecovered?.Invoke(new ReplayResultRecoveryContext(
                        attemptId,
                        ReplayResultRecoveryOutcome.NoChanges,
                        null,
                        beatmapPath,
                        Path.GetDirectoryName(beatmapPath),
                        null,
                        samples,
                        RequiresSimulation: true,
                        RequiresTosuRestart: false,
                        SimulationOwnsCoreResult: simulationOwnsCoreResult,
                        TosuResultWasMissing: tosuResultWasMissing));
                    Log.Information(
                        "Queued retained-frame stable ruleset simulation from {Count} frames for attempt {AttemptId}",
                        samples.Count,
                        attemptId);
                    return Task.FromResult(true);
                }

                if (workCoordinator is not null)
                {
                    await workCoordinator.RunFairRetryLoopAsync(
                        $"stable-partial-simulation-{attemptId}",
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
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Stable retained-frame simulation recovery failed for attempt {AttemptId}", attemptId);
            }
            finally
            {
                activeRecoveries.TryRemove(attemptId, out _);
            }
        }, CancellationToken.None);
    }

    private async Task RecoverAsync(
        AttemptStart start,
        long attemptId,
        DateTime startedUtc)
    {
        try
        {
            // scores.db/Data/r updates can land well after the results packet.
            // Keep the two-minute window, but run only one bounded scan in each
            // coordinator turn. Retry delay happens outside the worker.
            using var state = new RecoveryState();
            Task<bool> RunPass(int pass, CancellationToken token)
                => RecoverOnePassAsync(
                    start,
                    attemptId,
                    startedUtc,
                    state,
                    pass == SearchPassCount - 1,
                    token);

            if (workCoordinator is not null)
            {
                await workCoordinator.RunFairRetryLoopAsync(
                    $"stable-replay-recovery-{attemptId}",
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
            Log.Warning(ex, "osu!stable replay-frame recovery failed for attempt {AttemptId}", attemptId);
            UpdateForAttempt(attemptId, status =>
            {
                status.State = "error";
                status.Detail = $"Stable replay recovery failed for attempt {attemptId}.";
                status.ActiveAttemptId = null;
                status.LastError = ex.Message;
            });
        }
    }

    private Task<bool> RecoverOnePassAsync(
        AttemptStart start,
        long attemptId,
        DateTime startedUtc,
        RecoveryState state,
        bool isLastPass,
        CancellationToken operationToken)
    {
        operationToken.ThrowIfCancellationRequested();
        if (!state.PathsValidated)
        {
            state.PathsValidated = true;
            state.BeatmapPath = ResolveBeatmapPath(start);
            state.GameFolder = start.GameFolder;
            if (state.BeatmapPath is null ||
                !File.Exists(state.BeatmapPath) ||
                string.IsNullOrWhiteSpace(state.GameFolder))
            {
                UpdateForAttempt(attemptId, status =>
                {
                    status.State = "paths_unavailable";
                    status.Detail = "tosu did not provide a usable stable game folder and beatmap path.";
                    status.LastError = "Stable local paths unavailable";
                    status.ActiveAttemptId = null;
                });
                return Task.FromResult(true);
            }
            state.Candidates = new ReplayCandidateCursor(state.GameFolder);
        }

        if (state.Matched is null)
        {
            var candidatesChecked = 0;
            foreach (var candidate in state.Candidates!.ReadNextBatch(
                         MaxDirectoryEntriesPerPass,
                         operationToken))
            {
                operationToken.ThrowIfCancellationRequested();
                if (!TryGetReplayVersion(candidate, out var version) ||
                    version.WriteTicks < startedUtc.AddSeconds(-10).Ticks ||
                    state.CheckedVersions.TryGetValue(candidate, out var checkedVersion) && checkedVersion == version)
                    continue;
                if (version.Length is <= 0 or > MaximumReplayFileBytes)
                {
                    state.CheckedVersions[candidate] = version;
                    continue;
                }
                if (candidatesChecked++ >= MaxCandidatesPerPass)
                    break;

                UpdateForAttempt(attemptId, status =>
                {
                    status.State = "checking_candidate";
                    status.CandidateReplayPath = candidate;
                    status.CandidatesChecked++;
                });
                if (TryRead(
                        candidate,
                        state.BeatmapPath!,
                        start.Checksum,
                        out var samples,
                        out var replayResult,
                        operationToken))
                {
                    state.Matched = new RecoveredReplay(
                        candidate,
                        samples as MovementSample[] ?? samples.ToArray(),
                        replayResult);
                    break;
                }

                // Retry a partial/invalid replay only after it changes.
                state.CheckedVersions[candidate] = version;
            }
        }

        if (state.Matched is null)
        {
            if (!isLastPass)
                return Task.FromResult(false);
            if (operationToken.IsCancellationRequested)
                return Task.FromResult(false);

            CompleteNotFound(attemptId, operationToken);
            return Task.FromResult(true);
        }

        var matched = state.Matched;
        var interruptedAfterApply = false;
        if (state.Recovery is null)
        {
            operationToken.ThrowIfCancellationRequested();
            var recovery = resultRecovery.Apply(
                attemptId,
                matched.Result,
                "stable_replay",
                operationToken);
            interruptedAfterApply = operationToken.IsCancellationRequested;
            if (!recovery.AttemptReady && !isLastPass)
                return Task.FromResult(false);
            state.Recovery = recovery;
        }

        if (state.Recovery.Applied && !state.ResultRecoveryNotified)
        {
            // Result recovery is already committed. Notify immediately so the
            // mandatory tosu restart cannot be lost if gameplay begins before
            // optional movement replacement finishes.
            NotifyResultRecovery(
                state.Recovery,
                attemptId,
                matched.ReplayPath,
                state.BeatmapPath!,
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
        var existing = _repository.GetMetadata(attemptId, operationToken);
        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        if (existing is { SampleCount: > 0 } && existing.Source is not ("stable_replay" or "stable_memory" or "stable_live"))
        {
            UpdateForAttempt(attemptId, status =>
            {
                status.State = "existing_capture_preserved";
                status.Detail = $"A {existing.Source} capture already exists for attempt {attemptId}; it was preserved.";
                status.ActiveAttemptId = null;
            });
            return Task.FromResult(true);
        }

        if (!state.ComparisonAttempted &&
            existing is { SampleCount: > 0 } && existing.Source is "stable_memory" or "stable_live")
        {
            if (operationToken.IsCancellationRequested)
                return Task.FromResult(false);
            try
            {
                IReadOnlyList<MovementSample> memorySamples;
                using (new BelowNormalThreadPriorityScope())
                    memorySamples = _repository.GetSamples(attemptId, operationToken);
                if (operationToken.IsCancellationRequested)
                    return Task.FromResult(false);

                using (new BelowNormalThreadPriorityScope())
                    state.Comparison = StableReplayComparisonArchive.Save(
                        attemptId,
                        memorySamples,
                        matched.Samples,
                        matched.ReplayPath,
                        start.Checksum,
                        cancellationToken: operationToken);
                state.ComparisonAttempted = true;
                Log.Information(
                    "Archived stable memory/.osr comparison for attempt {AttemptId} at {Report}",
                    attemptId,
                    state.Comparison.ReportPath);
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                // The pending archive is removed atomically by Save. Return a
                // normal retry result so this committed recovery is re-enqueued
                // fairly instead of rerunning in the same coordinator item.
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                state.ComparisonAttempted = true;
                Log.Warning(ex, "Skipping optional stable replay comparison for attempt {AttemptId}", attemptId);
            }
        }

        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        _movement.Start(attemptId);
        for (var offset = 0; offset < matched.Samples.Length; offset += PersistenceChunkSize)
        {
            if (operationToken.IsCancellationRequested)
                return Task.FromResult(false);
            int count = Math.Min(PersistenceChunkSize, matched.Samples.Length - offset);
            _movement.AddSamples(new ArraySegment<MovementSample>(matched.Samples, offset, count));
        }
        if (operationToken.IsCancellationRequested)
            return Task.FromResult(false);
        _movement.Complete(0, "stable_replay", JsonSerializer.Serialize(new
        {
            source = "stable_replay",
            replay_exact = true,
            replay_path = matched.ReplayPath,
            origin = "Data/r",
        }), operationToken);
        state.MovementCommitted = true;
        Log.Information(
            "Recovered {Count} exact osu!stable replay frames for attempt {AttemptId}",
            matched.Samples.Length,
            attemptId);
        movementReplaced?.Invoke(attemptId);
        UpdateForAttempt(attemptId, status =>
        {
            status.State = "stored";
            status.Detail = $"Recovered and stored {matched.Samples.Length} exact stable replay frames for attempt {attemptId}.";
            status.FramesDecoded = matched.Samples.Length;
            status.FramesStored += matched.Samples.Length;
            status.ComparisonReportPath = state.Comparison?.ReportPath;
            status.ComparisonSummary = state.Comparison?.Summary;
            status.ActiveAttemptId = null;
            status.LastError = null;
        });
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

    private void CompleteNotFound(long attemptId, CancellationToken operationToken)
    {
        Log.Debug("No matching local osu!stable replay appeared for attempt {AttemptId}", attemptId);
        var retained = _repository.GetMetadata(attemptId, operationToken);
        if (retained is { SampleCount: > 0 } && retained.Source is "stable_memory" or "stable_live")
        {
            UpdateForAttempt(attemptId, status =>
            {
                status.State = "existing_capture_preserved";
                status.Detail = $"No matching local stable replay appeared; preserved {retained.SampleCount} {retained.Source} frames for attempt {attemptId}.";
                status.ActiveAttemptId = null;
                status.LastError = null;
            });
            return;
        }
        UpdateForAttempt(attemptId, status =>
        {
            status.State = "replay_not_found";
            status.Detail = $"No checksum-matching local stable replay appeared for attempt {attemptId}.";
            status.ActiveAttemptId = null;
            status.LastError = "Matching stable replay not found";
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

    private static void UpdateForAttempt(long attemptId, Action<StableReplayFrameStatus> mutate)
        => StableReplayFrameDiagnostics.Update(status =>
        {
            if (status.ActiveAttemptId == attemptId)
                mutate(status);
        });

    internal static bool TryRead(string replayPath, string beatmapPath, string? checksum, out IReadOnlyList<MovementSample> samples)
        => TryRead(replayPath, beatmapPath, checksum, out samples, out _);

    internal static bool TryRead(
        string replayPath,
        string beatmapPath,
        string? checksum,
        out IReadOnlyList<MovementSample> samples,
        out ReplayResultData result,
        CancellationToken cancellationToken = default)
    {
        samples = [];
        result = new ReplayResultData(0, 0, null, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetReplayVersion(replayPath, out var version) ||
                version.Length is <= 0 or > MaximumReplayFileBytes)
                return false;

            using var stream = File.Open(replayPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            cancellationToken.ThrowIfCancellationRequested();
            using var priority = new BelowNormalThreadPriorityScope();
            var decoder = new StableScoreDecoder(beatmapPath);
            var decoded = decoder.Parse(stream);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(checksum) &&
                !decoder.ReplayBeatmapHash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                return false;

            var score = decoded.ScoreInfo;
            result = new ReplayResultData(
                score.TotalScore,
                score.Accuracy * 100d,
                score.Rank.ToString(),
                score.MaxCombo,
                score.Statistics.GetValueOrDefault(HitResult.Great),
                score.Statistics.GetValueOrDefault(HitResult.Ok),
                score.Statistics.GetValueOrDefault(HitResult.Meh),
                score.Statistics.GetValueOrDefault(HitResult.Miss),
                score.Statistics.GetValueOrDefault(HitResult.Perfect),
                score.Statistics.GetValueOrDefault(HitResult.Good),
                score.Statistics.GetValueOrDefault(HitResult.LargeTickHit),
                score.Statistics.GetValueOrDefault(HitResult.LargeTickMiss),
                score.Statistics.GetValueOrDefault(HitResult.SmallTickHit),
                score.Statistics.GetValueOrDefault(HitResult.SmallTickMiss),
                score.Statistics.GetValueOrDefault(HitResult.SliderTailHit));

            var decodedSamples = new List<MovementSample>();
            var frameIndex = 0;
            foreach (var frame in decoded.Replay.Frames.OfType<OsuReplayFrame>())
            {
                if ((frameIndex++ & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                decodedSamples.Add(new MovementSample
                {
                    MapTimeMs = frame.Time,
                    MonotonicMs = frame.Time,
                    X = frame.Position.X,
                    Y = frame.Position.Y,
                    RawX = (short)Math.Clamp((int)Math.Round(frame.Position.X), short.MinValue, short.MaxValue),
                    RawY = (short)Math.Clamp((int)Math.Round(frame.Position.Y), short.MinValue, short.MaxValue),
                    Buttons = (frame.Actions.Contains(OsuAction.LeftButton) ? 0x10 : 0) |
                              (frame.Actions.Contains(OsuAction.RightButton) ? 0x20 : 0),
                    Flags = 1,
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
            samples = decodedSamples.ToArray();
            return samples.Count > 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void NotifyResultRecovery(
        ReplayResultRecoveryOutcome recovery,
        long attemptId,
        string replayPath,
        string beatmapPath,
        IReadOnlyList<MovementSample> samples)
    {
        if (!recovery.Applied) return;
        resultRecovered?.Invoke(new ReplayResultRecoveryContext(
            attemptId,
            recovery,
            replayPath,
            beatmapPath,
            Path.GetDirectoryName(beatmapPath),
            null,
            samples));
    }

    private static string? ResolveBeatmapPath(AttemptStart start)
    {
        if (string.IsNullOrWhiteSpace(start.BeatmapFile)) return null;
        var file = start.BeatmapFile;
        var candidates = new List<string>();
        if (Path.IsPathRooted(file)) candidates.Add(file);
        var songsFolder = start.SongsFolder;
        if (!string.IsNullOrWhiteSpace(songsFolder) && !Path.IsPathRooted(songsFolder) && !string.IsNullOrWhiteSpace(start.GameFolder))
            songsFolder = Path.Combine(start.GameFolder, songsFolder);
        if (!string.IsNullOrWhiteSpace(songsFolder)) candidates.Add(Path.Combine(songsFolder, file));
        if (!string.IsNullOrWhiteSpace(start.BeatmapFolder))
        {
            var folder = start.BeatmapFolder;
            if (!Path.IsPathRooted(folder) && !string.IsNullOrWhiteSpace(songsFolder))
                folder = Path.Combine(songsFolder, folder);
            candidates.Add(Path.Combine(folder, Path.GetFileName(file)));
        }
        if (!string.IsNullOrWhiteSpace(start.GameFolder))
        {
            candidates.Add(Path.Combine(start.GameFolder, "Songs", file));
            candidates.Add(Path.Combine(start.GameFolder, file));
        }
        var normalized = candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return normalized.FirstOrDefault(File.Exists) ?? normalized.FirstOrDefault();
    }

    private static string? BeatmapPathHint(AttemptStart start)
    {
        if (string.IsNullOrWhiteSpace(start.BeatmapFile))
            return null;

        var file = start.BeatmapFile;
        if (Path.IsPathRooted(file))
            return file;

        var songsFolder = start.SongsFolder;
        if (!string.IsNullOrWhiteSpace(songsFolder)
            && !Path.IsPathRooted(songsFolder)
            && !string.IsNullOrWhiteSpace(start.GameFolder))
        {
            songsFolder = Path.Combine(start.GameFolder, songsFolder);
        }

        if (!string.IsNullOrWhiteSpace(start.BeatmapFolder))
        {
            var folder = start.BeatmapFolder;
            if (!Path.IsPathRooted(folder) && !string.IsNullOrWhiteSpace(songsFolder))
                folder = Path.Combine(songsFolder, folder);
            return Path.Combine(folder, Path.GetFileName(file));
        }

        if (!string.IsNullOrWhiteSpace(songsFolder))
            return Path.Combine(songsFolder, file);
        if (!string.IsNullOrWhiteSpace(start.GameFolder))
            return Path.Combine(start.GameFolder, "Songs", file);
        return file;
    }

    private sealed class StableScoreDecoder(string beatmapPath) : LegacyScoreDecoder
    {
        private readonly WorkingBeatmap _beatmap = new FlatWorkingBeatmap(beatmapPath);
        public string ReplayBeatmapHash { get; private set; } = "";
        protected override Ruleset GetRuleset(int rulesetId) => rulesetId == 0 ? new OsuRuleset() : throw new InvalidDataException();
        protected override WorkingBeatmap GetBeatmap(string md5Hash)
        {
            ReplayBeatmapHash = md5Hash;
            return _beatmap;
        }
    }

    private sealed class BelowNormalThreadPriorityScope : IDisposable
    {
        private readonly Thread thread = Thread.CurrentThread;
        private ThreadPriority previous;
        private bool changed;

        public BelowNormalThreadPriorityScope()
        {
            try
            {
                previous = thread.Priority;
                if (previous > ThreadPriority.BelowNormal)
                {
                    thread.Priority = ThreadPriority.BelowNormal;
                    changed = true;
                }
            }
            catch
            {
                changed = false;
            }
        }

        public void Dispose()
        {
            if (!changed)
                return;
            try { thread.Priority = previous; }
            catch { }
        }
    }

    private sealed class RecoveryState : IDisposable
    {
        public bool PathsValidated { get; set; }
        public string? BeatmapPath { get; set; }
        public string? GameFolder { get; set; }
        public ReplayCandidateCursor? Candidates { get; set; }
        public Dictionary<string, (long Length, long WriteTicks)> CheckedVersions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public RecoveredReplay? Matched { get; set; }
        public ReplayResultRecoveryOutcome? Recovery { get; set; }
        public bool ResultRecoveryNotified { get; set; }
        public bool ComparisonAttempted { get; set; }
        public StableReplayComparisonResult? Comparison { get; set; }
        public bool MovementCommitted { get; set; }

        public void Dispose() => Candidates?.Dispose();
    }

    /// <summary>
    /// Keeps a lazy cursor across passes so a large Data/r directory is never
    /// fully enumerated in one coordinator turn. A completed sweep is reopened
    /// on the next pass so newly committed replay files become visible.
    /// </summary>
    private sealed class ReplayCandidateCursor(string gameFolder) : IDisposable
    {
        private readonly (string Directory, string Pattern)[] sources =
        [
            (Path.Combine(gameFolder, "Data", "r"), "*"),
            (Path.Combine(gameFolder, "Replays"), "*.osr"),
        ];
        private IEnumerator<string>? current;
        private int sourceIndex;

        public IReadOnlyList<string> ReadNextBatch(int maxEntries, CancellationToken cancellationToken)
        {
            var result = new List<string>(Math.Min(maxEntries, 32));
            var entriesRead = 0;
            var completedSources = 0;
            while (entriesRead < maxEntries && completedSources < sources.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current is null && !TryOpenCurrentSource())
                {
                    AdvanceSource();
                    completedSources++;
                    continue;
                }

                bool hasNext;
                try
                {
                    hasNext = current!.MoveNext();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    hasNext = false;
                }

                if (!hasNext)
                {
                    AdvanceSource();
                    completedSources++;
                    continue;
                }

                entriesRead++;
                result.Add(current!.Current);
            }
            return result;
        }

        private bool TryOpenCurrentSource()
        {
            var source = sources[sourceIndex];
            try
            {
                if (!Directory.Exists(source.Directory))
                    return false;
                current = Directory
                    .EnumerateFiles(source.Directory, source.Pattern, SearchOption.TopDirectoryOnly)
                    .GetEnumerator();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                current = null;
                return false;
            }
        }

        private void AdvanceSource()
        {
            current?.Dispose();
            current = null;
            sourceIndex = (sourceIndex + 1) % sources.Length;
        }

        public void Dispose()
        {
            current?.Dispose();
            current = null;
        }
    }

    private sealed record RecoveredReplay(
        string ReplayPath,
        MovementSample[] Samples,
        ReplayResultData Result);
}
