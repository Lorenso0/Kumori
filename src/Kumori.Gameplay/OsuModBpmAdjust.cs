using System.Collections.Generic;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.UI;
using osu.Framework.Utils;

namespace Kumori.Gameplay;

/// <summary>
/// Runtime representation of the local fork's osu!standard BPM Adjust mod.
/// It intentionally mirrors the fork's rate, audio, and unscaled-stat behavior
/// without requiring the fork's assemblies at runtime.
/// </summary>
public sealed class OsuModBpmAdjust : OsuModDoubleTime, IApplicableToDifficulty, IApplicableToDrawableRuleset<OsuHitObject>
{
    private const double nightcore_pitch_adjust = 1.5;
    private const double minimum_supported_tempo = 0.05;

    private readonly BindableNumber<double> tempoAdjust = new BindableDouble(1);
    private readonly BindableNumber<double> frequencyAdjust = new BindableDouble(1);

    public override string Name => "BPM Adjust";
    public override string Acronym => "BPM";
    public override IconUsage? Icon => null;
    public override ModType Type => ModType.Fun;
    public override LocalisableString Description => "Play every map at your chosen BPM.";
    public override bool Ranked => false;

    public double? TargetBpm { get; }
    public double SourceBpm { get; }
    public BpmAdjustAudioMode AudioMode { get; }
    public bool ScaleMapStatsWithBpm { get; }

    /// <summary>
    /// Required by lazer's mod cloning contract. Runtime instances are normally
    /// created with the beatmap-aware constructor below.
    /// </summary>
    public OsuModBpmAdjust()
        : this(null, 0, BpmAdjustAudioMode.PreservePitch, true, 1)
    {
    }

    public OsuModBpmAdjust(IBeatmap beatmap, BpmAdjustSettings settings)
        : this(
            settings.TargetBpm,
            BpmAdjustBeatmap.SourceBpm(beatmap),
            settings.AudioMode,
            settings.ScaleMapStatsWithBpm,
            settings.ClockRate(BpmAdjustBeatmap.SourceBpm(beatmap)))
    {
    }

    private OsuModBpmAdjust(
        double? targetBpm,
        double sourceBpm,
        BpmAdjustAudioMode audioMode,
        bool scaleMapStatsWithBpm,
        double speedChange)
    {
        SpeedChange.MinValue = double.Epsilon;
        SpeedChange.MaxValue = double.MaxValue;
        SpeedChange.Precision = double.Epsilon;
        TargetBpm = targetBpm;
        SourceBpm = sourceBpm;
        AudioMode = audioMode;
        ScaleMapStatsWithBpm = scaleMapStatsWithBpm;
        SpeedChange.Value = speedChange;
        updateAudioAdjustments();
    }

    public override Mod DeepClone() =>
        new OsuModBpmAdjust(TargetBpm, SourceBpm, AudioMode, ScaleMapStatsWithBpm, SpeedChange.Value);

    public void ApplyToDifficulty(BeatmapDifficulty difficulty)
    {
        if (ScaleMapStatsWithBpm)
            return;

        double preempt = IBeatmapDifficultyInfo.DifficultyRange(
            difficulty.ApproachRate,
            OsuHitObject.PREEMPT_RANGE) * SpeedChange.Value;
        difficulty.ApproachRate = (float)IBeatmapDifficultyInfo.InverseDifficultyRange(
            preempt,
            OsuHitObject.PREEMPT_RANGE);

        double greatWindow = IBeatmapDifficultyInfo.DifficultyRange(
            difficulty.OverallDifficulty,
            OsuHitWindows.GREAT_WINDOW_RANGE) * SpeedChange.Value;
        difficulty.OverallDifficulty = (float)IBeatmapDifficultyInfo.InverseDifficultyRange(
            greatWindow,
            OsuHitWindows.GREAT_WINDOW_RANGE);
    }

    public override void ApplyToTrack(IAdjustableAudioComponent track)
    {
        track.AddAdjustment(AdjustableProperty.Frequency, frequencyAdjust);
        track.AddAdjustment(AdjustableProperty.Tempo, tempoAdjust);
    }

    public void ApplyToDrawableRuleset(DrawableRuleset<OsuHitObject> drawableRuleset)
    {
        if (AudioMode != BpmAdjustAudioMode.Nightcore)
            return;

        bool playHats = Precision.AlmostEquals(drawableRuleset.Beatmap.Difficulty.SliderTickRate % 2, 0);
        drawableRuleset.Overlays.Add(new ModNightcore<OsuHitObject>.NightcoreBeatContainer(playHats));
    }

    public override IEnumerable<(LocalisableString setting, LocalisableString value)> SettingDescription
    {
        get
        {
            if (TargetBpm != null)
                yield return ("Target BPM", FormattableString.Invariant($"{TargetBpm.Value:0.##} BPM"));

            yield return ("Speed change", FormattableString.Invariant($"{SpeedChange.Value:0.####}x"));

            if (AudioMode != BpmAdjustAudioMode.PreservePitch)
                yield return ("Audio mode", AudioMode.ToString());

            if (!ScaleMapStatsWithBpm)
                yield return ("Map stats", "Unscaled");
        }
    }

    public override string ExtendedIconInformation =>
        TargetBpm == null ? string.Empty : FormattableString.Invariant($"{TargetBpm.Value:0.##}");

    private void updateAudioAdjustments()
    {
        double frequency;
        double tempo;

        switch (AudioMode)
        {
            case BpmAdjustAudioMode.PreservePitch:
                frequency = 1;
                tempo = SpeedChange.Value;
                break;

            case BpmAdjustAudioMode.AdjustPitch:
                frequency = SpeedChange.Value;
                tempo = 1;
                break;

            case BpmAdjustAudioMode.Nightcore:
                frequency = nightcore_pitch_adjust;
                tempo = SpeedChange.Value / nightcore_pitch_adjust;
                break;

            default:
                frequency = 1;
                tempo = SpeedChange.Value;
                break;
        }

        if (tempo < minimum_supported_tempo)
        {
            frequency *= tempo / minimum_supported_tempo;
            tempo = minimum_supported_tempo;
        }

        frequencyAdjust.Value = frequency;
        tempoAdjust.Value = tempo;
    }
}
