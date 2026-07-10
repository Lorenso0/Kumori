using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class SessionTrackerTests
{
    private readonly RecordingSessionSink _sink = new();
    private readonly SessionTracker _tracker;

    public SessionTrackerTests()
    {
        _tracker = new SessionTracker(_sink);
    }

    [Fact]
    public void StartsOnFirstStandardGameplay()
    {
        _tracker.Ingest(Frame(0, playing: false));
        _tracker.Ingest(Frame(1, playing: true));

        Assert.Single(_sink.Starts);
        Assert.True(_tracker.HasSession);
    }

    [Fact]
    public void AccumulatesOnlyWhilePreviousFrameWasPlaying()
    {
        _tracker.Ingest(Frame(0, playing: true));
        _tracker.Ingest(Frame(0.4, playing: true));
        _tracker.Ingest(Frame(2.0, playing: true)); // clamped to 1.0
        _tracker.Ingest(Frame(2.2, playing: false));
        _tracker.Ingest(Frame(3.2, playing: false));

        Assert.Equal(3, _sink.ActiveSeconds.Count);
        Assert.Equal(0.4, _sink.ActiveSeconds[0], precision: 6);
        Assert.Equal(1.0, _sink.ActiveSeconds[1], precision: 6);
        Assert.Equal(0.2, _sink.ActiveSeconds[2], precision: 6);
        Assert.Equal(1.6, _sink.ActiveSeconds.Sum(), precision: 6);
    }

    [Fact]
    public void NonStandardFramesAreIgnored()
    {
        _tracker.Ingest(Frame(0, playing: true) with { IsStandardMode = false });

        Assert.Empty(_sink.Starts);
    }

    [Fact]
    public void OsuClosePromptsOnceThenEndsInterruptedAfterGrace()
    {
        _tracker.Ingest(Frame(0, playing: true));
        _tracker.Ingest(Frame(10, playing: false, osuRunning: false));
        _tracker.Ingest(Frame(100, playing: false, osuRunning: false));
        _tracker.Ingest(Frame(610.1, playing: false, osuRunning: false));

        Assert.Single(_sink.Prompts);
        var end = Assert.Single(_sink.Ends);
        Assert.True(end.Interrupted);
        Assert.False(_tracker.HasSession);
    }

    [Fact]
    public void OsuReturnWithinGraceCancelsClosePromptWindow()
    {
        _tracker.Ingest(Frame(0, playing: true));
        _tracker.Ingest(Frame(10, playing: false, osuRunning: false));
        _tracker.Ingest(Frame(20, playing: false, osuRunning: true));
        _tracker.Ingest(Frame(620, playing: true, osuRunning: true));

        Assert.Single(_sink.Prompts);
        Assert.Empty(_sink.Ends);
        Assert.True(_tracker.HasSession);
    }

    [Fact]
    public void EndCleanEndsWithoutInterruptedFlag()
    {
        _tracker.Ingest(Frame(0, playing: true));
        _tracker.EndClean(100, 10);

        Assert.False(Assert.Single(_sink.Ends).Interrupted);
    }

    [Fact]
    public void EndInterruptedEndsImmediately()
    {
        _tracker.Ingest(Frame(0, playing: true));
        _tracker.EndInterrupted(10, 10);

        Assert.True(Assert.Single(_sink.Ends).Interrupted);
        Assert.False(_tracker.HasSession);
    }

    private static SessionTracker.Frame Frame(
        double t,
        bool playing,
        bool osuRunning = true) => new()
    {
        WallTime = 1_788_000_000 + t,
        MonoTime = t,
        IsPlaying = playing,
        OsuRunning = osuRunning,
    };

    private sealed class RecordingSessionSink : ISessionSink
    {
        public List<SessionStart> Starts { get; } = new();
        public List<double> ActiveSeconds { get; } = new();
        public List<SessionClosePrompt> Prompts { get; } = new();
        public List<SessionEnd> Ends { get; } = new();

        public void StartSession(SessionStart start) => Starts.Add(start);
        public void AddActiveSeconds(double seconds) => ActiveSeconds.Add(seconds);
        public void PromptOsuClosed(SessionClosePrompt prompt) => Prompts.Add(prompt);
        public void EndSession(SessionEnd end) => Ends.Add(end);
    }
}
