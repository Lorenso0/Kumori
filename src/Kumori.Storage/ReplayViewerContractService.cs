using System.Diagnostics;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Serilog;

namespace Kumori.Storage;

public sealed class ReplayViewerContractService
{
    private readonly AttemptDetailsRepository _details;
    private readonly MovementRepository _movement;
    private readonly Func<KumoriSettings> _settings;
    private readonly string _contractDirectory;

    public ReplayViewerContractService(
        AttemptDetailsRepository details,
        MovementRepository movement,
        KumoriSettings settings,
        string? contractDirectory = null)
        : this(details, movement, () => settings, contractDirectory)
    {
    }

    public bool IsEnabled => _settings().ReplayViewer.Enabled;

    public ReplayViewerContractService(
        AttemptDetailsRepository details,
        MovementRepository movement,
        Func<KumoriSettings> settings,
        string? contractDirectory = null)
    {
        _details = details;
        _movement = movement;
        _settings = settings;
        _contractDirectory = contractDirectory ?? AppPaths.ViewerContractsDir;
    }

    /// <summary>Returns the raw movement capture for validation and analysis views.</summary>
    public IReadOnlyList<MovementSample> GetMovementSamples(long attemptId) => _movement.GetSamples(attemptId);

    /// <summary>
    /// Runs the official headless lazer ruleset against a checksum-matched
    /// replay and returns values which are not present in the replay header.
    /// </summary>
    public async Task<ReplaySimulationResult> SimulateRecoveryAsync(
        long attemptId,
        string? replayPath,
        string beatmapPath,
        string? mediaDirectory,
        IReadOnlyDictionary<string, string>? mediaPaths,
        IReadOnlyList<MovementSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
            throw new InvalidOperationException("Replay simulation requires replay frames.");
        if (!string.IsNullOrWhiteSpace(replayPath) && !File.Exists(replayPath))
            throw new FileNotFoundException("Recovered replay file not found.", replayPath);
        if (!File.Exists(beatmapPath))
            throw new FileNotFoundException("Replay beatmap file not found.", beatmapPath);
        cancellationToken.ThrowIfCancellationRequested();

        var details = _details.GetDetails(attemptId)
            ?? throw new InvalidOperationException($"Attempt {attemptId} was not found.");
        Directory.CreateDirectory(_contractDirectory);
        string contractPath = Path.Combine(_contractDirectory, $"{attemptId}-recovery-{Guid.NewGuid():N}.json");
        string analysisPath = contractPath + ".analysis.json";
        var payload = new
        {
            contract_version = 1,
            attempt = new
            {
                id = attemptId,
                artist = details.Summary.Artist,
                title = details.Summary.Title,
                difficulty = details.Summary.Difficulty,
                mods_key = details.Summary.ModsKey,
                mods = details.Mods.Select(mod => new
                {
                    acronym = mod.Acronym,
                    settings = SettingsObject(mod.SettingsJson),
                }).ToArray(),
                clock_rate = ClockRate(details),
                movement_source = "replay_result_recovery",
                accuracy = details.Summary.Accuracy,
                score = details.Summary.Score,
                max_combo = details.BeatmapMaxCombo,
                grade = details.Summary.Grade ?? "",
                outcome = details.Summary.Outcome,
                progress = details.Summary.Progress,
                mean_offset = details.Timing?.Mean,
            },
            beatmap_path = Path.GetFullPath(beatmapPath),
            media_directory = Path.GetFullPath(mediaDirectory ?? Path.GetDirectoryName(beatmapPath)!),
            media_paths = mediaPaths ?? new Dictionary<string, string>(),
            replay_path = string.IsNullOrWhiteSpace(replayPath) ? null : Path.GetFullPath(replayPath),
            settings = ReplaySettings(),
            judgement_events = Array.Empty<object>(),
            final_hits = new
            {
                n300 = details.N300,
                n100 = details.N100,
                n50 = details.N50,
                misses = details.Summary.Misses,
            },
            recent_attempts = Array.Empty<object>(),
            comparison = (object?)null,
            comparison_options = Array.Empty<object>(),
            samples = samples.Select(sample => new
            {
                map_time_ms = sample.MapTimeMs,
                monotonic_ms = sample.MonotonicMs,
                x = sample.X,
                y = sample.Y,
                buttons = sample.Buttons,
                flags = sample.Flags,
                pressure = sample.Pressure,
            }).ToArray(),
        };

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using (var contract = new FileStream(
                             contractPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(contract, payload, JsonOptions, cancellationToken);
            }
            await PrepareAnalysisAsync(contractPath, cancellationToken: cancellationToken);
            var prepared = JsonSerializer.Deserialize<PreparedRecoveryAnalysis>(
                await File.ReadAllTextAsync(analysisPath, cancellationToken), JsonOptions)
                ?? throw new InvalidDataException("Replay simulation result was empty.");
            if (prepared.Summary is null)
                throw new InvalidDataException("Replay simulation did not return a result summary.");
            ValidateSimulationCompleteness(details, prepared.Summary, prepared.Judgements);
            return prepared.Summary with { Judgements = prepared.Judgements };
        }
        finally
        {
            try { File.Delete(contractPath); } catch { }
            try { File.Delete(analysisPath); } catch { }
        }
    }

