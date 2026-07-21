using System.Text.Json;
using Kumori.Gameplay;
using Kumori.ReplayViewer;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Utils;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class BpmAdjustSupportTests
{
    [Theory]
    [InlineData(500, 180, 1.5)] // 120 -> 180
    [InlineData(300, 150, 0.75)] // 200 -> 150
    [InlineData(500, 174.5, 174.5 / 120)]
    public void ModDerivesGameplayRateFromBeatmap(double beatLength, double targetBpm, double expectedRate)
    {
        Beatmap beatmap = beatmapWithTiming($"0,{beatLength},4,2,1,60,1,0");
        var mod = new OsuModBpmAdjust(
            beatmap,
            new BpmAdjustSettings(targetBpm, BpmAdjustAudioMode.PreservePitch, true));

        Assert.Equal(60000 / beatLength, mod.SourceBpm, 6);
        Assert.Equal(expectedRate, mod.SpeedChange.Value, 6);
    }

    [Fact]
    public void ReplayPlayerCanDeepCloneBpmModWithoutLosingSettings()
    {
        var original = new OsuModBpmAdjust(
            beatmapWithTiming("0,500,4,2,1,60,1,0"),
            new BpmAdjustSettings(180, BpmAdjustAudioMode.Nightcore, false));

        var clone = Assert.IsType<OsuModBpmAdjust>(original.DeepClone());

        Assert.Equal(180, clone.TargetBpm);
        Assert.Equal(120, clone.SourceBpm, 6);
        Assert.Equal(1.5, clone.SpeedChange.Value, 6);
        Assert.Equal(BpmAdjustAudioMode.Nightcore, clone.AudioMode);
        Assert.False(clone.ScaleMapStatsWithBpm);
        Assert.Equal("180", clone.ExtendedIconInformation);
        Assert.True(ModUtils.CheckModsBelongToRuleset(new OsuRuleset(), [clone]));
    }

    [Fact]
    public void MissingSettingsUseForkDefaultsAndRemainNeutral()
    {
        BpmAdjustSettings settings = BpmAdjustSettings.Parse("{}");
        var mod = new OsuModBpmAdjust(
            beatmapWithTiming("0,500,4,2,1,60,1,0"),
            settings);

        Assert.Null(settings.TargetBpm);
        Assert.Equal(BpmAdjustAudioMode.PreservePitch, settings.AudioMode);
        Assert.True(settings.ScaleMapStatsWithBpm);
        Assert.Equal(1, mod.SpeedChange.Value);
    }

    [Theory]
    [InlineData("0", BpmAdjustAudioMode.PreservePitch)]
    [InlineData("\"0\"", BpmAdjustAudioMode.PreservePitch)]
    [InlineData("1", BpmAdjustAudioMode.AdjustPitch)]
    [InlineData("\"AdjustPitch\"", BpmAdjustAudioMode.AdjustPitch)]
    [InlineData("\"adjust_pitch\"", BpmAdjustAudioMode.AdjustPitch)]
    [InlineData("2", BpmAdjustAudioMode.Nightcore)]
    [InlineData("\"Nightcore\"", BpmAdjustAudioMode.Nightcore)]
    public void AudioModeAcceptsNumericAndStringRepresentations(
        string serializedMode,
        BpmAdjustAudioMode expected)
    {
        BpmAdjustSettings settings = BpmAdjustSettings.Parse(
            $$"""{"target_bpm":"174.5","audio_mode":{{serializedMode}},"scale_map_stats_with_bpm":"false"}""");

        Assert.Equal(174.5, settings.TargetBpm);
        Assert.Equal(expected, settings.AudioMode);
        Assert.False(settings.ScaleMapStatsWithBpm);
        Assert.Equal(174.5 / 120, settings.ClockRate(120), 6);
    }

    [Fact]
    public void VariableBpmUsesLongestUninheritedTimingSection()
    {
        Beatmap beatmap = BpmAdjustBeatmap.Decode(fixturePath());
        var mod = new OsuModBpmAdjust(
            beatmap,
            new BpmAdjustSettings(180, BpmAdjustAudioMode.PreservePitch, true));

        // 120 BPM lasts for 1 second; 150 BPM lasts through the remaining map.
        Assert.Equal(150, mod.SourceBpm, 6);
        Assert.Equal(1.2, mod.SpeedChange.Value, 6);
    }

    [Fact]
    public void DifficultyCalculatorAppliesCapturedBpmSettings()
    {
        string path = fixturePath();
        BeatmapDifficultyResult result = BeatmapDifficultyCalculator.Calculate(
            path,
            [new CapturedMod("BPM", """{"target_bpm":240,"scale_map_stats_with_bpm":true}""")]);

        Assert.True(result.BaseStars > 0);
        Assert.NotEqual(result.BaseStars, result.AdjustedStars, precision: 8);
    }

    [Fact]
    public void StatScalingEnabledLeavesMapStatsForNormalRateAdjustment()
    {
        var difficulty = new BeatmapDifficulty { ApproachRate = 9, OverallDifficulty = 8 };
        var mod = createOnePointFiveMod(scaleStats: true);

        mod.ApplyToDifficulty(difficulty);

        Assert.Equal(9, difficulty.ApproachRate);
        Assert.Equal(8, difficulty.OverallDifficulty);
    }

    [Fact]
    public void StatScalingDisabledPreservesRealTimeArAndOd()
    {
        const double rate = 1.5;
        var difficulty = new BeatmapDifficulty { ApproachRate = 9, OverallDifficulty = 8 };
        var mod = createOnePointFiveMod(scaleStats: false);

        mod.ApplyToDifficulty(difficulty);

        double originalPreempt = IBeatmapDifficultyInfo.DifficultyRange(9, OsuHitObject.PREEMPT_RANGE);
        double adjustedPreempt = IBeatmapDifficultyInfo.DifficultyRange(
            difficulty.ApproachRate,
            OsuHitObject.PREEMPT_RANGE);
        double originalGreat = IBeatmapDifficultyInfo.DifficultyRange(8, OsuHitWindows.GREAT_WINDOW_RANGE);
        double adjustedGreat = IBeatmapDifficultyInfo.DifficultyRange(
            difficulty.OverallDifficulty,
            OsuHitWindows.GREAT_WINDOW_RANGE);

        Assert.Equal(originalPreempt, adjustedPreempt / rate, 4);
        Assert.Equal(originalGreat, adjustedGreat / rate, 4);

        Beatmap beatmap = beatmapWithTiming("0,500,4,2,1,60,1,0");
        var displayMod = new OsuModBpmAdjust(
            beatmap,
            new BpmAdjustSettings(180, BpmAdjustAudioMode.PreservePitch, false));
        BeatmapDifficulty display = new OsuRuleset().GetAdjustedDisplayDifficulty(
            beatmap.BeatmapInfo,
            [displayMod]);
        Assert.Equal(9, display.ApproachRate, 3);
        Assert.Equal(8, display.OverallDifficulty, 3);
    }

    [Fact]
    public void ReplayAdapterReconstructsBpmModFromLocalScoreContract()
    {
        Beatmap beatmap = beatmapWithTiming("0,500,4,2,1,60,1,0");
        var attempt = new AttemptContract
        {
            ModsKey = "BPM",
            Mods =
            [
                new ModContract
                {
                    Acronym = "BPM",
                    Settings = settings(
                        """{"target_bpm":180,"audio_mode":"Nightcore","scale_map_stats_with_bpm":false}"""),
                },
            ],
        };

        var mod = Assert.IsType<OsuModBpmAdjust>(
            Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt, beatmap)));

        Assert.Equal(1.5, mod.SpeedChange.Value, 6);
        Assert.Equal(BpmAdjustAudioMode.Nightcore, mod.AudioMode);
        Assert.False(mod.ScaleMapStatsWithBpm);
    }

    private static OsuModBpmAdjust createOnePointFiveMod(bool scaleStats) => new(
        beatmapWithTiming("0,500,4,2,1,60,1,0"),
        new BpmAdjustSettings(180, BpmAdjustAudioMode.PreservePitch, scaleStats));

    private static Beatmap beatmapWithTiming(string timingPoint)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kumori-bpm-{Guid.NewGuid():N}.osu");
        File.WriteAllText(path, $$"""
            osu file format v14

            [General]
            Mode: 0

            [Metadata]
            Title:Test
            Artist:Kumori
            Creator:Tests
            Version:BPM

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:8
            ApproachRate:9
            SliderMultiplier:1.4
            SliderTickRate:1

            [TimingPoints]
            {{timingPoint}}

            [HitObjects]
            256,192,1000,1,0,0:0:0:0:
            256,192,5000,1,0,0:0:0:0:
            """);
        try
        {
            return BpmAdjustBeatmap.Decode(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Dictionary<string, JsonElement> settings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private static string fixturePath() => Path.Combine(
        findRepositoryRoot(),
        "tests",
        "Kumori.Core.Tests",
        "Fixtures",
        "bpm-adjust-variable.osu");

    private static string findRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Kumori.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
