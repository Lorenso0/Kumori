using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;

namespace Kumori.ReplayFrameTool;

internal static class Program
{
    private static readonly Uri DefaultVanillaTosuUri = new("ws://127.0.0.1:24051/websocket/v2");

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var options = CliOptions.Parse(args.Skip(1).ToArray());
        var paths = ToolPaths.Create(options);
        Directory.CreateDirectory(paths.LogDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(paths.LogDir, "replay-frame-tool-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            return command switch
            {
                "capture" => await CaptureAsync(options, paths),
                "status" => Status(paths),
                "list" => ListAttempts(options, paths),
                "analyze" => Analyze(options, paths),
                "export" => Export(options, paths),
                "viewer" => Viewer(options, paths),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Command failed");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task<int> CaptureAsync(CliOptions options, ToolPaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Database)!);
        var status = new JsonReplayFrameStatusSink(paths.StatusJson);
        var store = new AppStateStore();
        store.StateChanged += state =>
        {
            status.Update(s =>
            {
                s.Detail = state.Tracking.TosuConnected
                    ? state.Tracking.CurrentBeatmap ?? state.Tracking.Detail ?? s.Detail
                    : state.Tracking.Detail ?? s.Detail;
            });
        };

        var factory = new SqliteConnectionFactory(paths.Database, readOnly: false);
        var trackingSink = new AttemptSqliteSink(factory);
        var frameSource = new LazerMemoryReplayFrameSource(
            TimeSpan.FromMilliseconds(options.PollMs ?? 16),
            status,
            options.Value("offsets"));
        await using var frameCapture = new LazerReplayFrameCaptureService(
            store,
            factory,
            () => trackingSink.CurrentAttemptId,
            frameSource,
            status,
            "lazer_memory");
        frameCapture.Start();

        var attemptSink = new CompositeAttemptSink(trackingSink, frameCapture);
        var tosuUri = options.Uri("tosu-url") ?? DefaultVanillaTosuUri;
        await using var tracker = new TosuTrackingService(
            store,
            tosuUri,
            new AttemptTracker(attemptSink),
            new SessionTracker(trackingSink),
            recordPackets: options.Has("record-packets"));
        tracker.Start();

        Console.WriteLine("Capture running. Press Ctrl+C to stop.");
        Console.WriteLine($"Database: {paths.Database}");
        Console.WriteLine($"Status:   {paths.StatusJson}");
        Console.WriteLine($"tosu:     {tosuUri}");

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        await stop.Task;
        return 0;
    }

    private static int Status(ToolPaths paths)
    {
        var status = new JsonReplayFrameStatusSink(paths.StatusJson).Load();
        Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
        return 0;
    }

    private static int ListAttempts(CliOptions options, ToolPaths paths)
    {
        var factory = new SqliteConnectionFactory(paths.Database);
        var repo = new AttemptRepository(factory);
        var limit = options.Int("limit") ?? 20;
        foreach (var attempt in repo.GetRecentAttempts(limit: limit))
        {
            Console.WriteLine(
                $"{attempt.Id,5} {attempt.StartedAt} {attempt.Outcome,-10} {attempt.Accuracy:P2} {attempt.Score,9} {attempt.ModsKey,-10} {attempt.Artist} - {attempt.Title} [{attempt.Difficulty}] movement={(attempt.HasMovement ? "yes" : "no")}");
        }
        return 0;
    }

    private static int Analyze(CliOptions options, ToolPaths paths)
    {
        var id = RequiredId(options);
        var factory = new SqliteConnectionFactory(paths.Database);
        var details = new AttemptDetailsRepository(factory).GetDetails(id)
            ?? throw new InvalidOperationException($"Attempt {id} was not found.");
        var samples = new MovementRepository(factory).GetSamples(id);
        var payload = new
        {
            attempt = details.Summary,
            duration_seconds = details.DurationSeconds,
            hits = new { details.N300, details.N100, details.N50, details.Summary.Misses, details.SliderBreaks },
            timing = details.Timing,
            input = details.Input,
            movement = details.Movement,
            movement_samples = samples.Count,
            first_sample_ms = samples.Count > 0 ? samples[0].MapTimeMs : (double?)null,
            last_sample_ms = samples.Count > 0 ? samples[^1].MapTimeMs : (double?)null,
        };

        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
        else
        {
            Console.WriteLine($"{details.Summary.Artist} - {details.Summary.Title} [{details.Summary.Difficulty}] {details.Summary.ModsKey}");
            Console.WriteLine($"Score {details.Summary.Score}  Acc {details.Summary.Accuracy:P2}  Combo {details.Summary.Combo}  Misses {details.Summary.Misses}");
            Console.WriteLine($"Movement {details.Movement?.Source ?? "none"}  Samples {samples.Count}  Rate {details.Movement?.SampleRate ?? 0:0.##}/s");
            Console.WriteLine($"Input K1 {details.Input?.Key1Presses ?? 0}  K2 {details.Input?.Key2Presses ?? 0}  Peak KPS {details.Input?.PeakKps ?? 0}");
            Console.WriteLine($"Timing hits {details.Timing?.HitCount ?? 0}  mean {details.Timing?.Mean ?? 0:0.##}  dev {details.Timing?.Deviation ?? 0:0.##}");
        }
        return 0;
    }

    private static int Export(CliOptions options, ToolPaths paths)
    {
        var id = RequiredId(options);
        var factory = new SqliteConnectionFactory(paths.Database);
        var details = new AttemptDetailsRepository(factory).GetDetails(id)
            ?? throw new InvalidOperationException($"Attempt {id} was not found.");
        var samples = new MovementRepository(factory).GetSamples(id);
        var output = options.Value("out") ?? Path.Combine(paths.ExportDir, $"attempt-{id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            exported_at = DateTimeOffset.UtcNow,
            details,
            samples,
        }, JsonOptions));
        Console.WriteLine(output);
        return 0;
    }