    internal static void ValidateSimulationCompleteness(
        AttemptDetails details,
        ReplaySimulationResult summary,
        IReadOnlyList<ReplaySimulationJudgement> judgements)
    {
        int simulatedCoreTotal = summary.N300 + summary.N100 + summary.N50 + summary.Misses;
        int capturedCoreTotal = details.N300 + details.N100 + details.N50 + details.Summary.Misses;
        int timingEvidence = details.Timing?.HitCount ?? 0;
        int minimumCoreTotal = Math.Max(capturedCoreTotal, timingEvidence);
        if (simulatedCoreTotal < minimumCoreTotal)
        {
            throw new InvalidDataException(
                $"Replay simulation was incomplete: scored {simulatedCoreTotal} core objects but capture evidence requires at least {minimumCoreTotal}.");
        }

        int simulatedMissEvents = judgements.Count(judgement => judgement.Kind == 0);
        int simulatedFifties = judgements.Count(judgement => judgement.Kind == 1);
        int simulatedHundreds = judgements.Count(judgement => judgement.Kind == 2);
        if (simulatedMissEvents != summary.Misses
            || simulatedFifties != summary.N50
            || simulatedHundreds != summary.N100)
        {
            throw new InvalidDataException(
                "Replay simulation judgement events did not match its numeric 100/50/miss summary.");
        }
    }

