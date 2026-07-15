using Kumori.Core.Settings;
using Kumori.Tracking;
using Serilog;

namespace Kumori.App;

/// <summary>
/// One-shot developer diagnostic that feeds a finalized play through the real
/// missing-tosu replay recovery path. Only result telemetry is removed; map
/// identity, mods, timing and attempt lifecycle remain valid so the persisted
/// replay can be located and simulated normally.
/// </summary>
internal sealed class ReplayRecoveryTestAttemptSink(
    IAttemptSink inner,
    SettingsService settings,
    Action<Action> deferSettingsWrite) : IAttemptSink
{
    private bool forceCurrentAttempt;
    private bool consumedSwitchPendingPersistence;

    public void StartAttempt(AttemptStart start)
    {
        forceCurrentAttempt = !Volatile.Read(ref consumedSwitchPendingPersistence)
            && settings.Current.Developer.ForceReplayRecoveryNextPlay;
        if (forceCurrentAttempt)
            Log.Warning("Developer replay recovery test armed for the current attempt");
        inner.StartAttempt(forceCurrentAttempt
            ? start with { BeatmapStats = new BeatmapStats() }
            : start);
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
        // Gameplay checkpoints are the fallback authority when the final tosu
        // result is missing. The diagnostic removes only the final result.
        => inner.Checkpoint(checkpoint);

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        // An empty/discarded attempt does not consume the one-shot switch.
        inner.DiscardIfEmpty(discard);
        forceCurrentAttempt = false;
    }

    public void Finalize(AttemptFinalization finalization)
    {
        bool consume = forceCurrentAttempt
                       && !finalization.Outcome.Equals("active", StringComparison.OrdinalIgnoreCase);
        forceCurrentAttempt = false;
        if (!consume)
        {
            inner.Finalize(finalization);
            return;
        }

        Log.Warning(
            "Developer replay recovery test is discarding tosu result telemetry for the finalized play");
        try
        {
            inner.Finalize(finalization with
            {
                Snapshot = WithoutResultTelemetry(finalization.Snapshot),
            });
        }
        finally
        {
            // The one-shot is consumed in memory immediately so another attempt
            // cannot re-arm it while the settings write waits for gameplay to end.
            Volatile.Write(ref consumedSwitchPendingPersistence, true);
            void PersistConsumedSwitch()
            {
                try
                {
                    settings.Update(value => value.Developer.ForceReplayRecoveryNextPlay = false);
                    Volatile.Write(ref consumedSwitchPendingPersistence, false);
                    Log.Information("Developer replay recovery test switch consumed and automatically disabled");
                }
                catch (Exception ex)
                {
                    // Never fail attempt finalization after the test result was
                    // already stored. A settings write failure is logged clearly.
                    Log.Error(ex, "Could not persist the disabled developer replay recovery switch");
                }
            }

            try
            {
                deferSettingsWrite(PersistConsumedSwitch);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not schedule persistence of the disabled developer replay recovery switch");
                _ = Task.Run(PersistConsumedSwitch);
            }
        }
    }

    internal static AttemptSnapshot WithoutResultTelemetry(AttemptSnapshot snapshot) => snapshot with
    {
        Score = 0,
        Accuracy = 0,
        Grade = null,
        Pp = 0,
        FcPp = 0,
        MaxPp = 0,
        Combo = 0,
        N300 = 0,
        N100 = 0,
        N50 = 0,
        Misses = 0,
        Geki = 0,
        Katu = 0,
        SliderBreaks = 0,
        LargeTickHits = 0,
        LargeTickMisses = 0,
        SmallTickHits = 0,
        SmallTickMisses = 0,
        SliderTailHits = 0,
        SliderTailMisses = 0,
        UnstableRate = 0,
        BeatmapStats = new BeatmapStats(),
    };
}