    private static int Viewer(CliOptions options, ToolPaths paths)
    {
        var id = RequiredId(options);
        var beatmap = options.Value("beatmap")
            ?? throw new ArgumentException("viewer requires --beatmap <path>.");
        var factory = new SqliteConnectionFactory(paths.Database);
        var service = new ReplayViewerContractService(
            new AttemptDetailsRepository(factory),
            new MovementRepository(factory),
            new KumoriSettings(),
            paths.ContractsDir);
        var contract = service.WriteContract(id, beatmap);
        if (!options.Has("no-launch"))
        {
            service.LaunchViewer(contract, options.Value("viewer-exe"));
        }
        Console.WriteLine(contract);
        return 0;
    }

    private static long RequiredId(CliOptions options)
    {
        var idText = options.Positionals.FirstOrDefault()
            ?? throw new ArgumentException("This command requires an attempt id.");
        return long.TryParse(idText, out var id)
            ? id
            : throw new ArgumentException($"Invalid attempt id: {idText}");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Usage();
        return 2;
    }

    private static void Usage()
    {
        Console.WriteLine("""
        Kumori.ReplayFrameTool

        Commands:
          capture [--tosu-url <ws-url>] [--db <path>] [--poll-ms <n>] [--status-json <path>] [--record-packets]
          status [--status-json <path>]
          list [--db <path>] [--limit <n>]
          analyze <attempt-id> [--db <path>] [--json]
          export <attempt-id> [--db <path>] [--out <path>]
          viewer <attempt-id> --beatmap <path> [--db <path>] [--viewer-exe <path>] [--no-launch]
        """);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

internal sealed record ToolPaths(string Root, string Database, string StatusJson, string LogDir, string ExportDir, string ContractsDir)
{
    public static ToolPaths Create(CliOptions options)
    {
        var root = Path.GetFullPath(options.Value("data-dir") ?? AppPaths.AppDataDir);
        var defaultDatabase = string.Equals(root, AppPaths.AppDataDir, StringComparison.OrdinalIgnoreCase)
            ? AppPaths.TrackingDatabase
            : Path.Combine(root, "data", "tracking", "osu_tracking.sqlite3");
        var defaultStatus = string.Equals(root, AppPaths.AppDataDir, StringComparison.OrdinalIgnoreCase)
            ? AppPaths.LazerReplayFrameStatusFile
            : Path.Combine(root, "runtime", "status", "lazer_replay_frame_status.json");
        return new ToolPaths(
            root,
            Path.GetFullPath(options.Value("db") ?? defaultDatabase),
            Path.GetFullPath(options.Value("status-json") ?? defaultStatus),
            Path.Combine(root, "logs", "replay-frame-tool"),
            Path.Combine(root, "reports"),
            Path.Combine(root, "runtime", "viewer-contracts"));
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = [];
    public int? PollMs => Int("poll-ms");

    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(arg);
                continue;
            }

            var key = arg[2..];
            string? value = null;
            var equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            result._values[key] = value ?? "true";
        }

        return result;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string? Value(string key) => _values.TryGetValue(key, out var value) ? value : null;
    public int? Int(string key) => int.TryParse(Value(key), out var value) ? value : null;
    public Uri? Uri(string key) => System.Uri.TryCreate(Value(key), UriKind.Absolute, out var uri) ? uri : null;
}
