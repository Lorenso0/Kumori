using osu.Framework.Graphics;
using osu.Framework.Bindables;
using osu.Game.Overlays.Settings;
using osu.Game.Screens.Play.PlayerSettings;

namespace Kumori.ReplayViewer;

/// <summary>
/// "Kumori" group for the replay settings panel: per-kind judgement marker
/// visibility, persisted through <see cref="KumoriViewerConfig"/>.
/// </summary>
internal partial class KumoriSeekBarSettings : PlayerSettingsGroup
{
    private readonly Bindable<bool> hidden;
    private readonly Action<ValueChangedEvent<bool>> hiddenChangedHandler;

    public KumoriSeekBarSettings(
        KumoriViewerConfig config,
        Action? hiddenChanged = null,
        KumoriSeekBar? seekBar = null,
        Action? openMissAnalyzer = null,
        Action? openComparisonMenu = null)
        : base("Kumori")
    {
        hidden = config.GetBindable<bool>(KumoriViewerSetting.DisableHidden);
        hiddenChangedHandler = _ =>
        {
            config.Save();
            hiddenChanged?.Invoke();
        };
        hidden.ValueChanged += hiddenChangedHandler;

        Children = new Drawable[]
        {
            new PlayerCheckbox
            {
                LabelText = "Disable Hidden mod",
                Current = hidden,
            },
            new PlayerCheckbox
            {
                LabelText = "Show misses",
                Current = seekBar?.ShowMisses ?? config.GetBindable<bool>(KumoriViewerSetting.ShowMissMarkers),
            },
            new PlayerCheckbox
            {
                LabelText = "Show 50s",
                Current = seekBar?.ShowMehs ?? config.GetBindable<bool>(KumoriViewerSetting.ShowMehMarkers),
            },
            new PlayerCheckbox
            {
                LabelText = "Show 100s",
                Current = seekBar?.ShowOks ?? config.GetBindable<bool>(KumoriViewerSetting.ShowOkMarkers),
            },
            new PlayerCheckbox
            {
                LabelText = "Show slider breaks",
                Current = seekBar?.ShowSliderBreaks ?? config.GetBindable<bool>(KumoriViewerSetting.ShowSliderBreakMarkers),
            },
            new SettingsButton
            {
                Text = "Open advanced analyzer",
                Action = openMissAnalyzer,
            },
            new SettingsButton
            {
                Text = "Replay comparison",
                Action = openComparisonMenu,
            },
        };
    }

    protected override void Dispose(bool isDisposing)
    {
        hidden.ValueChanged -= hiddenChangedHandler;
        base.Dispose(isDisposing);
    }
}
