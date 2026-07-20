using System.Text.Json;
using Kumori.ReplayViewer;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Mods;
using Xunit;

namespace Kumori.Core.Tests;

public class LazerReplayAdapterTests
{
    [Fact]
    public void CreateCapturedMods_DoubleTimeUsesDefaultGameplayRate()
    {
        var attempt = new AttemptContract
        {
            ModsKey = "DTCL",
            Mods =
            [
                new ModContract { Acronym = "DT" },
                new ModContract { Acronym = "CL" },
            ],
        };

        var mods = LazerReplayAdapter.CreateCapturedMods(attempt);
        var doubleTime = Assert.IsAssignableFrom<ModRateAdjust>(
            Assert.Single(mods, mod => mod is OsuModDoubleTime));

        Assert.Equal(1.5, doubleTime.SpeedChange.Value, 3);
    }

    [Fact]
    public void CreateCapturedMods_PreservesCustomDoubleTimeRate()
    {
        var attempt = new AttemptContract
        {
            ModsKey = "DT",
            Mods =
            [
                new ModContract
                {
                    Acronym = "DT",
                    Settings = Settings("""{ "speed_change": 2.0 }"""),
                },
            ],
        };

        var doubleTime = Assert.IsAssignableFrom<ModRateAdjust>(
            Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));

