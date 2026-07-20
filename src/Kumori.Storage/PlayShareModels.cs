using System.Text.Json.Serialization;
using Kumori.Core.Models;

namespace Kumori.Storage;

public sealed record KumoriPackageManifestV1
{
    [JsonPropertyName("format")] public string Format { get; init; } = PlaySharePackageService.FormatName;
    [JsonPropertyName("version")] public int Version { get; init; } = PlaySharePackageService.CurrentFormatVersion;
    [JsonPropertyName("exported_at")] public DateTimeOffset ExportedAt { get; init; }
    [JsonPropertyName("app_version")] public string AppVersion { get; init; } = "";
    [JsonPropertyName("player_name")] public string PlayerName { get; init; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("play_sha256")] public string PlaySha256 { get; init; } = "";
    [JsonPropertyName("movement")] public IReadOnlyList<KumoriPackageMovementEntryV1> Movement { get; init; } = [];
    [JsonPropertyName("assets")] public IReadOnlyList<KumoriPackageAssetV1> Assets { get; init; } = [];
    [JsonPropertyName("optional_media_omissions")] public IReadOnlyList<string> OptionalMediaOmissions { get; init; } = [];
}

public sealed record KumoriPackageMovementEntryV1
{
    [JsonPropertyName("entry")] public string Entry { get; init; } = "";
    [JsonPropertyName("sample_count")] public int SampleCount { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
}

public sealed record KumoriPackageAssetV1
{
    [JsonPropertyName("entry")] public string Entry { get; init; } = "";
    [JsonPropertyName("logical_name")] public string LogicalName { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
}

public sealed record SharedPlayV1
{
    [JsonPropertyName("started_at")] public string StartedAt { get; init; } = "";
    [JsonPropertyName("ended_at")] public string? EndedAt { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
    [JsonPropertyName("grade")] public string? Grade { get; init; }
    [JsonPropertyName("accuracy")] public double Accuracy { get; init; }
    [JsonPropertyName("score")] public long Score { get; init; }
    [JsonPropertyName("pp")] public double Pp { get; init; }
    [JsonPropertyName("combo")] public int Combo { get; init; }
    [JsonPropertyName("misses")] public int Misses { get; init; }
    [JsonPropertyName("progress")] public double Progress { get; init; }
    [JsonPropertyName("mods_key")] public string ModsKey { get; init; } = "NM";
    [JsonPropertyName("mods")] public IReadOnlyList<SharedModV1> Mods { get; init; } = [];
    [JsonPropertyName("map")] public SharedMapV1 Map { get; init; } = new();
    [JsonPropertyName("results")] public SharedResultsV1 Results { get; init; } = new();
    [JsonPropertyName("timing")] public SharedTimingV1? Timing { get; init; }
    [JsonPropertyName("events")] public IReadOnlyList<SharedEventV1> Events { get; init; } = [];
    [JsonPropertyName("input")] public SharedInputV1? Input { get; init; }
    [JsonPropertyName("captured_difficulty")]
    public IReadOnlyDictionary<string, SharedDifficultyV1> CapturedDifficulty { get; init; }
        = new Dictionary<string, SharedDifficultyV1>();
    [JsonPropertyName("movement")] public SharedMovementV1 Movement { get; init; } = new();

    public static SharedPlayV1 From(AttemptDetails details, MovementMetadata metadata) => new()
    {
        StartedAt = details.Summary.StartedAt,
        EndedAt = details.Summary.EndedAt,
        Outcome = details.Summary.Outcome,
        Grade = details.Summary.Grade,
        Accuracy = details.Summary.Accuracy,
        Score = details.Summary.Score,
        Pp = details.Summary.Pp,
        Combo = details.Summary.Combo,
        Misses = details.Summary.Misses,
        Progress = details.Summary.Progress,
        ModsKey = details.Summary.ModsKey,
        Mods = details.Mods.Select(mod => new SharedModV1(mod.Acronym, mod.SettingsJson)).ToArray(),
        Map = new SharedMapV1
        {
            Artist = details.Summary.Artist,
            Title = details.Summary.Title,
            Difficulty = details.Summary.Difficulty,
            Mapper = details.Mapper,
            BeatmapId = details.Summary.OsuBeatmapId,
            SetId = details.Summary.BeatmapSetId,
            Checksum = details.Summary.Checksum,
            BaseStars = details.BaseStars ?? details.Summary.Stars,
            AdjustedStars = details.AdjustedStars ?? details.Summary.AdjustedStars,
            Ar = details.BeatmapAr,
            Cs = details.BeatmapCs,
            Od = details.BeatmapOd,
            Hp = details.BeatmapHp,
            Bpm = details.Bpm,
            MaxCombo = details.BeatmapMaxCombo,
        },
        Results = new SharedResultsV1
        {
            N300 = details.N300,
            N100 = details.N100,
            N50 = details.N50,
            Geki = details.Geki,
            Katu = details.Katu,
            SliderBreaks = details.SliderBreaks,
            LargeTickHits = details.LargeTickHits,
            LargeTickMisses = details.LargeTickMisses,
            SmallTickHits = details.SmallTickHits,
            SmallTickMisses = details.SmallTickMisses,
            SliderTailHits = details.SliderTailHits,
            SliderTailMisses = details.SliderTailMisses,
            UnstableRate = details.UnstableRate,
            FcPp = details.FcPp,
            MaxPp = details.MaxPp,
            DurationSeconds = details.DurationSeconds,
            TerminationEvidence = details.TerminationEvidence,
            Key1Count = details.Key1Count,
            Key2Count = details.Key2Count,
            Key1Binding = details.Key1Binding,
            Key2Binding = details.Key2Binding,
        },
        Timing = details.Timing is null ? null : new SharedTimingV1
        {
            HitCount = details.Timing.HitCount,
            EarlyCount = details.Timing.EarlyCount,
            LateCount = details.Timing.LateCount,
            Mean = details.Timing.Mean,
            Median = details.Timing.Median,
            Deviation = details.Timing.Deviation,
            Offsets = details.Timing.Offsets.ToArray(),
        },
        Events = details.Events.Select(e => new SharedEventV1
        {
            EventType = e.EventType,
            MapTimeMs = e.MapTimeMs,
            Value = e.Value,
            DataJson = e.DataJson,
        }).ToArray(),
        Input = details.Input is null ? null : new SharedInputV1
        {
            Key1Presses = details.Input.Key1Presses,
            Key2Presses = details.Input.Key2Presses,
            Alternations = details.Input.Alternations,
            SimultaneousPresses = details.Input.SimultaneousPresses,
            Key1HoldMs = details.Input.Key1HoldMs,
            Key2HoldMs = details.Input.Key2HoldMs,
            PeakKps = details.Input.PeakKps,
            AverageKps = details.Input.AverageKps,
        },
        CapturedDifficulty = details.CapturedDifficulty.ToDictionary(
            pair => pair.Key,
            pair => new SharedDifficultyV1(pair.Value.Original, pair.Value.Converted),
            StringComparer.OrdinalIgnoreCase),
        Movement = new SharedMovementV1
        {
            Source = metadata.Source,
            SampleRate = metadata.SampleRate,
            SampleCount = metadata.SampleCount,
            DroppedSamples = metadata.DroppedSamples,
            ReplayStatus = metadata.ReplayStatus,
        },
    };

    public AttemptDetails ToAttemptDetails(
        long importId,
        string playerName,
        string importedAt,
        string beatmapPath,
        string? backgroundPath,
        IReadOnlyDictionary<string, string> mediaPaths) => new()
        {
            Summary = new AttemptSummary
            {
                Id = importId,
                SessionId = importId,
                StartedAt = StartedAt,
                EndedAt = EndedAt,
                Outcome = Outcome,
                Grade = Grade,
                Accuracy = Accuracy,
                Score = Score,
                Pp = Pp,
                Combo = Combo,
                BeatmapMaxCombo = Map.MaxCombo,
                Misses = Misses,
                Key1Count = Results.Key1Count,
                Key2Count = Results.Key2Count,
                ModsKey = ModsKey,
                Mods = Mods.Select(mod => new ModEntry(mod.Acronym, mod.SettingsJson)).ToArray(),
                Artist = Map.Artist,
                Title = Map.Title,
                Difficulty = Map.Difficulty,
                Mapper = Map.Mapper,
                Stars = Map.BaseStars,
                AdjustedStars = Map.AdjustedStars,
                Progress = Progress,
                OsuBeatmapId = Map.BeatmapId,
                BeatmapSetId = Map.SetId,
                Checksum = Map.Checksum,
                HasMovement = Movement.SampleCount > 0,
                PlayerName = playerName,
                SharedByPlayerName = playerName,
                ImportedAt = importedAt,
                LocalBeatmapPath = beatmapPath,
                LocalBackgroundPath = backgroundPath,
            },
            N300 = Results.N300,
            N100 = Results.N100,
            N50 = Results.N50,
            Geki = Results.Geki,
            Katu = Results.Katu,
            SliderBreaks = Results.SliderBreaks,
            LargeTickHits = Results.LargeTickHits,
            LargeTickMisses = Results.LargeTickMisses,
            SmallTickHits = Results.SmallTickHits,
            SmallTickMisses = Results.SmallTickMisses,
            SliderTailHits = Results.SliderTailHits,
            SliderTailMisses = Results.SliderTailMisses,
            UnstableRate = Results.UnstableRate,
            FcPp = Results.FcPp,
            MaxPp = Results.MaxPp,
            DurationSeconds = Results.DurationSeconds,
            TerminationEvidence = Results.TerminationEvidence,
            Key1Count = Results.Key1Count,
            Key2Count = Results.Key2Count,
            Key1Binding = Results.Key1Binding,
            Key2Binding = Results.Key2Binding,
            BaseStars = Map.BaseStars,
            AdjustedStars = Map.AdjustedStars,
            Mapper = Map.Mapper,
            BeatmapAr = Map.Ar,
            BeatmapCs = Map.Cs,
            BeatmapOd = Map.Od,
            BeatmapHp = Map.Hp,
            Bpm = Map.Bpm,
            BeatmapMaxCombo = Map.MaxCombo,
            Mods = Mods.Select(mod => new ModEntry(mod.Acronym, mod.SettingsJson)).ToArray(),
            Timing = Timing is null ? null : new TimingSummary
            {
                HitCount = Timing.HitCount,
                EarlyCount = Timing.EarlyCount,
                LateCount = Timing.LateCount,
                Mean = Timing.Mean,
                Median = Timing.Median,
                Deviation = Timing.Deviation,
                Offsets = Timing.Offsets,
            },
            Events = Events.Select((e, index) => new JudgementEvent
            {
                Id = index + 1,
                EventType = e.EventType,
                MapTimeMs = e.MapTimeMs,
                Value = e.Value,
                DataJson = e.DataJson,
            }).ToArray(),
            Input = Input is null ? null : new InputSummary
            {
                Key1Presses = Input.Key1Presses,
                Key2Presses = Input.Key2Presses,
                Alternations = Input.Alternations,
                SimultaneousPresses = Input.SimultaneousPresses,
                Key1HoldMs = Input.Key1HoldMs,
                Key2HoldMs = Input.Key2HoldMs,
                PeakKps = Input.PeakKps,
                AverageKps = Input.AverageKps,
            },
            Movement = new MovementSummary
            {
                Available = Movement.SampleCount > 0,
                Source = Movement.Source,
                SampleRate = Movement.SampleRate,
                SampleCount = Movement.SampleCount,
                DroppedSamples = Movement.DroppedSamples,
            },
            CapturedDifficulty = CapturedDifficulty.ToDictionary(
            pair => pair.Key,
            pair => new DifficultyPair(pair.Value.Original, pair.Value.Converted),
            StringComparer.OrdinalIgnoreCase),
            LocalBeatmapPath = beatmapPath,
            LocalMediaDirectory = Path.GetDirectoryName(beatmapPath),
            LocalMediaPaths = mediaPaths,
            LocalBackgroundPath = backgroundPath,
            SharedByPlayerName = playerName,
            ImportedAt = importedAt,
            ClientKind = "imported",
        };
}

public sealed record SharedMapV1
{
    [JsonPropertyName("artist")] public string Artist { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("difficulty")] public string Difficulty { get; init; } = "";
    [JsonPropertyName("mapper")] public string Mapper { get; init; } = "";
    [JsonPropertyName("beatmap_id")] public long? BeatmapId { get; init; }
    [JsonPropertyName("set_id")] public long? SetId { get; init; }
    [JsonPropertyName("checksum")] public string? Checksum { get; init; }
    [JsonPropertyName("base_stars")] public double? BaseStars { get; init; }
    [JsonPropertyName("adjusted_stars")] public double? AdjustedStars { get; init; }
    [JsonPropertyName("ar")] public double? Ar { get; init; }
    [JsonPropertyName("cs")] public double? Cs { get; init; }
    [JsonPropertyName("od")] public double? Od { get; init; }
    [JsonPropertyName("hp")] public double? Hp { get; init; }
    [JsonPropertyName("bpm")] public double? Bpm { get; init; }
    [JsonPropertyName("max_combo")] public int MaxCombo { get; init; }
}

public sealed record SharedResultsV1
{
    [JsonPropertyName("n300")] public int N300 { get; init; }
    [JsonPropertyName("n100")] public int N100 { get; init; }
    [JsonPropertyName("n50")] public int N50 { get; init; }
    [JsonPropertyName("geki")] public int Geki { get; init; }
    [JsonPropertyName("katu")] public int Katu { get; init; }
    [JsonPropertyName("slider_breaks")] public int SliderBreaks { get; init; }
    [JsonPropertyName("large_tick_hits")] public int LargeTickHits { get; init; }
    [JsonPropertyName("large_tick_misses")] public int LargeTickMisses { get; init; }
    [JsonPropertyName("small_tick_hits")] public int SmallTickHits { get; init; }
    [JsonPropertyName("small_tick_misses")] public int SmallTickMisses { get; init; }
    [JsonPropertyName("slider_tail_hits")] public int SliderTailHits { get; init; }
    [JsonPropertyName("slider_tail_misses")] public int SliderTailMisses { get; init; }
    [JsonPropertyName("unstable_rate")] public double UnstableRate { get; init; }
    [JsonPropertyName("fc_pp")] public double FcPp { get; init; }
    [JsonPropertyName("max_pp")] public double MaxPp { get; init; }
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("termination_evidence")] public string? TerminationEvidence { get; init; }
    [JsonPropertyName("key1_count")] public int Key1Count { get; init; }
    [JsonPropertyName("key2_count")] public int Key2Count { get; init; }
    [JsonPropertyName("key1_binding")] public string Key1Binding { get; init; } = "Z";
    [JsonPropertyName("key2_binding")] public string Key2Binding { get; init; } = "X";
}

public sealed record SharedModV1(
    [property: JsonPropertyName("acronym")] string Acronym,
    [property: JsonPropertyName("settings_json")] string SettingsJson);

public sealed record SharedTimingV1
{
    [JsonPropertyName("hit_count")] public int HitCount { get; init; }
    [JsonPropertyName("early_count")] public int EarlyCount { get; init; }
    [JsonPropertyName("late_count")] public int LateCount { get; init; }
    [JsonPropertyName("mean")] public double Mean { get; init; }
    [JsonPropertyName("median")] public double Median { get; init; }
    [JsonPropertyName("deviation")] public double Deviation { get; init; }
    [JsonPropertyName("offsets")] public IReadOnlyList<double> Offsets { get; init; } = [];
}

public sealed record SharedEventV1
{
    [JsonPropertyName("event_type")] public string EventType { get; init; } = "";
    [JsonPropertyName("map_time_ms")] public long? MapTimeMs { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
    [JsonPropertyName("data_json")] public string DataJson { get; init; } = "{}";
}

public sealed record SharedInputV1
{
    [JsonPropertyName("key1_presses")] public int Key1Presses { get; init; }
    [JsonPropertyName("key2_presses")] public int Key2Presses { get; init; }
    [JsonPropertyName("alternations")] public int Alternations { get; init; }
    [JsonPropertyName("simultaneous_presses")] public int SimultaneousPresses { get; init; }
    [JsonPropertyName("key1_hold_ms")] public double Key1HoldMs { get; init; }
    [JsonPropertyName("key2_hold_ms")] public double Key2HoldMs { get; init; }
    [JsonPropertyName("peak_kps")] public int PeakKps { get; init; }
    [JsonPropertyName("average_kps")] public double AverageKps { get; init; }
}

public sealed record SharedDifficultyV1(
    [property: JsonPropertyName("original")] double? Original,
    [property: JsonPropertyName("converted")] double? Converted);

public sealed record SharedMovementV1
{
    [JsonPropertyName("source")] public string Source { get; init; } = "shared";
    [JsonPropertyName("sample_rate")] public double SampleRate { get; init; }
    [JsonPropertyName("sample_count")] public int SampleCount { get; init; }
    [JsonPropertyName("dropped_samples")] public int DroppedSamples { get; init; }
    [JsonPropertyName("replay_status")] public string ReplayStatus { get; init; } = "available";
}

public sealed record KumoriPackagePreview(
    string Path,
    string Fingerprint,
    string PlayerName,
    SharedPlayV1 Play,
    DateTimeOffset ExportedAt,
    long PackageSize,
    IReadOnlyList<string> OptionalMediaOmissions);

public sealed record KumoriImportResult(
    long ImportId,
    bool AlreadyImported,
    AttemptDetails Details,
    int ReusedLocalAssetCount = 0,
    long ReusedLocalAssetBytes = 0);

public sealed record ShareMediaFile(string LogicalName, string Role, string Path);
