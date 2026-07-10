using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Screens.Play.PlayerSettings;

namespace Kumori.ReplayViewer;

internal partial class KumoriAudioSettings : PlayerSettingsGroup
{
    private readonly PlayerSliderBar<double> master;
    private readonly PlayerSliderBar<double> music;
    private readonly PlayerSliderBar<double> hitsounds;
    private readonly KumoriViewerConfig config;
    private readonly List<Action> unbindActions = [];

    public KumoriAudioSettings(KumoriViewerConfig config)
        : base("Audio")
    {
        this.config = config;
        Children = new Drawable[]
        {
            master = slider("Master volume"),
            music = slider("Music volume"),
            hitsounds = slider("Hitsound volume"),
        };
    }

    [BackgroundDependencyLoader]
    private void load(AudioManager audio)
    {
        var seeded = config.GetBindable<bool>(KumoriViewerSetting.AudioSettingsSeeded);
        if (!seeded.Value)
        {
            config.SetValue(KumoriViewerSetting.MasterVolume, audio.Volume.Value);
            config.SetValue(KumoriViewerSetting.MusicVolume, audio.VolumeTrack.Value);
            config.SetValue(KumoriViewerSetting.HitsoundVolume, audio.VolumeSample.Value);
            seeded.Value = true;
            config.Save();
        }

        bind(master, config.GetBindable<double>(KumoriViewerSetting.MasterVolume), audio.Volume);
        bind(music, config.GetBindable<double>(KumoriViewerSetting.MusicVolume), audio.VolumeTrack);
        bind(hitsounds, config.GetBindable<double>(KumoriViewerSetting.HitsoundVolume), audio.VolumeSample);
    }

    private void bind(PlayerSliderBar<double> slider, Bindable<double> persisted, Bindable<double> audio)
    {
        bool syncing = false;
        Action<ValueChangedEvent<double>> persistedChanged = change =>
        {
            if (syncing)
                return;
            syncing = true;
            audio.Value = change.NewValue;
            syncing = false;
            config.Save();
        };
        Action<ValueChangedEvent<double>> audioChanged = change =>
        {
            if (syncing)
                return;
            syncing = true;
            persisted.Value = change.NewValue;
            syncing = false;
            config.Save();
        };

        persisted.ValueChanged += persistedChanged;
        audio.ValueChanged += audioChanged;
        unbindActions.Add(() =>
        {
            persisted.ValueChanged -= persistedChanged;
            audio.ValueChanged -= audioChanged;
        });

        audio.Value = persisted.Value;
        slider.Current = persisted;
    }

    protected override void Dispose(bool isDisposing)
    {
        foreach (Action unbind in unbindActions)
            unbind();
        unbindActions.Clear();
        base.Dispose(isDisposing);
    }

    private static PlayerSliderBar<double> slider(string label) => new()
    {
        LabelText = label,
        DisplayAsPercentage = true,
        KeyboardStep = 0.01f,
    };
}
