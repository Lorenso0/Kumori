using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class TosuClientTests
{
    private static TosuPacket Packet(string raw, double mono = 100.0) =>
        new(raw, 1_700_000_000.0, mono);

    [Fact]
    public void Ingest_ParsesPlayingSnapshot()
    {
        var client = new TosuClient();
        TosuSnapshot? received = null;
        client.SnapshotReceived += s => received = s;

        client.Ingest(Packet("""
            {
                "state": {"number": 2, "name": "Play"},
                "play": {"mode": {"number": 0, "name": "osu"}},
                "beatmap": {
                    "id": 129891, "set": 39804,
                    "checksum": "abc123",
                    "artist": "xi", "title": "FREEDOM DiVE", "version": "FOUR DIMENSIONS",
                    "mapper": "Nakagawa-Kanon",
                    "time": {"live": 45120}
                }
            }
            """));

        Assert.NotNull(received);
        Assert.Equal("play", received!.State);
        Assert.True(received.IsPlaying);
        Assert.False(received.IsResults);
        Assert.True(received.IsStandardMode);
        Assert.Equal("abc123", received.BeatmapIdentity);
        Assert.Equal(45120, received.LiveTimeMs);
        Assert.Equal("xi — FREEDOM DiVE [FOUR DIMENSIONS]", received.BeatmapDisplay);
        Assert.Equal(1, client.PacketCount);
    }

    [Theory]
    [InlineData("""{"state": {"name": "ResultScreen"}}""", false, true)]
    [InlineData("""{"state": {"name": "Ranking"}}""", false, true)]
    [InlineData("""{"state": {"name": "Gameplay"}}""", true, false)]
    [InlineData("""{"state": "playing"}""", true, false)]
    [InlineData("""{"state": {"name": "SongSelect"}}""", false, false)]
    public void Ingest_NormalizesStates(string raw, bool playing, bool results)
    {
        var client = new TosuClient();
        client.Ingest(Packet(raw));
        Assert.Equal(playing, client.LastSnapshot!.IsPlaying);
        Assert.Equal(results, client.LastSnapshot.IsResults);
    }

    [Fact]
    public void Ingest_NonStandardMode_Flagged()
    {
        var client = new TosuClient();
        client.Ingest(Packet("""{"play": {"mode": {"number": 3, "name": "mania"}}}"""));
        Assert.False(client.LastSnapshot!.IsStandardMode);
    }

    [Fact]
    public void Ingest_DifferentProfileAndPlayerNames_FlagsWatchedReplay()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "profile": {"name": "OurPlayer"},
                "play": {"playerName": "OtherPlayer"}
            }
            """));

        var snapshot = client.LastSnapshot!;
        Assert.Equal("OurPlayer", snapshot.ProfileName);
        Assert.Equal("OtherPlayer", snapshot.PlayerName);
        Assert.True(snapshot.IsWatchedReplay);
    }

    [Fact]
    public void Ingest_PlayerNameComparison_IsCaseInsensitive()
    {
        var client = new TosuClient();
        client.Ingest(Packet("""{"profile":{"name":"OurPlayer"},"play":{"playerName":"ourplayer"}}"""));

        Assert.False(client.LastSnapshot!.IsWatchedReplay);
    }

    [Fact]
    public void Ingest_MissingModeAfterStandardPacket_PreservesStandardMode()
    {
        var client = new TosuClient();
        client.Ingest(Packet("""{"play":{"mode":{"number":0}}}"""));
        client.Ingest(Packet("""{"state":{"name":"ResultScreen"}}"""));

        Assert.True(client.LastSnapshot!.IsStandardMode);
    }

    [Fact]
    public void Ingest_ParsesCurrentTosuModsHitErrorsAndBeatmapStats()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "state": {"name": "Gameplay"},
                "play": {
                    "mode": {"number": 0, "name": "osu"},
                    "mods": {
                        "array": [
                            {"acronym": "DT", "settings": {"speed_change": 1.5}},
                            {"acronym": "DA", "settings": {"approach_rate": 9.8}}
                        ]
                    },
                    "hitErrorArray": [-12.5, 0, 18]
                },
                "beatmap": {
                    "id": 11,
                    "set": 22,
                    "checksum": "deadbeef",
                    "stats": {
                        "ar": {"converted": 9.8},
                        "cs": {"converted": 4},
                        "od": {"converted": 8.5},
                        "hp": {"converted": 6},
                        "bpm": {"realtime": 270},
                        "stars": {"original": 6.16, "total": 7.3},
                        "maxCombo": 1234
                    }
                }
            }
            """));

        var snapshot = client.LastSnapshot!;
        Assert.Equal("DTDA", snapshot.ModsKey);
        Assert.Equal(2, snapshot.Mods.Count);
        Assert.Contains("\"speed_change\"", snapshot.Mods[0].SettingsJson);
        Assert.Contains("1.5", snapshot.Mods[0].SettingsJson);
        Assert.Equal(new[] { -12.5, 0, 18 }, snapshot.Play.HitErrors);
        Assert.Equal(11, snapshot.BeatmapId);
        Assert.Equal(22, snapshot.BeatmapSetId);
        Assert.Equal("deadbeef", snapshot.Checksum);
        Assert.Equal(9.8, snapshot.BeatmapStats.ApproachRate);
        Assert.Equal(6.16, snapshot.BeatmapStats.BaseStars);
        Assert.Equal(7.3, snapshot.BeatmapStats.Stars);
        Assert.Equal(1234, snapshot.BeatmapStats.MaxCombo);
    }

    [Fact]
    public void Ingest_MalformedPacket_CountsInvalidWithoutThrowing()
    {
        var client = new TosuClient();
        string? error = null;
        client.PacketInvalid += e => error = e;

        client.Ingest(Packet("{not json"));

        Assert.Equal(1, client.InvalidPacketCount);
        Assert.Equal(0, client.PacketCount);
        Assert.NotNull(error);
        Assert.Null(client.LastSnapshot);
    }

    [Fact]
    public void Ingest_ParsesResultsScreenRankAndScore()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "state": {"name": "ResultScreen"},
                "resultsScreen": {"rank": "S", "score": 123456}
            }
            """));

        Assert.True(client.LastSnapshot!.IsResults);
        Assert.Equal("S", client.LastSnapshot.Grade);
        Assert.Equal(123456, client.LastSnapshot.Score);
    }

    [Fact]
    public void Ingest_ParsesPerformanceValuesFromResultsScreen()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "state": {"name": "ResultScreen"},
                "resultsScreen": {
                    "score": 123456,
                    "pp": {"current": 321.45, "fc": 400.5, "maxThisPlay": 350.25}
                }
            }
            """));

        var snapshot = client.LastSnapshot!;
        Assert.Equal(321.45, snapshot.Pp);
        Assert.Equal(400.5, snapshot.FcPp);
        Assert.Equal(350.25, snapshot.MaxPp);
    }

    [Fact]
    public void Ingest_ResultScreen_PrefersFinalPerformanceOverLiveEstimate()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "state": {"name": "ResultScreen"},
                "play": {
                    "mode": {"number": 0},
                    "pp": {"current": 250.0, "fc": 300.0}
                },
                "resultsScreen": {
                    "pp": {"current": 200.0, "fc": 275.0, "maxThisPlay": 310.0}
                }
            }
            """));

        var snapshot = client.LastSnapshot!;
        Assert.Equal(200.0, snapshot.Pp);
        Assert.Equal(275.0, snapshot.FcPp);
        Assert.Equal(310.0, snapshot.MaxPp);
    }

    [Fact]
    public void Ingest_IgnoresBlankGradeWhenRankIsAvailable()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "state": {"name": "ResultScreen"},
                "play": {"grade": "", "rank": "A", "mode": {"number": 0}},
                "resultsScreen": {"grade": "", "rank": "B", "score": 123456}
            }
            """));

        Assert.Equal("A", client.LastSnapshot!.Grade);
    }

    [Fact]
    public void Ingest_ParsesRichResultHitCounts()
    {
        var client = new TosuClient();

        client.Ingest(Packet("""
            {
                "state": {"name": "ResultScreen"},
                "play": {"mode": {"number": 0}},
                "score": {
                    "result": {
                        "hits": {
                            "geki": 2,
                            "katu": 3,
                            "largeTickHits": 74,
                            "largeTickMisses": 5,
                            "smallTickHits": 6,
                            "smallTickMisses": 7,
                            "sliderEndHits": 84,
                            "sliderEndMisses": 8
                        }
                    }
                }
            }
            """));

        var play = client.LastSnapshot!.Play;
        Assert.Equal(2, play.Geki);
        Assert.Equal(3, play.Katu);
        Assert.Equal(74, play.LargeTickHit);
        Assert.Equal(5, play.LargeTickMiss);
        Assert.Equal(6, play.SmallTickHit);
        Assert.Equal(7, play.SmallTickMiss);
        Assert.Equal(84, play.SliderTailHit);
        Assert.Equal(8, play.SliderTailMiss);
    }

    [Fact]
    public void Ingest_BeatmapChange_RaisesEventOnce()
    {
        var client = new TosuClient();
        var changes = 0;
        client.BeatmapChanged += _ => changes++;

        client.Ingest(Packet("""{"beatmap": {"checksum": "aaa"}}"""));
        client.Ingest(Packet("""{"beatmap": {"checksum": "aaa"}}"""));
        client.Ingest(Packet("""{"beatmap": {"checksum": "bbb"}}"""));

        Assert.Equal(2, changes); // initial detection + one change
    }

    [Fact]
    public void BeatmapIdentity_MatchesPythonPriority()
    {
        Assert.Equal("abc", TosuClient.BeatmapIdentity("abc", 5, "a", "t", "d", "m"));
        Assert.Equal("id:5", TosuClient.BeatmapIdentity("  ", 5, "a", "t", "d", "m"));
        Assert.Equal("a|t|d|m", TosuClient.BeatmapIdentity(null, null, "a", "t", "d", "m"));
        Assert.Equal("unknown", TosuClient.BeatmapIdentity(null, null, null, null, null, null));
    }
}
