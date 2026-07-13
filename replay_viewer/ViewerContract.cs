using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kumori.ReplayViewer;

public sealed record ViewerContract
{
    private const int paused_sample_flag = 0x02;
    public const int CurrentVersion = 1;

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; init; }

    [JsonPropertyName("attempt")]
    public required AttemptContract Attempt { get; init; }

    [JsonPropertyName("beatmap_path")]
    public required string BeatmapPath { get; init; }

    [JsonPropertyName("media_directory")]
    public string? MediaDirectory { get; init; }

    [JsonPropertyName("media_paths")]
    public Dictionary<string, string> MediaPaths { get; init; } = [];

    [JsonPropertyName("replay_path")]
    public string? ReplayPath { get; init; }

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement> Settings { get; init; } = [];

    [JsonIgnore]
    public string ThemeId
    {
        get
        {
            if (Settings.TryGetValue("kumori_theme", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() switch
                {
                    "pulse" => "pulse",
                    "windows-fluent" => "windows-fluent",
                    "custom" => "custom",
                    _ => "refined-kumori",
                };
            }
            return "refined-kumori";
        }
    }

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> CustomThemeColors
    {
        get
        {
            if (!Settings.TryGetValue("kumori_custom_theme", out var value)
                || value.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();

            return value.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    [JsonPropertyName("samples")]
    public List<MovementSample> Samples { get; init; } = [];

    [JsonPropertyName("judgement_events")]
    public List<JudgementEventContract> JudgementEvents { get; init; } = [];

    [JsonPropertyName("final_hits")]
    public FinalHitsContract? FinalHits { get; init; }

    [JsonPropertyName("recent_attempts")]
    public List<RecentAttemptContract> RecentAttempts { get; init; } = [];
    [JsonPropertyName("comparison")]
    public ComparisonContract? Comparison { get; init; }

    [JsonPropertyName("comparison_options")]
    public List<ComparisonContract> ComparisonOptions { get; init; } = [];

    /// <summary>
    /// Last map time backed by real capture evidence for an unfinished play.
    /// Completed plays intentionally return null so their full judgement pass
    /// remains available. Replay rendering may append a synthetic tail frame,
    /// which must never be used as analysis evidence.
    /// </summary>
    public double? AnalysisCoverageEnd
    {
        get
        {
            if (Attempt.Progress >= 0.99
                || Attempt.Outcome.Equals("completed", StringComparison.OrdinalIgnoreCase))
                return null;

            double lastSample = Samples
                .Where(sample => (sample.Flags & paused_sample_flag) == 0)
                .Select(sample => sample.MapTimeMs)
                .DefaultIfEmpty(double.NegativeInfinity)
                .Max();
            double lastJudgement = JudgementEvents
                .Select(judgement => (double)judgement.MapTimeMs)
                .DefaultIfEmpty(double.NegativeInfinity)
                .Max();
            double cutoff = Math.Max(lastSample, lastJudgement);
            return double.IsFinite(cutoff) ? cutoff : null;
        }
    }

    /// <summary>
    /// Last map time containing real cursor input for an unfinished play.
    /// Completed plays remain unrestricted and run to the end of the map.
    /// </summary>
    public double? ReplayPlaybackEnd
    {
        get
        {
            if (Attempt.Outcome.Equals("completed", StringComparison.OrdinalIgnoreCase))
                return null;

            double lastSample = Samples
                .Where(sample => (sample.Flags & paused_sample_flag) == 0)
                .Select(sample => sample.MapTimeMs)
                .DefaultIfEmpty(double.NegativeInfinity)
                .Max();
            return double.IsFinite(lastSample) ? lastSample : AnalysisCoverageEnd;
        }
    }

    public static ViewerContract Load(string path)
    {
        using var stream = File.OpenRead(path);
        var contract = JsonSerializer.Deserialize<ViewerContract>(stream, JsonOptions)
                       ?? throw new InvalidDataException("Viewer contract is empty.");

        if (contract.ContractVersion != CurrentVersion)
            throw new InvalidDataException($"Unsupported viewer contract {contract.ContractVersion}; expected {CurrentVersion}.");
        if (!File.Exists(contract.BeatmapPath))
            throw new FileNotFoundException("The beatmap supplied to the replay viewer does not exist.", contract.BeatmapPath);
        if (contract.Samples.Count == 0)
            throw new InvalidDataException("The viewer contract contains no replay samples.");

        return contract;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}

public sealed record AttemptContract
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("artist")]
    public string Artist { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; init; } = "";

    [JsonPropertyName("mods_key")]
    public string ModsKey { get; init; } = "";

    [JsonPropertyName("mods")]
    public List<ModContract> Mods { get; init; } = [];

    [JsonPropertyName("clock_rate")]
    public double ClockRate { get; init; } = 1;

    [JsonPropertyName("movement_source")]
    public string MovementSource { get; init; } = "";

    [JsonPropertyName("accuracy")]
    public double Accuracy { get; init; }

    [JsonPropertyName("score")]
    public long Score { get; init; }

    [JsonPropertyName("grade")]
    public string Grade { get; init; } = "";

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "";

    [JsonPropertyName("progress")]
    public double Progress { get; init; }

    [JsonPropertyName("mean_offset")]
    public double? MeanOffset { get; init; }
}

public sealed record ModContract
{
    [JsonPropertyName("acronym")]
    public string Acronym { get; init; } = "";

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement> Settings { get; init; } = [];
}

public sealed record MovementSample
{
    [JsonPropertyName("map_time_ms")]
    public double MapTimeMs { get; init; }

    [JsonPropertyName("monotonic_ms")]
    public double MonotonicMs { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("buttons")]
    public int Buttons { get; init; }

    [JsonPropertyName("flags")]
    public int Flags { get; init; }

    [JsonPropertyName("pressure")]
    public uint Pressure { get; init; }
}

public sealed record JudgementEventContract
{
    [JsonPropertyName("map_time_ms")]
    public int MapTimeMs { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("delta")]
    public int Delta { get; init; } = 1;
}

public sealed record FinalHitsContract
{
    [JsonPropertyName("n300")]
    public int N300 { get; init; }

    [JsonPropertyName("n100")]
    public int N100 { get; init; }

    [JsonPropertyName("n50")]
    public int N50 { get; init; }

    [JsonPropertyName("misses")]
    public int Misses { get; init; }
}

public sealed record RecentAttemptContract
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
    [JsonPropertyName("accuracy")]
    public double Accuracy { get; init; }
    [JsonPropertyName("n100")]
    public int N100 { get; init; }
    [JsonPropertyName("n50")]
    public int N50 { get; init; }
    [JsonPropertyName("misses")]
    public int Misses { get; init; }
    [JsonPropertyName("slider_breaks")]
    public int SliderBreaks { get; init; }
    [JsonPropertyName("mean_offset")]
    public double? MeanOffset { get; init; }
}
public sealed record ComparisonContract
{
    [JsonPropertyName("attempt_id")] public long AttemptId { get; init; }
    [JsonPropertyName("ephemeral")] public bool Ephemeral { get; init; }
    [JsonPropertyName("source_name")] public string SourceName { get; init; } = "";
    [JsonPropertyName("started_at")] public string StartedAt { get; init; } = "";
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
    [JsonPropertyName("mods_key")] public string ModsKey { get; init; } = "NM";
    [JsonPropertyName("accuracy")] public double Accuracy { get; init; }
    [JsonPropertyName("score")] public long Score { get; init; }
    [JsonPropertyName("pp")] public double Pp { get; init; }
    [JsonPropertyName("combo")] public int Combo { get; init; }
    [JsonPropertyName("n300")] public int N300 { get; init; }
    [JsonPropertyName("n100")] public int N100 { get; init; }
    [JsonPropertyName("n50")] public int N50 { get; init; }
    [JsonPropertyName("misses")] public int Misses { get; init; }
    [JsonPropertyName("judgement_events")] public List<JudgementEventContract> JudgementEvents { get; init; } = [];
    [JsonPropertyName("samples")] public List<MovementSample> Samples { get; init; } = [];
}
