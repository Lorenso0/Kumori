using System.Text;
using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class LazerReplayFrameTests
{
    [Fact]
    public void TryParse_AcceptsActionsAndPositionObject()
    {
        var ok = LazerReplayFrameJson.TryParse("""
            {
              "type": "lazer_replay_frame",
              "time": 1234.5,
              "position": { "x": 256.25, "y": 192.75 },
              "actions": ["LeftButton"],
              "sequence": 42
            }
            """, out var frame);

        Assert.True(ok);
        Assert.Equal(1234.5, frame.MapTimeMs);
        Assert.Equal(256.25, frame.X);
        Assert.Equal(192.75, frame.Y);
        Assert.True(frame.LeftPressed);
        Assert.False(frame.RightPressed);
        Assert.True(frame.Focused);
        Assert.False(frame.Paused);
        Assert.Equal(42, frame.Sequence);
    }

    [Fact]
    public void TryParse_AcceptsFlatBooleanShape()
    {
        var ok = LazerReplayFrameJson.TryParse("""
            {
              "mapTimeMs": 50,
              "x": 12,
              "y": 34,
              "leftPressed": false,
              "rightPressed": true,
              "focused": false,
              "paused": true,
              "monotonicMs": 99
            }
            """, out var frame);

        Assert.True(ok);
        Assert.Equal(50, frame.MapTimeMs);
        Assert.False(frame.LeftPressed);
        Assert.True(frame.RightPressed);
        Assert.False(frame.Focused);
        Assert.True(frame.Paused);
        Assert.Equal(99, frame.MonotonicMs);
    }

    [Fact]
    public void Mapper_ProducesKumoriMovementSample()
    {
        var sample = LazerReplayFrameMapper.ToMovementSample(new LazerReplayFrame
        {
            MapTimeMs = 1000,
            MonotonicMs = 1100,
            X = 260.4,
            Y = 195.6,
            LeftPressed = true,
            RightPressed = true,
            Focused = true,
            Paused = false,
        });

        Assert.Equal(1000, sample.MapTimeMs);
        Assert.Equal(1100, sample.MonotonicMs);
        Assert.Equal(260.4, sample.X);
        Assert.Equal(195.6, sample.Y);
        Assert.Equal(0x30, sample.Buttons);
        Assert.Equal(1, sample.Flags);
        Assert.Equal(260, sample.RawX);
        Assert.Equal(196, sample.RawY);
        Assert.Equal((uint)0, sample.Pressure);
    }

    [Fact]
    public async Task JsonlSource_YieldsOnlyFrameLines()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"metadata","version":1}
            {"time":1,"x":2,"y":3,"actions":["RightButton"]}
            not json
            {"mapTimeMs":4,"x":5,"y":6,"leftPressed":true}
            """));
        var source = new JsonlLazerReplayFrameSource(() => stream);

        var frames = new List<LazerReplayFrame>();
        await foreach (var frame in source.ReadFramesAsync(CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].RightPressed);
        Assert.True(frames[1].LeftPressed);
    }

}