        Assert.Equal(2.0, doubleTime.SpeedChange.Value, 3);
    }

    [Theory]
    [InlineData(null, 0.75)]
    [InlineData(0.5, 0.5)]
    public void CreateCapturedMods_PreservesHalfTimeRate(double? configuredRate, double expectedRate)
    {
        var attempt = new AttemptContract
        {
            ModsKey = "HT",
            Mods =
            [
                new ModContract
                {
                    Acronym = "HT",
                    Settings = configuredRate is null
                        ? []
                        : Settings($$"""{ "speed_change": {{configuredRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }"""),
                },
            ],
        };

        var halfTime = Assert.IsAssignableFrom<ModRateAdjust>(
            Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));

        Assert.IsType<OsuModHalfTime>(halfTime);
        Assert.Equal(expectedRate, halfTime.SpeedChange.Value, 3);
    }

    [Theory]
    [InlineData("NC", 1.8, typeof(OsuModNightcore))]
    [InlineData("DC", 0.6, typeof(OsuModDaycore))]
    public void CreateCapturedMods_PreservesAlternativeRateModSettings(string acronym, double rate, Type expectedType)
    {
        var attempt = new AttemptContract
        {
            ModsKey = acronym,
            Mods =
            [
                new ModContract
                {
                    Acronym = acronym,
                    Settings = Settings($$"""{ "speed_change": {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }"""),
                },
            ],
        };

        var mod = Assert.IsAssignableFrom<ModRateAdjust>(Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));
        Assert.IsType(expectedType, mod);
        Assert.Equal(rate, mod.SpeedChange.Value, 3);
    }

    [Theory]
    [InlineData("WU", 1.1, 1.9)]
    [InlineData("WD", 1.8, 0.7)]
    public void CreateCapturedMods_PreservesTimeRampSettings(string acronym, double initialRate, double finalRate)
    {
        var attempt = new AttemptContract
        {
            ModsKey = acronym,
            Mods =
            [
                new ModContract
                {
                    Acronym = acronym,
                    Settings = Settings($$"""{ "initial_rate": {{initialRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "final_rate": {{finalRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "adjust_pitch": true }"""),
                },
            ],
        };

        var mod = Assert.IsAssignableFrom<ModTimeRamp>(Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));
        Assert.Equal(initialRate, mod.InitialRate.Value, 3);
        Assert.Equal(finalRate, mod.FinalRate.Value, 3);
        Assert.True(mod.AdjustPitch.Value);
    }

    [Fact]
    public void CreateCapturedMods_PreservesAdaptiveSpeedSettings()
    {
        var attempt = new AttemptContract
        {
            ModsKey = "AS",
            Mods =
            [
                new ModContract
                {
                    Acronym = "AS",
                    Settings = Settings("""{ "initial_rate": 1.7, "adjust_pitch": false }"""),
                },
            ],
        };

        var mod = Assert.IsType<ModAdaptiveSpeed>(Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));
        Assert.Equal(1.7, mod.InitialRate.Value, 3);
        Assert.False(mod.AdjustPitch.Value);
    }

    [Fact]
    public void ResolveMods_MergesDecodedModMissingFromStructuredConversion()
    {
        var attempt = new AttemptContract
        {
            ModsKey = "DT",
            Mods = [new ModContract { Acronym = "DT", Settings = Settings("""{ "speed_change": 2.0 }""") }],
        };

        var mods = LazerReplayAdapter.ResolveMods(attempt, [new OsuModDoubleTime(), new OsuModNoFail()]);

        Assert.Equal(2, mods.Length);
        Assert.Equal(2.0, Assert.IsAssignableFrom<ModRateAdjust>(mods.Single(mod => mod.Acronym == "DT")).SpeedChange.Value, 3);
        Assert.Contains(mods, mod => mod is OsuModNoFail);
    }

    [Fact]
    public void ResolveMods_PrefersStructuredSettingsOverDecodedReplayFlags()
    {
        var attempt = new AttemptContract
        {
            ModsKey = "DTDA",
            Mods =
            [
                new ModContract
                {
                    Acronym = "DT",
                    Settings = Settings("""{ "speed_change": 2.0 }"""),
                },
                new ModContract
                {
                    Acronym = "DA",
                    Settings = Settings("""{ "circle_size": 3.5, "approach_rate": 10.4, "overall_difficulty": 9.2, "drain_rate": 6.1 }"""),
                },
            ],
        };

        var mods = LazerReplayAdapter.ResolveMods(attempt, [new OsuModDoubleTime()]);
        var rate = Assert.IsAssignableFrom<ModRateAdjust>(Assert.Single(mods, mod => mod is OsuModDoubleTime));
        var difficulty = Assert.IsType<OsuModDifficultyAdjust>(Assert.Single(mods, mod => mod is OsuModDifficultyAdjust));

        Assert.Equal(2.0, rate.SpeedChange.Value, 3);
        Assert.Equal(3.5f, difficulty.CircleSize.Value);
        Assert.Equal(10.4f, difficulty.ApproachRate.Value);
        Assert.Equal(9.2f, difficulty.OverallDifficulty.Value);
        Assert.Equal(6.1f, difficulty.DrainRate.Value);
    }

    [Fact]
    public void CreateCapturedMods_PassesClassicToReplayPlayer()
    {
        var attempt = new AttemptContract
        {
            ModsKey = "NFCL",
            Mods =
            [
                new ModContract { Acronym = "NF" },
                new ModContract { Acronym = "CL" },
            ],
        };

        var mods = LazerReplayAdapter.CreateCapturedMods(attempt);
        Assert.Contains(mods, mod => mod is OsuModNoFail);
        var classic = Assert.IsType<OsuModClassic>(Assert.Single(mods, mod => mod is OsuModClassic));
        Assert.True(classic.NoSliderHeadAccuracy.Value);
        Assert.True(classic.ClassicNoteLock.Value);
    }

    [Fact]
    public void CreateReplay_StableMemoryUsesFinalStateAtSharedTimestamp()
    {
        var contract = new ViewerContract
        {
            BeatmapPath = "unused.osu",
            Attempt = new AttemptContract { MovementSource = "stable_memory" },
            Samples =
            [
                new MovementSample { MapTimeMs = 100, MonotonicMs = 100, X = 10, Y = 20, Buttons = 0x10 },
                new MovementSample { MapTimeMs = 100, MonotonicMs = 101, X = 11, Y = 21, Buttons = 0 },
            ],
        };

        var frames = LazerReplayAdapter.CreateReplay(contract).Frames.OfType<OsuReplayFrame>().ToArray();

        OsuReplayFrame frame = Assert.Single(frames, candidate => Math.Abs(candidate.Time - 100) < 0.001);
        Assert.Empty(frame.Actions);
        Assert.Equal(11, frame.Position.X);
        Assert.Equal(21, frame.Position.Y);
    }

    [Fact]
    public void FitCapturedReplay_DoesNotRescaleLazerMemoryGameplayClockTimestamps()
    {
        var replay = new osu.Game.Replays.Replay();
        replay.Frames.Add(new OsuReplayFrame(
            12_407,
            new osuTK.Vector2(256, 192),
            OsuAction.LeftButton));

        LazerReplayAdapter.FitCapturedReplay(
            replay,
            firstHitTime: 530,
            lastHitTime: 151_655,
            clockRate: 1.875,
            movementSource: "lazer_memory");

        OsuReplayFrame actionFrame = Assert.Single(
            replay.Frames.OfType<OsuReplayFrame>(),
            frame => frame.Actions.Count > 0);
        Assert.Equal(12_407, actionFrame.Time);
    }

    [Fact]
    public void CreateCapturedMods_AppliesDifficultyAdjustSettingsFromStructuredMods()
    {
        var attempt = new AttemptContract
        {
            Mods =
            [
                new ModContract
                {
                    Acronym = "DA",
                    Settings = Settings("""
                    {
                        "circle_size": 4.2,
                        "approach_rate": 9.8,
                        "overall_difficulty": 8.7,
                        "drain_rate": 6.5
                    }
                    """),
                },
            ],
        };

        var da = Assert.IsType<OsuModDifficultyAdjust>(Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));
        Assert.Equal(4.2f, da.CircleSize.Value);
        Assert.Equal(9.8f, da.ApproachRate.Value);
        Assert.Equal(8.7f, da.OverallDifficulty.Value);
        Assert.Equal(6.5f, da.DrainRate.Value);
    }

    [Fact]
    public void CreateCapturedMods_AppliesDifficultyAdjustShortSettingNamesFromJsonKey()
    {
        var mods = LazerReplayAdapter.CreateCapturedMods("""
        [
            {
                "acronym": "DA",
                "settings": {
                    "cs": 3.8,
                    "ar": 10.2,
                    "od": 9.1,
                    "hp": 5.4
                }
            }
        ]
        """);

        var da = Assert.IsType<OsuModDifficultyAdjust>(Assert.Single(mods));
        Assert.Equal(3.8f, da.CircleSize.Value);
        Assert.Equal(10.2f, da.ApproachRate.Value);
        Assert.Equal(9.1f, da.OverallDifficulty.Value);
        Assert.Equal(5.4f, da.DrainRate.Value);
    }

    private static Dictionary<string, JsonElement> Settings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
                       .ToDictionary(property => property.Name, property => property.Value.Clone());
    }
}
