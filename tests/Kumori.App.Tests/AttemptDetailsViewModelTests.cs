using Kumori.App.ViewModels;
using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.App.Tests;

public sealed class AttemptDetailsViewModelTests
{
    [Fact]
    public void AccuracyValue_truncates_to_match_the_in_game_display()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                Summary = new AttemptSummary { Accuracy = 90.08754793430288 },
            },
        };

        Assert.Equal("90.08%", viewModel.AccuracyValue);
    }

    [Fact]
    public void Stable_slider_overview_remains_visible_but_is_marked_unsupported()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                ClientKind = "stable",
                Mods = [new ModEntry("HD", "{}"), new ModEntry("CL", "{}")],
            },
        };

        Assert.True(viewModel.HasRichSliderData);
        Assert.True(viewModel.IsStablePlay);
        Assert.Equal("—", viewModel.LargeTickText);
        Assert.Equal("—", viewModel.SliderTailText);
        Assert.Equal("—", viewModel.SliderBreakText);
        Assert.Equal(0.38, viewModel.SliderStatsOpacity);
        Assert.Contains("not available", viewModel.SliderStatsToolTip);

        viewModel.Details = new AttemptDetails { ClientKind = "lazer", Mods = [new ModEntry("HD", "{}")] };
        Assert.True(viewModel.HasRichSliderData);
        Assert.False(viewModel.IsStablePlay);
        Assert.Equal(1, viewModel.SliderStatsOpacity);
    }

    [Fact]
    public void Replay_recovery_notice_explains_missing_tosu_data_and_simulation()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                ResultRecoveredFromReplay = true,
                ResultRecoverySimulationCompleted = true,
            },
        };

        Assert.True(viewModel.HasReplayRecoveredResult);
        Assert.Contains("tosu gameplay data was unavailable", viewModel.ReplayRecoveryNotice);
        Assert.Contains("re-simulated", viewModel.ReplayRecoveryNotice);
    }

    [Fact]
    public async Task Movement_refresh_reloads_metadata_cached_before_deferred_persistence()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"kumori-movement-refresh-{Guid.NewGuid():N}.sqlite3");

        try
        {
            var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
            var sink = new AttemptSqliteSink(
                factory,
                (_, work) => work(CancellationToken.None));
            sink.StartAttempt(new AttemptStart
            {
                Identity = "movement-refresh",
                WallTime = 1_788_000_000,
                Artist = "Artist",
                Title = "Song",
                Difficulty = "Extra",
                ClientKind = OsuClientKind.Lazer,
            });
            var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
            sink.Finalize(new AttemptFinalization(
                "completed",
                "test_boundary",
                new AttemptSnapshot
                {
                    Identity = "movement-refresh",
                    WallTime = 1_788_000_003,
                    DurationSeconds = 3,
                    Progress = 1,
                },
                Ordinal: 1));

            var viewModel = new AttemptDetailsViewModel(new AttemptDetailsRepository(factory));
            await viewModel.LoadAsync(attemptId);

            Assert.NotNull(viewModel.Details);
            Assert.Null(viewModel.Details.Movement);
            Assert.Equal("none", viewModel.TechnicalRecordingSource);

            var capture = new MovementCaptureStore(factory);
            capture.Start(attemptId);
            capture.AddSamples([
                new MovementSample { MapTimeMs = 0, MonotonicMs = 0 },
                new MovementSample { MapTimeMs = 10, MonotonicMs = 10 },
                new MovementSample { MapTimeMs = 20, MonotonicMs = 20 },
                new MovementSample { MapTimeMs = 30, MonotonicMs = 30 },
                new MovementSample { MapTimeMs = 40, MonotonicMs = 40 },
            ]);
            capture.Complete(7, "lazer_memory", "{}");

            // A normal selection reload still returns the pre-persistence cache entry.
            await viewModel.LoadAsync(attemptId);
            Assert.Null(viewModel.Details.Movement);

            await viewModel.RefreshAfterMovementReplacementAsync(attemptId);

            var movement = Assert.IsType<MovementSummary>(viewModel.Details?.Movement);
            Assert.True(movement.Available);
            Assert.Equal("lazer_memory", movement.Source);
            Assert.Equal(5, movement.SampleCount);
            Assert.Equal(125, movement.SampleRate);
            Assert.Equal(7, movement.DroppedSamples);
            Assert.Equal("Lazer Memory", viewModel.TechnicalRecordingSource);
            Assert.Equal("5", viewModel.TechnicalRecordingSamples);
            Assert.Equal("125 Hz", viewModel.TechnicalSampleRate);
            Assert.Equal("7", viewModel.TechnicalDroppedSamples);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
