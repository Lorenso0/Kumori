using Kumori.App;
using Kumori.Core.Settings;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ReplayRecoveryTestAttemptSinkTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"kumori-recovery-switch-{Guid.NewGuid():N}");

    [Fact]
    public void CompletedPlay_IsStrippedAndSwitchAutomaticallyDisarms()
    {
        SettingsService settings = CreateArmedSettings();
        var capture = new CaptureSink();
        var sink = new ReplayRecoveryTestAttemptSink(capture, settings);
        AttemptSnapshot snapshot = PopulatedSnapshot();

        sink.StartAttempt(new AttemptStart
        {
            Identity = "checksum:test",
            BeatmapStats = new BeatmapStats { Stars = 6 },
        });
        sink.Checkpoint(new AttemptCheckpoint(snapshot, [], false));
        sink.Finalize(new AttemptFinalization("completed", "results", snapshot, 1));

        Assert.NotNull(capture.LastStart);
        Assert.Null(capture.LastStart!.BeatmapStats.Stars);
        Assert.NotNull(capture.LastCheckpoint);
        AssertMissing(capture.LastCheckpoint!.Snapshot);
        Assert.NotNull(capture.LastFinalization);
        AssertMissing(capture.LastFinalization!.Snapshot);
        Assert.Equal(snapshot.DurationSeconds, capture.LastFinalization.Snapshot.DurationSeconds);
        Assert.Equal(snapshot.Progress, capture.LastFinalization.Snapshot.Progress);
        Assert.Equal(snapshot.ModsKey, capture.LastFinalization.Snapshot.ModsKey);
        Assert.False(settings.Current.Developer.ForceReplayRecoveryNextPlay);

        var reloaded = new SettingsService(
            Path.Combine(directory, "settings.v2.json"),
            Path.Combine(directory, "missing.json"));
        reloaded.Load();
        Assert.False(reloaded.Current.Developer.ForceReplayRecoveryNextPlay);
    }

    [Fact]
    public void NonCompletedPlay_DoesNotConsumeSwitchOrStripFinalResult()
    {
        SettingsService settings = CreateArmedSettings();
        var capture = new CaptureSink();
        var sink = new ReplayRecoveryTestAttemptSink(capture, settings);
        AttemptSnapshot snapshot = PopulatedSnapshot();

        sink.StartAttempt(new AttemptStart { Identity = "checksum:test" });
        sink.Finalize(new AttemptFinalization("retried", "retry", snapshot, 1));

        Assert.Equal(snapshot.Score, capture.LastFinalization!.Snapshot.Score);
        Assert.True(settings.Current.Developer.ForceReplayRecoveryNextPlay);
    }

    private SettingsService CreateArmedSettings()
    {
        Directory.CreateDirectory(directory);
        var settings = new SettingsService(
            Path.Combine(directory, "settings.v2.json"),
            Path.Combine(directory, "missing.json"));
        settings.Load();
        settings.Update(value => value.Developer.ForceReplayRecoveryNextPlay = true);
        return settings;
    }

    private static AttemptSnapshot PopulatedSnapshot() => new()
    {
        Identity = "checksum:test",
        DurationSeconds = 60,
        Score = 999_999,
        Accuracy = 98.5,
        Grade = "A",
        Pp = 200,
        FcPp = 220,
        MaxPp = 250,
        Combo = 500,
        N300 = 400,
        N100 = 20,
        N50 = 1,
        Misses = 2,
        Geki = 3,
        Katu = 4,
        SliderBreaks = 2,
        LargeTickHits = 30,
        LargeTickMisses = 2,
        SmallTickHits = 5,
        SmallTickMisses = 1,
        SliderTailHits = 20,
        SliderTailMisses = 2,
        UnstableRate = 123,
        Progress = 1,
        TimingOffsets = [-10, 5],
        BeatmapStats = new BeatmapStats { BaseStars = 5, Stars = 6, ApproachRate = 9 },
        ModsKey = "DT",
        Mods = [new AttemptMod("DT")],
    };

    private static void AssertMissing(AttemptSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.Score);
        Assert.Equal(0, snapshot.Accuracy);
        Assert.Null(snapshot.Grade);
        Assert.Equal(0, snapshot.Pp);
        Assert.Equal(0, snapshot.FcPp);
        Assert.Equal(0, snapshot.MaxPp);
        Assert.Equal(0, snapshot.Combo);
        Assert.Equal(0, snapshot.N300);
        Assert.Equal(0, snapshot.N100);
        Assert.Equal(0, snapshot.N50);
        Assert.Equal(0, snapshot.Misses);
        Assert.Equal(0, snapshot.UnstableRate);
        Assert.Empty(snapshot.TimingOffsets);
        Assert.Null(snapshot.BeatmapStats.Stars);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class CaptureSink : IAttemptSink
    {
        public AttemptCheckpoint? LastCheckpoint { get; private set; }
        public AttemptFinalization? LastFinalization { get; private set; }
        public AttemptStart? LastStart { get; private set; }

        public void StartAttempt(AttemptStart start) => LastStart = start;
        public void Checkpoint(AttemptCheckpoint checkpoint) => LastCheckpoint = checkpoint;
        public void DiscardIfEmpty(AttemptDiscard discard) { }
        public void Finalize(AttemptFinalization finalization) => LastFinalization = finalization;
    }
}
