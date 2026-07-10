using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kumori.ReplayViewer;

public sealed record ViewerContract
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; init; }

    [JsonPropertyName("attempt")]
    public required AttemptContract Attempt { get; init; }

    [JsonPropertyName("beatmap_path")]
    public required string BeatmapPath { get; init; }

    [JsonPropertyName("media_directory")]
    public string? MediaDirectory { get; init; }

    [JsonPropertyName("replay_path")]
    public string? ReplayPath { get; init; }

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement> Settings { get; init; } = [];

    [JsonPropertyName("samples")]
    public List<MovementSample> Samples { get; init; } = [];

    [JsonPropertyName("judgement_events")]
    public List<JudgementEventContract> JudgementEvents { get; init; } = [];

    [JsonPropertyName("final_hits")]
    public FinalHitsContract? FinalHits { get; init; }

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

    [JsonPropertyName("grade")]
    public string Grade { get; init; } = "";
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
