using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Localisation;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Screens.Play.PlayerSettings;

namespace Kumori.ReplayViewer;

/// <summary>
/// osu!standard gameplay animation settings exposed in the replay side menu.
/// Preferences live in Kumori's viewer config and are mirrored into the
/// official ruleset config consumed by drawable sliders and hit circles.
/// </summary>
internal partial class KumoriGameplaySettings : PlayerSettingsGroup
{
    private readonly KumoriViewerConfig viewerConfig;
    private readonly Bindable<bool> snakingIn;
    private readonly Bindable<bool> snakingOut;
    private readonly Bindable<bool> hitAnimations;
    private readonly Bindable<bool> rulesetSnakingIn;
    private readonly Bindable<bool> rulesetSnakingOut;
    private readonly Bindable<bool> rulesetHitAnimations;
    private readonly Action<ValueChangedEvent<bool>> snakingInChanged;
    private readonly Action<ValueChangedEvent<bool>> snakingOutChanged;
    private readonly Action<ValueChangedEvent<bool>> hitAnimationsChanged;

    public KumoriGameplaySettings(
        KumoriViewerConfig viewerConfig,
        OsuRulesetConfigManager rulesetConfig)
        : base("Gameplay")
    {
        this.viewerConfig = viewerConfig;

        snakingIn = viewerConfig.GetBindable<bool>(KumoriViewerSetting.SnakingInSliders);
        snakingOut = viewerConfig.GetBindable<bool>(KumoriViewerSetting.SnakingOutSliders);
        hitAnimations = viewerConfig.GetBindable<bool>(KumoriViewerSetting.HitAnimations);

        rulesetSnakingIn = rulesetConfig.GetBindable<bool>(OsuRulesetSetting.SnakingInSliders);
        rulesetSnakingOut = rulesetConfig.GetBindable<bool>(OsuRulesetSetting.SnakingOutSliders);
        rulesetHitAnimations = rulesetConfig.GetBindable<bool>(OsuRulesetSetting.HitAnimations);

        rulesetSnakingIn.Value = snakingIn.Value;
        rulesetSnakingOut.Value = snakingOut.Value;
        rulesetHitAnimations.Value = hitAnimations.Value;

        snakingInChanged = value =>
        {
            rulesetSnakingIn.Value = value.NewValue;
            viewerConfig.Save();
        };
        snakingOutChanged = value =>
        {
            rulesetSnakingOut.Value = value.NewValue;
            viewerConfig.Save();
        };
        hitAnimationsChanged = value =>
        {
            rulesetHitAnimations.Value = value.NewValue;
            viewerConfig.Save();
        };

        snakingIn.ValueChanged += snakingInChanged;
        snakingOut.ValueChanged += snakingOutChanged;
        hitAnimations.ValueChanged += hitAnimationsChanged;

        Children = new Drawable[]
        {
            new PlayerCheckbox
            {
                LabelText = RulesetSettingsStrings.SnakingInSliders,
                Current = snakingIn,
            },
            new PlayerCheckbox
            {
                LabelText = RulesetSettingsStrings.SnakingOutSliders,
                Current = snakingOut,
            },
            new PlayerCheckbox
            {
                LabelText = RulesetSettingsStrings.HitAnimations,
                Current = hitAnimations,
            },
        };
    }

    protected override void Dispose(bool isDisposing)
    {
        snakingIn.ValueChanged -= snakingInChanged;
        snakingOut.ValueChanged -= snakingOutChanged;
        hitAnimations.ValueChanged -= hitAnimationsChanged;
        base.Dispose(isDisposing);
    }
}
