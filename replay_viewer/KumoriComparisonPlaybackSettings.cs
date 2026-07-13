using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;

namespace Kumori.ReplayViewer;

/// <summary>Native-style playback controls which target the current comparison player.</summary>
internal partial class KumoriComparisonPlaybackSettings : PlayerSettingsGroup
{
    private readonly Func<KumoriReplayPlayer?> player;
    private readonly BindableDouble playbackRate = new(1)
    {
        MinValue = 0.05,
        MaxValue = 2,
        Precision = 0.01,
    };
    private readonly IconButton playButton;
    private bool syncingRate;

    public KumoriComparisonPlaybackSettings(Func<KumoriReplayPlayer?> player)
        : base("Playback")
    {
        this.player = player;
        playbackRate.ValueChanged += rateChanged;
        Children =
        [
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(7, 0),
                Children =
                [
                    button(FontAwesome.Solid.FastBackward, "Back 10 seconds", () => player()?.SeekInDirection(-10)),
                    button(FontAwesome.Solid.Backward, "Back 1 second", () => player()?.SeekInDirection(-1)),
                    button(FontAwesome.Solid.StepBackward, "Previous frame", () => player()?.StepFrame(-1)),
                    playButton = button(FontAwesome.Regular.PlayCircle, "Play", togglePlayback, 1.25f),
                    button(FontAwesome.Solid.StepForward, "Next frame", () => player()?.StepFrame(1)),
                    button(FontAwesome.Solid.Forward, "Forward 1 second", () => player()?.SeekInDirection(1)),
                    button(FontAwesome.Solid.FastForward, "Forward 10 seconds", () => player()?.SeekInDirection(10)),
                ],
            },
            new PlayerSliderBar<double>
            {
                LabelText = "Playback speed",
                Current = playbackRate,
                KeyboardStep = 0.01f,
            },
        ];
    }

    protected override void Update()
    {
        base.Update();
        KumoriReplayPlayer? current = player();
        if (current == null)
            return;

        if (!syncingRate && Math.Abs(playbackRate.Value - current.PlaybackRate) > 0.0001)
        {
            syncingRate = true;
            playbackRate.Value = current.PlaybackRate;
            syncingRate = false;
        }

        bool paused = current.IsGameplayPaused;
        playButton.Icon = paused ? FontAwesome.Regular.PlayCircle : FontAwesome.Regular.PauseCircle;
        playButton.TooltipText = paused ? "Play" : "Pause";
    }

    private void togglePlayback()
    {
        if (player() is not { } current)
            return;
        if (current.IsGameplayPaused)
            current.StartReplayPlayback();
        else
            current.PauseGameplay();
    }

    private void rateChanged(ValueChangedEvent<double> change)
    {
        if (!syncingRate)
            player()?.SetPlaybackRate(change.NewValue);
    }

    private static IconButton button(IconUsage icon, string tooltip, Action action, float scale = 1) => new()
    {
        Icon = icon,
        TooltipText = tooltip,
        Action = action,
        Scale = new Vector2(scale),
        IconScale = new Vector2(scale),
    };

    protected override void Dispose(bool isDisposing)
    {
        playbackRate.ValueChanged -= rateChanged;
        base.Dispose(isDisposing);
    }
}
