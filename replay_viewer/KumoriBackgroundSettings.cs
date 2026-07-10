using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Configuration;
using osu.Game.Screens.Play.PlayerSettings;

namespace Kumori.ReplayViewer;

/// <summary>
/// Minimal visual settings restored for Kumori's replay viewer.
/// Keeps only the background controls users need, without the full lazer
/// visual/audio settings groups.
/// </summary>
internal partial class KumoriBackgroundSettings : PlayerSettingsGroup
{
    private readonly PlayerSliderBar<double> opacitySlider;
    private readonly PlayerSliderBar<double> blurSlider;
    private readonly KumoriViewerConfig viewerConfig;

    private bool syncingOpacity;

    public KumoriBackgroundSettings(KumoriViewerConfig viewerConfig)
        : base("Background")
    {
        this.viewerConfig = viewerConfig;

        Children = new Drawable[]
        {
            opacitySlider = new PlayerSliderBar<double>
            {
                LabelText = "Background opacity",
                DisplayAsPercentage = true,
            },
            blurSlider = new PlayerSliderBar<double>
            {
                LabelText = "Background blur",
                DisplayAsPercentage = true,
            },
        };
    }

    [BackgroundDependencyLoader]
    private void load(OsuConfigManager config)
    {
        var dimLevel = config.GetBindable<double>(OsuSetting.DimLevel);
        var backgroundOpacity = viewerConfig.GetBindable<double>(KumoriViewerSetting.BackgroundOpacity);

        dimLevel.Value = 1 - backgroundOpacity.Value;
        opacitySlider.Current = backgroundOpacity;

        dimLevel.ValueChanged += value =>
        {
            if (syncingOpacity)
                return;

            backgroundOpacity.Value = 1 - value.NewValue;
        };
        backgroundOpacity.ValueChanged += value =>
        {
            syncingOpacity = true;
            dimLevel.Value = 1 - value.NewValue;
            syncingOpacity = false;
        };

        blurSlider.Current = config.GetBindable<double>(OsuSetting.BlurLevel);
    }
}
