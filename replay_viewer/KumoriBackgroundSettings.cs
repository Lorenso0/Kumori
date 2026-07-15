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
    private Bindable<double>? dimLevel;
    private Bindable<double>? backgroundOpacity;
    private Action<ValueChangedEvent<double>>? dimLevelChanged;
    private Action<ValueChangedEvent<double>>? backgroundOpacityChanged;

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
        dimLevel = config.GetBindable<double>(OsuSetting.DimLevel);
        backgroundOpacity = viewerConfig.GetBindable<double>(KumoriViewerSetting.BackgroundOpacity);

        dimLevel.Value = 1 - backgroundOpacity.Value;
        opacitySlider.Current = backgroundOpacity;

        dimLevelChanged = value =>
        {
            if (syncingOpacity)
                return;

            backgroundOpacity.Value = 1 - value.NewValue;
        };
        backgroundOpacityChanged = value =>
        {
            syncingOpacity = true;
            dimLevel.Value = 1 - value.NewValue;
            syncingOpacity = false;
        };
        dimLevel.ValueChanged += dimLevelChanged;
        backgroundOpacity.ValueChanged += backgroundOpacityChanged;

        blurSlider.Current = config.GetBindable<double>(OsuSetting.BlurLevel);
    }

    protected override void Dispose(bool isDisposing)
    {
        if (dimLevel != null && dimLevelChanged != null)
            dimLevel.ValueChanged -= dimLevelChanged;
        if (backgroundOpacity != null && backgroundOpacityChanged != null)
            backgroundOpacity.ValueChanged -= backgroundOpacityChanged;
        base.Dispose(isDisposing);
    }
}
