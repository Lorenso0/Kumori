using osu.Framework.Graphics;
using osu.Game.Overlays.Settings;
using osu.Game.Screens.Play.PlayerSettings;

namespace Kumori.ReplayViewer;

/// <summary>
/// "Kumori" group for the replay settings panel: per-kind judgement marker
/// visibility, persisted through <see cref="KumoriViewerConfig"/>.
/// </summary>
internal partial class KumoriSeekBarSettings : PlayerSettingsGroup
{
    public KumoriSeekBarSettings(
        KumoriViewerConfig config,
        Action? hiddenChanged = null,
        KumoriSeekBar? seekBar = null,
        Action? openMissAnalyzer = null,
        Action? openComparisonMenu = null)
        : base("Kumori")
    {
        var hidden = config.GetBindable<bool>(KumoriViewerSetting.DisableHidden);
        hidden.ValueChanged += _ =>
        {
            config.Save();
            hiddenChanged?.Invoke();
        };

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
}