    public string WriteContract(long attemptId, string beatmapPath, string? mediaDirectory = null, IReadOnlyDictionary<string, string>? mediaPaths = null)
    {
        var details = _details.GetDetails(attemptId)
            ?? throw new InvalidOperationException($"Attempt {attemptId} was not found.");
        var samples = _movement.GetSamples(attemptId);
        if (samples.Count == 0)
        {
            throw new InvalidOperationException("No movement samples are available for this attempt.");
        }
        if (!File.Exists(beatmapPath))
        {
            throw new FileNotFoundException("Beatmap file not found.", beatmapPath);
        }

        var metadata = _movement.GetMetadata(attemptId);
        var recentAttempts = _details.GetRecentSameMapAttempts(attemptId);
        var comparisonOptions = _details.GetComparableAttempts(attemptId)
            .Select(candidate => new
            {
                summary = candidate,
                samples = CompactComparisonSamples(_movement.GetSamples(candidate.Id)),
                judgements = _details.GetJudgementEvents(candidate.Id)
                    .Where(e => e.EventType is "miss" or "slider_break" or "hit_100" or "hit_50")
                    .ToArray(),
            })
            .Where(candidate => candidate.samples.Count > 0)
            .ToArray();
        Directory.CreateDirectory(_contractDirectory);
        DeleteOldContracts(attemptId);
        var contractPath = Path.Combine(_contractDirectory, $"{attemptId}-{Guid.NewGuid():N}.json");

        var payload = new
        {
            contract_version = 1,
            attempt = new
            {
                id = details.Summary.Id,
                artist = details.Summary.Artist,
                title = details.Summary.Title,
                difficulty = details.Summary.Difficulty,
                mods_key = details.Summary.ModsKey,
                mods = details.Mods.Select(m => new
                {
                    acronym = m.Acronym,
                    settings = SettingsObject(m.SettingsJson),
                }).ToArray(),
                clock_rate = ClockRate(details),
                movement_source = metadata?.Source ?? "live",
                accuracy = details.Summary.Accuracy,
                score = details.Summary.Score,
                max_combo = details.BeatmapMaxCombo,
                grade = details.Summary.Grade ?? "",
                outcome = details.Summary.Outcome,
                progress = details.Summary.Progress,
                mean_offset = details.Timing?.Mean,
            },
            beatmap_path = Path.GetFullPath(beatmapPath),
            media_directory = Path.GetFullPath(mediaDirectory ?? Path.GetDirectoryName(beatmapPath)!),
            media_paths = mediaPaths ?? new Dictionary<string, string>(),
            replay_path = ReplayPathFromCalibration(metadata?.CalibrationJson),
            settings = ReplaySettings(),
            judgement_events = details.Events
                .Where(e => e.EventType is "miss" or "slider_break" or "hit_100" or "hit_50")
                .Select(ToViewerJudgement)
                .ToArray(),
            final_hits = new
            {
                n300 = details.N300,
                n100 = details.N100,
                n50 = details.N50,
                misses = details.Summary.Misses,
            },
            recent_attempts = recentAttempts.Select(attempt => new
            {
                id = attempt.Id,
                accuracy = attempt.Accuracy,
                n100 = attempt.N100,
                n50 = attempt.N50,
                misses = attempt.Misses,
                slider_breaks = attempt.SliderBreaks,
                mean_offset = attempt.MeanOffset,
            }).ToArray(),
            comparison = (object?)null,
            comparison_options = comparisonOptions.Select(candidate => new
            {
                attempt_id = candidate.summary.Id,
                started_at = candidate.summary.StartedAt,
                outcome = candidate.summary.Outcome,
                mods_key = candidate.summary.ModsKey,
                accuracy = candidate.summary.Accuracy,
                score = candidate.summary.Score,
                pp = candidate.summary.Pp,
                combo = candidate.summary.Combo,
                max_combo = details.BeatmapMaxCombo,
                n300 = candidate.summary.N300,
                n100 = candidate.summary.N100,
                n50 = candidate.summary.N50,
                misses = candidate.summary.Misses,
                judgement_events = candidate.judgements.Select(ToViewerJudgement).ToArray(),
                samples = candidate.samples.Select(s => new { map_time_ms = s.MapTimeMs, monotonic_ms = s.MonotonicMs, x = s.X, y = s.Y, buttons = s.Buttons, flags = s.Flags, pressure = s.Pressure }).ToArray(),
            }).ToArray(),
            samples = samples.Select(s => new
            {
                map_time_ms = s.MapTimeMs,
                monotonic_ms = s.MonotonicMs,
                x = s.X,
                y = s.Y,
                buttons = s.Buttons,
                flags = s.Flags,
                pressure = s.Pressure,
            }).ToArray(),
        };

        File.WriteAllText(contractPath, JsonSerializer.Serialize(payload, JsonOptions));
        return contractPath;
    }

    private static IReadOnlyList<MovementSample> CompactComparisonSamples(IReadOnlyList<MovementSample> samples)
    {
        // Comparison choices are embedded so the standalone viewer can switch
        // without a database connection. Keep input/flag transitions exactly,
        // but cap ordinary cursor points at roughly 250 Hz to prevent a list of
        // candidates from turning one launch contract into hundreds of MB.
        if (samples.Count <= 2)
            return samples;

        var compact = new List<MovementSample> { samples[0] };
        var previous = samples[0];
        var lastKept = samples[0];
        for (var index = 1; index < samples.Count - 1; index++)
        {
            var sample = samples[index];
            var stateChanged = sample.Buttons != previous.Buttons || sample.Flags != previous.Flags;
            if (stateChanged || sample.MapTimeMs - lastKept.MapTimeMs >= 4)
            {
                compact.Add(sample);
                lastKept = sample;
            }
            previous = sample;
        }
        compact.Add(samples[^1]);
        return compact;
    }

