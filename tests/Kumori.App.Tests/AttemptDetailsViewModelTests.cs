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
    public void Timing_summary_exposes_bias_spread_and_split_for_the_chart_card()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                UnstableRate = 206.1,
                Timing = new TimingSummary
                {
                    HitCount = 204,
                    EarlyCount = 169,
                    LateCount = 35,
                    Mean = -13.5,
                    Deviation = 20.61,
                    Offsets = [-13.5],
                },
            },
        };

        Assert.Equal("204 hits", viewModel.TimingHitCountText);
        Assert.Equal("Mostly early \u00B7 UR 206.1", viewModel.TimingOverviewText);
        Assert.Equal("-13.5 ms", viewModel.TimingBiasText);
        Assert.Equal("EARLY", viewModel.TimingBiasDirectionText);
        Assert.Equal("20.6 ms", viewModel.TimingSpreadText);
        Assert.Equal("169", viewModel.TimingEarlyCountText);
        Assert.Equal("35", viewModel.TimingLateCountText);
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
    public void Difficulty_adjust_tooltip_lists_effective_settings_in_stat_order()
    {
        var tooltip = Assert.IsType<string>(ModEntryToToolTipConverter.Instance.Convert(
            new ModEntry(
                "DA",
                "{\"circle_size\":6,\"approach_rate\":10,\"drain_rate\":0,\"overall_difficulty\":10}"),
            typeof(string),
            null,
            System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(
            "Difficulty Adjust (DA)" + Environment.NewLine + "AR: 10  |  CS: 6  |  OD: 10  |  HP: 0",
            tooltip);
    }

    [Fact]
    public void Bpm_adjust_tooltip_displays_decimal_targetAndAudioMode()
    {
        var tooltip = Assert.IsType<string>(ModEntryToToolTipConverter.Instance.Convert(
            new ModEntry(
                "BPM",
                """{"target_bpm":174.5,"audio_mode":2,"scale_map_stats_with_bpm":false,"target_initialised":true}"""),
            typeof(string),
            null,
            System.Globalization.CultureInfo.InvariantCulture));

        Assert.Contains("BPM Adjust (BPM)", tooltip);
        Assert.Contains("Target BPM: 174.5 BPM", tooltip);
        Assert.Contains("Audio mode: Nightcore", tooltip);
        Assert.Contains("Scale map stats: Off", tooltip);
        Assert.DoesNotContain("Target initialised", tooltip);

        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                Mods = [new ModEntry("BPM", """{"target_bpm":174.5}""")],
            },
        };
        Assert.Equal("BPM 174.5 BPM", viewModel.ModsLine);
    }

    [Fact]
    public void Bpm_adjust_score_badge_uses_acronym_logo_and_serialized_target()
    {
        var wholeTarget = new ModEntry("BPM", """{"target_bpm":200}""");
        var decimalTarget = new ModEntry("bpm", """{"target_bpm":"174.5"}""");
        var missingTarget = new ModEntry("BPM", "{}");

        Assert.Null(ModBadgeInfo.IconFileName("BPM"));
        Assert.Equal("mod-double-time.png", ModBadgeInfo.IconFileName("DT"));
        Assert.Equal("200", ModEntryToBpmTargetConverter.TargetText(wholeTarget));
        Assert.Equal("174.5", ModEntryToBpmTargetConverter.TargetText(decimalTarget));
        Assert.Equal("", ModEntryToBpmTargetConverter.TargetText(missingTarget));

        Assert.Equal(62d, ModEntryToScoreBadgeWidthConverter.Instance.Convert(
            wholeTarget, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(72d, ModEntryToScoreBadgeWidthConverter.Instance.Convert(
            decimalTarget, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(34d, ModEntryToScoreBadgeWidthConverter.Instance.Convert(
            missingTarget, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Mod_display_order_matches_osu_web_without_mutating_storage_order()
    {
        ModEntry[] captured =
        [
            new("HR", "{}"), new("SO", "{}"), new("10K", "{}"), new("DA", "{}"),
            new("HD", "{}"), new("NF", "{}"), new("2K", "{}"), new("AC", "{}"),
            new("EZ", "{}"), new("RX", "{}"), new("AS", "{}"), new("TD", "{}"),
        ];
        var row = new AttemptRowViewModel(new AttemptSummary
        {
            ModsKey = "HRHD",
            Mods = captured,
        });
        var details = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails { Mods = captured },
        };

        string[] expected = ["EZ", "NF", "AC", "HD", "HR", "2K", "10K", "DA", "RX", "SO", "AS", "TD"];
        Assert.Equal(expected, row.ModEntries.Select(mod => mod.Acronym));
        Assert.Equal(expected, details.DisplayMods.Select(mod => mod.Acronym));
        Assert.Equal("HDHR", row.ModsText);
        Assert.Equal("HR", captured[0].Acronym);
    }

    [Fact]
    public void Mod_display_distinguishes_no_mods_and_falls_back_to_the_summary_key()
    {
        var details = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                Summary = new AttemptSummary { ModsKey = "NM" },
            },
        };

        Assert.False(details.HasDisplayMods);
        Assert.Empty(details.DisplayMods);

        details.Details = new AttemptDetails
        {
            Summary = new AttemptSummary { ModsKey = "HDDA" },
        };

        Assert.True(details.HasDisplayMods);
        Assert.Equal(["HD", "DA"], details.DisplayMods.Select(mod => mod.Acronym));
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
