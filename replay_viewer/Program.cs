using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using osu.Framework;
using osu.Framework.Platform;
using osu.Game.Rulesets.Osu.Replays;

namespace Kumori.ReplayViewer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                NativeViewerLog.Error(ex, "Unhandled exception");
            else
                NativeViewerLog.Write($"Unhandled exception: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => NativeViewerLog.Error(e.Exception, "Unobserved task exception");

        try
        {
            var options = Arguments.Parse(args);
            NativeViewerLog.Write($"Starting with args: {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            if (options.Probe)
            {
                NativeViewerLog.Write("Probe succeeded");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    contract_version = ViewerContract.CurrentVersion,
                    lazer_package = BuildInfo.LazerPackageVersion,
                    assembly = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                }));
                return 0;
            }

            ViewerContract contract = ViewerContract.Load(options.ContractPath!);
            AdvancedAnalyzerColours.Configure(contract.ThemeId);
            NativeViewerLog.Write($"Loaded contract attempt={contract.Attempt.Id} samples={contract.Samples.Count} beatmap=\"{contract.BeatmapPath}\"");
            var replay = LazerReplayAdapter.CreateReplay(contract);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "loaded",
                attempt_id = contract.Attempt.Id,
                movement_source = contract.Attempt.MovementSource,
                mods_key = contract.Attempt.ModsKey,
                clock_rate = contract.Attempt.ClockRate,
                contract_samples = contract.Samples.Count,
                contract_first_ms = contract.Samples.Count > 0 ? contract.Samples[0].MapTimeMs : (double?)null,
                contract_last_ms = contract.Samples.Count > 0 ? contract.Samples[^1].MapTimeMs : (double?)null,
                replay_frames = replay.Frames.Count,
                replay_first_ms = replay.Frames.Count > 0 ? replay.Frames[0].Time : (double?)null,
                replay_last_ms = replay.Frames.Count > 0 ? replay.Frames[^1].Time : (double?)null,
                replay_action_frames = replay.Frames.OfType<OsuReplayFrame>().Count(f => f.Actions.Count > 0),
            }));
            var mods = LazerReplayAdapter.ResolveMods(
                contract.Attempt,
                LazerReplayAdapter.DecodedScore?.ScoreInfo.Mods);
            BeatmapAnalysis analysis = LazerBeatmapAnalyzer.Decode(contract.BeatmapPath, mods);
            NativeViewerLog.Write($"Decoded beatmap \"{analysis.Artist} - {analysis.Title} [{analysis.Difficulty}]\" objects={analysis.Objects.Count}");

            if (options.PrepareAnalysis)
            {
                var analysisGame = new ReplayAnalysisGame(contract, options.ContractPath!);
                using (var analysisHost = new HeadlessGameHost("Kumori.ReplayAnalysis", realtime: false))
                    analysisHost.Run(analysisGame);
                if (!analysisGame.Succeeded)
                    throw analysisGame.Failure ?? new InvalidOperationException("Replay analysis did not complete.");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "analysis_ready",
                    path = PreparedReplayAnalysis.PathFor(options.ContractPath!),
                }));
                return 0;
            }

            if (options.AnalyzePath is not null)
            {
                File.WriteAllText(options.AnalyzePath, JsonSerializer.Serialize(analysis, ViewerContract.JsonOptions));
                NativeViewerLog.Write($"Wrote analysis to \"{options.AnalyzePath}\"");
                return 0;
            }

            using DesktopGameHost host = Host.GetSuitableDesktopHost("Kumori.ReplayViewer");
            host.Run(new ReplayViewerGame(contract, analysis, PreparedReplayAnalysis.Load(options.ContractPath!)));
            NativeViewerLog.Write("Host exited normally");
            return 0;
        }
        catch (Exception ex)
        {
            NativeViewerLog.Error(ex, "Fatal startup error");
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                status = "error",
                type = ex.GetType().FullName,
                message = ex.Message,
                detail = ex.ToString(),
            }));
            return 1;
        }
    }
}

internal sealed record Arguments(bool Probe, string? ContractPath, string? AnalyzePath, bool PrepareAnalysis)
{
    public static Arguments Parse(string[] args)
    {
        if (args.Contains("--probe"))
            return new Arguments(true, null, null, false);

        string? contract = valueAfter(args, "--contract");
        string? analyze = valueAfter(args, "--analyze");
        if (string.IsNullOrWhiteSpace(contract))
            throw new ArgumentException("Usage: Kumori.ReplayViewer --contract <path> [--analyze <output.json>]");
        return new Arguments(
            false,
            Path.GetFullPath(contract),
            analyze is null ? null : Path.GetFullPath(analyze),
            args.Contains("--prepare-analysis"));
    }

    private static string? valueAfter(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal static class BuildInfo
{
    public const string LazerPackageVersion = "2026.621.0";
}
