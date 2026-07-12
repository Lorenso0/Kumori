using Kumori.Native;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class StableReplayFrameEmissionCursorTests
{
    [Fact]
    public void RotatedListContinuesWithGlobalSequence()
    {
        var cursor = new StableReplayFrameEmissionCursor();
        var first = cursor.TakeNew([Frame(10), Frame(20), Frame(30)], out bool firstRotated);
        var second = cursor.TakeNew([Frame(40), Frame(50)], out bool secondRotated);

        Assert.False(firstRotated);
        Assert.True(secondRotated);
        Assert.Equal([1L, 2L, 3L], first.Select(frame => frame.Sequence));
        Assert.Equal([4L, 5L], second.Select(frame => frame.Sequence));
        Assert.Equal([40d, 50d], second.Select(frame => frame.MapTimeMs));
    }

    [Fact]
    public void TransientShortPrefixDoesNotReplayOldFrames()
    {
        var cursor = new StableReplayFrameEmissionCursor();
        cursor.TakeNew([Frame(10), Frame(20), Frame(30)], out _);

        Assert.Empty(cursor.TakeNew([Frame(10), Frame(20)], out bool rotated));
        Assert.False(rotated);

        var resumed = cursor.TakeNew([Frame(10), Frame(20), Frame(30), Frame(40)], out rotated);
        Assert.False(rotated);
        Assert.Equal(4, Assert.Single(resumed).Sequence);
    }

    [Fact]
    public void RotationPreservesNewStateAtSameTimestamp()
    {
        var cursor = new StableReplayFrameEmissionCursor();
        cursor.TakeNew([Frame(10), Frame(20) with { LeftPressed = true }], out _);

        var resumed = cursor.TakeNew([
            Frame(20) with { LeftPressed = true },
            Frame(20) with { RightPressed = true },
            Frame(30),
        ], out bool rotated);

        Assert.True(rotated);
        Assert.Equal([20d, 30d], resumed.Select(frame => frame.MapTimeMs));
        Assert.True(resumed[0].RightPressed);
        Assert.Equal([3L, 4L], resumed.Select(frame => frame.Sequence));
    }

    [Fact]
    public void FrameThatAppearsLateInsideAnEmittedPrefixIsRecovered()
    {
        var cursor = new StableReplayFrameEmissionCursor();
        cursor.TakeNew([Frame(10), Frame(30)], out _);

        var recovered = cursor.TakeNew([Frame(10), Frame(20) with { LeftPressed = true }, Frame(30)], out _);

        LazerReplayFrame frame = Assert.Single(recovered);
        Assert.Equal(20, frame.MapTimeMs);
        Assert.True(frame.LeftPressed);
        Assert.Equal(3, frame.Sequence);
    }

    private static LazerReplayFrame Frame(double time) => new()
    {
        MapTimeMs = time,
        MonotonicMs = time,
        X = time,
        Y = time,
    };
}
