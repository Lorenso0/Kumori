using System.Security.Cryptography;
using Kumori.ReplayViewer;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Replays;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osuTK;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class OsrComparisonImporterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"kumori-osr-comparison-{Guid.NewGuid():N}");

    [Fact]
    public void ImportValidatesBeatmapAndKeepsReplayEphemeral()
    {
        Directory.CreateDirectory(directory);
        string beatmapPath = Path.Combine(directory, "map.osu");
        File.WriteAllText(beatmapPath, """
            osu file format v14

            [General]
            Mode:0

            [Metadata]
            Title:Comparison test
            Artist:Kumori
            Creator:Tests
            Version:Normal

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:5
            ApproachRate:5

            [TimingPoints]
            0,500,4,2,1,60,1,0

            [HitObjects]
            256,192,1000,1,0,0:0:0:0:
            """);

        string hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(beatmapPath))).ToLowerInvariant();
        Beatmap beatmap;
        using (var stream = File.OpenRead(beatmapPath))
        using (var reader = new LineBufferedReader(stream))
            beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        beatmap.BeatmapInfo.MD5Hash = hash;

        var ruleset = new OsuRuleset();
        var score = new Score
        {
            ScoreInfo = new ScoreInfo
            {
                BeatmapInfo = beatmap.BeatmapInfo,
                Ruleset = ruleset.RulesetInfo,
                User = new APIUser { Username = "comparison-test" },
                Date = DateTimeOffset.UtcNow,
                TotalScore = 123456,
                MaxCombo = 1,
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 1 },
            },
            Replay = new Replay(),
        };
        score.Replay.Frames.Add(new OsuReplayFrame(900, new Vector2(240, 180)));
        score.Replay.Frames.Add(new OsuReplayFrame(1000, new Vector2(256, 192), OsuAction.LeftButton));

        string replayPath = Path.Combine(directory, "comparison.osr");
        using (var stream = File.Create(replayPath))
            new LegacyScoreEncoder(score, beatmap).Encode(stream);

        ComparisonContract imported = OsrComparisonImporter.Import(replayPath, beatmapPath, new AttemptContract
        {
            ModsKey = "NM",
            ClockRate = 1,
            MaxCombo = 1,
        });

        Assert.True(imported.Ephemeral);
        Assert.Equal(OsrComparisonImporter.EphemeralAttemptId, imported.AttemptId);
        Assert.Equal("comparison.osr", imported.SourceName);
        Assert.Equal(123456, imported.Score);
        Assert.Equal(1, imported.MaxCombo);
        Assert.Equal(1, imported.N300);
        Assert.Equal(2, imported.Samples.Count);

        string differentMap = Path.Combine(directory, "different.osu");
        File.WriteAllText(differentMap, File.ReadAllText(beatmapPath) + Environment.NewLine);
        Assert.Throws<InvalidDataException>(() => OsrComparisonImporter.Import(
            replayPath, differentMap, new AttemptContract { ModsKey = "NM", ClockRate = 1 }));
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { }
    }
}