    private void DeleteOldContracts(long attemptId)
    {
        foreach (var file in Directory.EnumerateFiles(_contractDirectory, $"{attemptId}-*.json"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "Could not delete old Replay Analyzer contract {Path}", file);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Debug(ex, "Could not delete old Replay Analyzer contract {Path}", file);
            }
        }
    }

    public Process LaunchViewer(string contractPath, string? viewerExecutable = null)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Replay Analyzer is disabled in Kumori settings.");
        viewerExecutable ??= ResolveViewerExecutable();
        if (!File.Exists(viewerExecutable))
        {
            throw new FileNotFoundException("Replay viewer executable not found.", viewerExecutable);
        }

        var start = new ProcessStartInfo
        {
            FileName = viewerExecutable,
            WorkingDirectory = Path.GetDirectoryName(viewerExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--contract");
        start.ArgumentList.Add(contractPath);

        Log.Information("Launching Replay Analyzer: {Viewer} --contract {Contract}", viewerExecutable, contractPath);
        AppendViewerLog($"Launching {viewerExecutable} --contract {contractPath}");

        var process = Process.Start(start) ?? throw new InvalidOperationException("Replay viewer did not start.");
        _ = CaptureViewerOutputAsync(process);
        return process;
    }

    public async Task PrepareAnalysisAsync(
        string contractPath,
        string? viewerExecutable = null,
        CancellationToken cancellationToken = default)
    {
        if (UsesAuthoritativeCapturedJudgements(contractPath))
        {
            string preparedPath = contractPath + ".analysis.json";
            if (File.Exists(preparedPath)) File.Delete(preparedPath);
            AppendViewerLog($"Skipping lazer judgement simulation for stable memory contract {contractPath}; captured stable judgements are authoritative.");
            return;
        }
        viewerExecutable ??= ResolveViewerExecutable();
        if (!File.Exists(viewerExecutable))
            throw new FileNotFoundException("Replay viewer executable not found.", viewerExecutable);

        var start = new ProcessStartInfo
        {
            FileName = viewerExecutable,
            WorkingDirectory = Path.GetDirectoryName(viewerExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--contract");
        start.ArgumentList.Add(contractPath);
        start.ArgumentList.Add("--prepare-analysis");

        Log.Information("Preparing exact Replay Analyzer analysis for {Contract}", contractPath);
        AppendViewerLog($"Preparing analysis for {contractPath}");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Replay analysis process did not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) { Log.Debug(ex, "Could not lower replay analysis process priority"); }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("Replay judgement simulation did not complete within 45 seconds.");
        }
        string output = await stdout;
        string error = await stderr;
        if (!string.IsNullOrWhiteSpace(output))
            AppendViewerLog(output.Trim());
        if (!string.IsNullOrWhiteSpace(error))
            AppendViewerLog(error.Trim());
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Replay analysis exited with code {process.ExitCode}."
                    : error.Trim());
        if (!File.Exists(contractPath + ".analysis.json"))
            throw new InvalidOperationException("Replay analysis completed without producing judgement data.");
    }

    private static bool UsesAuthoritativeCapturedJudgements(string contractPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(contractPath));
            string? source = document.RootElement.GetProperty("attempt").GetProperty("movement_source").GetString();
            return source is not null && (source.Equals("stable_memory", StringComparison.OrdinalIgnoreCase)
                                           || source.Equals("stable_live", StringComparison.OrdinalIgnoreCase)
                                           || source.Equals("stable_replay", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public static string ResolveViewerExecutable()
        => ResolveViewerExecutable(AppContext.BaseDirectory);

    public static string ResolveViewerExecutable(string baseDirectory)
    {
        var embeddedViewer = ReplayViewerPayload.TryEnsureExtracted();
        if (embeddedViewer is not null)
        {
            return embeddedViewer;
        }

        var repoRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(repoRoot, "replay_viewer", "bin", "Debug", "net10.0", "win-x64", "Kumori.ReplayViewer.exe"),
            Path.Combine(repoRoot, "replay_viewer", "bin", "Debug", "net10.0", "Kumori.ReplayViewer.exe"),
            Path.Combine(baseDirectory, "Kumori.ReplayViewer", "Kumori.ReplayViewer.exe"),
            Path.Combine(repoRoot, "dist", "app", "Kumori.ReplayViewer", "Kumori.ReplayViewer.exe"),
            Path.Combine(repoRoot, "replay_viewer", "publish", "Kumori.ReplayViewer.exe"),
        };
        var resolved = candidates.FirstOrDefault(File.Exists);
        if (resolved is not null)
        {
            return resolved;
        }

        Log.Warning("Replay viewer executable was not found. Checked: {Candidates}", string.Join("; ", candidates));
        return candidates[0];
    }

    private Dictionary<string, object> ReplaySettings()
    {
        var settings = _settings();
        return new Dictionary<string, object>
        {
            ["osu_replay_master_volume"] = settings.ReplayViewer.MasterVolume,
            ["osu_replay_music_volume"] = settings.ReplayViewer.MusicVolume,
            ["osu_replay_hitsound_volume"] = settings.ReplayViewer.HitsoundVolume,
            ["osu_replay_skin_path"] = settings.ReplayViewer.SkinPath,
            ["osu_replay_disable_hidden"] = settings.ReplayViewer.DisableHidden,
            ["kumori_theme"] = settings.Appearance.ThemeId,
            ["kumori_custom_theme"] = CustomThemePalette.Normalize(settings.Appearance.CustomTheme).Colors,
        };
    }

    private static double ClockRate(AttemptDetails details)
    {
        foreach (var mod in details.Mods)
        {
            if (mod.Acronym.Equals("DT", StringComparison.OrdinalIgnoreCase) ||
                mod.Acronym.Equals("NC", StringComparison.OrdinalIgnoreCase) ||
                mod.Acronym.Equals("HT", StringComparison.OrdinalIgnoreCase) ||
                mod.Acronym.Equals("DC", StringComparison.OrdinalIgnoreCase))
            {
                if (SpeedChange(mod.SettingsJson) is { } speed)
                {
                    return speed;
                }
            }
        }

        var acronyms = details.Mods
            .Select(m => m.Acronym)
            .Concat(ModAcronymsFromKey(details.Summary.ModsKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (acronyms.Contains("DT") || acronyms.Contains("NC"))
        {
            return 1.5;
        }
        if (acronyms.Contains("HT") || acronyms.Contains("DC"))
        {
            return 0.75;
        }

        return 1.0;
    }

    private static Dictionary<string, object?> SettingsObject(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonValue(p.Value), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object? JsonValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };

    private static double? SpeedChange(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("speed_change", out var speed) &&
                speed.TryGetDouble(out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static IEnumerable<string> ModAcronymsFromKey(string? modsKey)
    {
        if (string.IsNullOrWhiteSpace(modsKey))
        {
            yield break;
        }

        if (modsKey.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(modsKey);
                foreach (var mod in doc.RootElement.EnumerateArray())
                {
                    if (mod.TryGetProperty("acronym", out var acronym) &&
                        !string.IsNullOrWhiteSpace(acronym.GetString()))
                    {
                        yield return acronym.GetString()!;
                    }
                }
            }
            finally
            {
                doc?.Dispose();
            }

            yield break;
        }

        var compact = modsKey.Trim();
        if (compact.Equals("NM", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        for (var i = 0; i + 1 < compact.Length; i += 2)
        {
            yield return compact.Substring(i, 2);
        }
    }

    private static object ToViewerJudgement(JudgementEvent e)
    {
        var delta = 1;
        try
        {
            using var doc = JsonDocument.Parse(e.DataJson);
            if (doc.RootElement.TryGetProperty("delta", out var value) &&
                value.TryGetInt32(out var parsed))
            {
                delta = parsed;
            }
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Could not parse replay judgement event data");
        }

        return new
        {
            map_time_ms = (int)(e.MapTimeMs ?? 0),
            kind = e.EventType switch
            {
                "hit_100" => "100",
                "hit_50" => "50",
                "slider_break" => "slider_break",
                _ => "miss",
            },
            delta,
        };
    }

    private static string? ReplayPathFromCalibration(string? calibrationJson)
    {
        if (string.IsNullOrWhiteSpace(calibrationJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(calibrationJson);
            return doc.RootElement.TryGetProperty("replay_file", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Could not parse replay calibration JSON");
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static async Task CaptureViewerOutputAsync(Process process)
    {
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync().ConfigureAwait(false);

            var stdoutText = await stdout.ConfigureAwait(false);
            var stderrText = await stderr.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdoutText))
                AppendViewerLog($"stdout:{Environment.NewLine}{stdoutText.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderrText))
                AppendViewerLog($"stderr:{Environment.NewLine}{stderrText.Trim()}");

            AppendViewerLog($"Exited with code {process.ExitCode}");
            if (process.ExitCode != 0)
                Log.Warning("Replay Analyzer exited with code {ExitCode}. See {LogPath}", process.ExitCode, ViewerLogPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not capture Replay Analyzer output.");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string ViewerLogPath => AppPaths.ViewerLogFile;

    private static void AppendViewerLog(string message)
    {
        try
        {
            LogRetentionPolicy.AppendWithSizeRotation(
                ViewerLogPath,
                $"[{DateTimeOffset.Now:O}] app {message}{Environment.NewLine}",
                maxAgeDays: LogRetentionPolicy.ReadConfiguredDays());
        }
        catch
        {
            // Logging must never block opening the viewer.
        }
    }
}

public sealed record ReplaySimulationResult
{
    public int N300 { get; init; }
    public int N100 { get; init; }
    public int N50 { get; init; }
    public int Misses { get; init; }
    public double Accuracy { get; init; }
    public long Score { get; init; }
    public int AchievedCombo { get; init; }
    public int SliderBreaks { get; init; }
    public int LargeTickHits { get; init; }
    public int LargeTickMisses { get; init; }
    public int SmallTickHits { get; init; }
    public int SmallTickMisses { get; init; }
    public int SliderTailHits { get; init; }
    public int SliderTailMisses { get; init; }
    public double UnstableRate { get; init; }
    public IReadOnlyList<double> TimingOffsets { get; init; } = [];
    public double Pp { get; init; }
    public double FcPp { get; init; }
    public double MaxPp { get; init; }
    public double BaseStars { get; init; }
    public double AdjustedStars { get; init; }
    public double ApproachRate { get; init; }
    public double AdjustedApproachRate { get; init; }
    public double CircleSize { get; init; }
    public double AdjustedCircleSize { get; init; }
    public double OverallDifficulty { get; init; }
    public double AdjustedOverallDifficulty { get; init; }
    public double DrainRate { get; init; }
    public double AdjustedDrainRate { get; init; }
    public double Bpm { get; init; }
    public double AdjustedBpm { get; init; }
    public double ClockRate { get; init; }
    public int MaxCombo { get; init; }
    public int CircleCount { get; init; }
    public int SliderCount { get; init; }
    public int SpinnerCount { get; init; }
    public IReadOnlyList<ReplaySimulationJudgement> Judgements { get; init; } = [];
}

public sealed record ReplaySimulationJudgement
{
    public int Kind { get; init; }
    public double EventTime { get; init; }
    public double RootStartTime { get; init; }
    public double ObjectStartTime { get; init; }
    public double TimeOffset { get; init; }
}

internal sealed record PreparedRecoveryAnalysis
{
    public int Version { get; init; }
    public long AttemptId { get; init; }
    public ReplaySimulationResult? Summary { get; init; }
    public IReadOnlyList<ReplaySimulationJudgement> Judgements { get; init; } = [];
}
