using System.Globalization;
using Kumori.App.Controls;
using Kumori.Core.Models;
using Xunit;

namespace Kumori.App.Tests;

public sealed class MapPressureGraphTests
{
    [Fact]
    public void BuildDifficultyCurveUsesRateAdjustedSectionAlignment()
    {
        var root = FindRepositoryRoot();
        var beatmapPath = Path.Combine(
            root,
            "tests",
            "Kumori.App.Tests",
            "Fixtures",
            "difficulty-curve.osu");
        var firstObjectTime = ReadFirstHitObjectTime(beatmapPath);
        var mods = new[] { new ModEntry("DT", """{"speed_change":1.5}""") };

        var curve = MapPressureGraph.BuildDifficultyCurve(beatmapPath, mods);

        Assert.True(curve.Count > 2);
        Assert.Equal(firstObjectTime, curve[0].TimeMs);
        Assert.Equal(600, curve[1].TimeMs - curve[0].TimeMs);
        Assert.All(curve, point => Assert.InRange(point.Value, 0, 1));
        Assert.Contains(curve, point => point.Value >= 0.999);
    }

    [Fact]
    public void SelectGraphEventsDropsAbandonedRetryPrefixBeyondFinalMissCount()
    {
        var details = new AttemptDetails
        {
            Summary = new AttemptSummary { Misses = 2 },
            Events =
            [
                new JudgementEvent { Id = 10, EventType = "miss", MapTimeMs = 245, Value = 1 },
                new JudgementEvent { Id = 11, EventType = "miss", MapTimeMs = 540, Value = 2 },
                new JudgementEvent { Id = 20, EventType = "miss", MapTimeMs = 11_073, Value = 1 },
                new JudgementEvent { Id = 21, EventType = "miss", MapTimeMs = 11_993, Value = 2 },
                new JudgementEvent { Id = 22, EventType = "slider_break", MapTimeMs = 12_500, Value = 1 },
            ],
        };

        var events = MapPressureGraph.SelectGraphEvents(details);

        Assert.Equal(new long[] { 20, 21 }, events.Where(e => e.EventType == "miss").Select(e => e.Id));
        Assert.Contains(events, e => e.Id == 22 && e.EventType == "slider_break");
    }

    [Fact]
    public void SelectGraphEventsKeepsAllMissesWhenCountMatchesFinalResult()
    {
        var details = new AttemptDetails
        {
            Summary = new AttemptSummary { Misses = 2 },
            Events =
            [
                new JudgementEvent { Id = 10, EventType = "miss", MapTimeMs = 245 },
                new JudgementEvent { Id = 11, EventType = "miss", MapTimeMs = 540 },
            ],
        };

        Assert.Equal(2, MapPressureGraph.SelectGraphEvents(details).Length);
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kumori.sln")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate Kumori.sln.");
    }

    private static int ReadFirstHitObjectTime(string beatmapPath)
    {
        var inObjects = false;
        foreach (var raw in File.ReadLines(beatmapPath))
        {
            var line = raw.Trim();
            if (line == "[HitObjects]")
            {
                inObjects = true;
                continue;
            }

            if (!inObjects || line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }

            var fields = line.Split(',');
            return int.Parse(fields[2], CultureInfo.InvariantCulture);
        }

        throw new InvalidDataException("Beatmap has no hit objects.");
    }
}
