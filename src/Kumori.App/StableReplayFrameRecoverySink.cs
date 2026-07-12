using Kumori.Core.Models;
using System.IO;
using System.Text.Json;
using Kumori.Storage;
using Kumori.Tracking;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
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
    private readonly Func<long?> _attemptId;
    private readonly MovementCaptureStore _movement;
    private readonly MovementRepository _repository;
    private readonly Action<long>? movementReplaced;
    private readonly CancellationToken cancellationToken;
    private AttemptStart? _start;
    private DateTime _startedUtc;

    public StableReplayFrameRecoverySink(
        SqliteConnectionFactory factory,
        Func<long?> attemptId,
        Action<long>? movementReplaced = null,
        CancellationToken cancellationToken = default)
    {
        _attemptId = attemptId;
        _movement = new MovementCaptureStore(factory);
        _repository = new MovementRepository(factory);
        this.movementReplaced = movementReplaced;
        this.cancellationToken = cancellationToken;
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
        _start = start.ClientKind == OsuClientKind.Stable ? start : null;
        _startedUtc = DateTime.UtcNow;
        if (_start is not null)
        {
            StableReplayFrameDiagnostics.Update(status =>
            {
                status.State = "armed";
                status.Detail = "Stable attempt active; native replay-memory capture is running. Local replay recovery starts after finalization.";
                status.ActiveAttemptId = _attemptId();
                status.GameFolder = start.GameFolder;
                status.BeatmapPath = ResolveBeatmapPath(start);
                status.ExpectedChecksum = start.Checksum;
                status.CandidateReplayPath = null;
                status.CandidatesChecked = 0;
                status.FramesDecoded = 0;
                status.LiveSnapshotPath = null;
                status.LastError = null;
            });
        }
    }

    public void Checkpoint(AttemptCheckpoint checkpoint) { }

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        if (_start is not null)
            StableReplayFrameDiagnostics.Update(status =>
            {
                status.State = "discarded";
                status.Detail = $"Stable attempt was discarded before replay recovery: {discard.Reason}.";
                status.ActiveAttemptId = null;
            });
        _start = null;
    }

    public void Finalize(AttemptFinalization finalization)
    {
        var start = _start;
        var attemptId = _attemptId();
        var startedUtc = _startedUtc;
        _start = null;
        if (start is null || attemptId is null)
            return;

        StableReplayFrameDiagnostics.Update(status =>
        {
            status.State = "searching";
            status.Detail = $"Attempt {attemptId} finalized; searching stable's local replay store.";
            status.ActiveAttemptId = attemptId;
        });

        _ = Task.Run(() => RecoverAsync(start, attemptId.Value, startedUtc), cancellationToken);
    }

    private async Task RecoverAsync(AttemptStart start, long attemptId, DateTime startedUtc)
    {
        try
        {
            var beatmapPath = ResolveBeatmapPath(start);
            var gameFolder = start.GameFolder;
            if (beatmapPath is null || !File.Exists(beatmapPath) || string.IsNullOrWhiteSpace(gameFolder))
            {
                UpdateForAttempt(attemptId, status =>
                {
                    status.State = "paths_unavailable";
                    status.Detail = "tosu did not provide a usable stable game folder and beatmap path.";
                    status.LastError = "Stable local paths unavailable";
                    status.ActiveAttemptId = null;
                });
                return;
            }

            // scores.db/Data/r updates can land well after the results packet.
            // Keep the window open for two minutes and retry a path whenever
            // its size/write stamp changes, so an initially partial file does
            // not get permanently rejected.
            var checkedVersions = new Dictionary<string, (long Length, long WriteTicks)>(StringComparer.OrdinalIgnoreCase);
            for (var pass = 0; pass < 240; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in ReplayCandidates(gameFolder, startedUtc))
                {
                    var info = new FileInfo(candidate);
                    var version = (info.Length, info.LastWriteTimeUtc.Ticks);
                    if (checkedVersions.TryGetValue(candidate, out var checkedVersion) && checkedVersion == version)
                        continue;
                    checkedVersions[candidate] = version;
                    UpdateForAttempt(attemptId, status =>
                    {
                        status.State = "checking_candidate";
                        status.CandidateReplayPath = candidate;
                        status.CandidatesChecked++;
                    });
                    if (TryRead(candidate, beatmapPath, start.Checksum, out var samples))
                    {
                        var existing = _repository.GetMetadata(attemptId);
                        if (existing is { SampleCount: > 0 } && existing.Source is not ("stable_replay" or "stable_memory" or "stable_live"))
                        {
                            UpdateForAttempt(attemptId, status =>
                            {
                                status.State = "existing_capture_preserved";
                                status.Detail = $"A {existing.Source} capture already exists for attempt {attemptId}; it was preserved.";
                                status.ActiveAttemptId = null;
                            });
                            return;
                        }

                        StableReplayComparisonResult? comparison = null;
                        if (existing is { SampleCount: > 0 } && existing.Source is "stable_memory" or "stable_live")
                        {
                            IReadOnlyList<MovementSample> memorySamples = _repository.GetSamples(attemptId);
                            comparison = StableReplayComparisonArchive.Save(
                                attemptId, memorySamples, samples, candidate, start.Checksum);
                            Log.Information("Archived stable memory/.osr comparison for attempt {AttemptId} at {Report}", attemptId, comparison.ReportPath);
                        }

                        _movement.Start(attemptId);
                        _movement.AddSamples(samples);
                        _movement.Complete(0, "stable_replay", JsonSerializer.Serialize(new
                        {
                            source = "stable_replay",
                            replay_exact = true,
                            replay_path = candidate,
                            origin = "Data/r",
                        }));
                        Log.Information("Recovered {Count} exact osu!stable replay frames for attempt {AttemptId}", samples.Count, attemptId);
                        movementReplaced?.Invoke(attemptId);
                        UpdateForAttempt(attemptId, status =>
                        {
                            status.State = "stored";
                            status.Detail = $"Recovered and stored {samples.Count} exact stable replay frames for attempt {attemptId}.";
                            status.FramesDecoded = samples.Count;
                            status.FramesStored += samples.Count;
                            status.ComparisonReportPath = comparison?.ReportPath;
                            status.ComparisonSummary = comparison?.Summary;
                            status.ActiveAttemptId = null;
                            status.LastError = null;
                        });
                        return;
                    }
                }
                await Task.Delay(500, cancellationToken);
            }
            Log.Debug("No matching local osu!stable replay appeared for attempt {AttemptId}", attemptId);
            var retained = _repository.GetMetadata(attemptId);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

    private static void UpdateForAttempt(long attemptId, Action<StableReplayFrameStatus> mutate)
        => StableReplayFrameDiagnostics.Update(status =>
        {
            if (status.ActiveAttemptId == attemptId)
                mutate(status);
        });

    private static IEnumerable<string> ExistingInternalReplays(string? gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder)) yield break;
        var directory = Path.Combine(gameFolder, "Data", "r");
        if (!Directory.Exists(directory)) yield break;
        // The embedded beatmap hash is authoritative; never rely on the
        // filename prefix to identify a replay.
        // Stable's internal Data/r store normally uses hash-like filenames with
        // no .osr extension. Exported replays use .osr, so inspect every regular
        // file here and let the decoder/checksum validation identify a match.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            yield return file;
    }

    private static IEnumerable<string> ReplayCandidates(
        string gameFolder,
        DateTime startedUtc)
    {
        foreach (var file in ExistingInternalReplays(gameFolder)
                     .Select(path => new FileInfo(path))
                     .Where(file => file.LastWriteTimeUtc >= startedUtc.AddSeconds(-10))
                     .OrderByDescending(file => file.LastWriteTimeUtc))
            yield return file.FullName;

        var replayExports = Path.Combine(gameFolder, "Replays");
        if (Directory.Exists(replayExports))
        {
            foreach (var file in Directory.EnumerateFiles(replayExports, "*.osr").Select(path => new FileInfo(path))
                         .Where(file => file.LastWriteTimeUtc >= startedUtc.AddSeconds(-10))
                         .OrderByDescending(file => file.LastWriteTimeUtc))
                yield return file.FullName;
        }
    }

    internal static bool TryRead(string replayPath, string beatmapPath, string? checksum, out IReadOnlyList<MovementSample> samples)
    {
        samples = [];
        try
        {
            using var stream = File.Open(replayPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = new StableScoreDecoder(beatmapPath);
            var decoded = decoder.Parse(stream);
            if (!string.IsNullOrWhiteSpace(checksum) &&
                !decoder.ReplayBeatmapHash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                return false;

            samples = decoded.Replay.Frames.OfType<OsuReplayFrame>().Select((frame, index) => new MovementSample
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
            }).ToArray();
            return samples.Count > 1;
        }
        catch
        {
            return false;
        }
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
}
