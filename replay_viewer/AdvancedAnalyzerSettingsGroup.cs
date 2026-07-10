using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays.Settings;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerSettingsGroup : PlayerSettingsGroup
{
    private readonly AdvancedAnalyzerViewModel viewModel;
    private readonly SpriteText title;
    private readonly SpriteText source;
    private readonly SpriteText diagnosis;
    private readonly SpriteText objectDetail;
    private readonly SpriteText timing;
    private readonly SpriteText aim;
    private readonly IconButton playButton;
    private readonly AdvancedAnalyzerAccuracyHeatmap heatmap;

    public AdvancedAnalyzerSettingsGroup(AdvancedAnalyzerViewModel viewModel, AdvancedAnalyzerOverlay overlay)
        : base("Advanced analyzer")
    {
        this.viewModel = viewModel;

        Drawable[] controls =
        [
            playbackRow(
                iconButton(FontAwesome.Solid.Backward, "Previous event", viewModel.SelectPrevious),
                iconButton(FontAwesome.Solid.StepBackward, "Previous frame", () => overlay.StepFrame(-1)),
                playButton = iconButton(FontAwesome.Regular.PlayCircle, "Play", overlay.TogglePlayback, 1.35f),
                iconButton(FontAwesome.Solid.StepForward, "Next frame", () => overlay.StepFrame(1)),
                iconButton(FontAwesome.Solid.Forward, "Next event", viewModel.SelectNext)),
            new PlayerCheckbox { LabelText = "Loop", Current = viewModel.LoopEnabled },
            new PlayerSliderBar<double>
            {
                LabelText = "Playback speed",
                Current = viewModel.PlaybackRate,
                KeyboardStep = 0.01f,
            },
            new PlayerSliderBar<double>
            {
                LabelText = "Start playback before (ms)",
                Current = viewModel.LoopBefore,
                KeyboardStep = 50,
                TransferValueOnCommit = true,
            },
            new PlayerSliderBar<double>
            {
                LabelText = "End playback after (ms)",
                Current = viewModel.LoopAfter,
                KeyboardStep = 50,
                TransferValueOnCommit = true,
            },
            divider(),
            title = heading(),
            source = text(11, true),
            diagnosis = text(16, true),
            divider(),
            objectDetail = text(14),
            timing = text(14),
            aim = text(14),
            heatmap = new AdvancedAnalyzerAccuracyHeatmap(viewModel),
            new PlayerCheckbox { LabelText = "Miss click marker", Current = viewModel.ShowInputMarkers },
            new PlayerCheckbox { LabelText = "Cursor movement samples", Current = viewModel.ShowMovementSamples },
            new PlayerCheckbox { LabelText = "Button-held samples", Current = viewModel.ShowHeldSamples },
            divider(),
            new PlayerCheckbox { LabelText = "Selected click marker", Current = viewModel.ShowSelectedClickMarker },
            divider(),
            fullButton("Close analyzer", overlay.Close),
        ];

        Children = controls;

        UpdateEntry(viewModel.SelectedEntry);
    }

    public void UpdateEntry(MissAnalysisEntry? entry)
    {
        if (entry == null)
        {
            title.Text = "No review events";
            source.Text = viewModel.TotalCount == 0 ? "No bad hits were found." : "No events match the filters.";
            diagnosis.Text = objectDetail.Text = timing.Text = aim.Text = string.Empty;
            heatmap.SetEntry(null);
            return;
        }

        title.Text = $"{entry.Label} at {AdvancedAnalyzerMetrics.FormatTime(entry.EventTime)}";
        source.Text = entry.Source == AnalysisDataSource.Lazer ? "EXACT - LAZER JUDGEMENT" : "CAPTURED RESULT + REPLAY DATA";
        source.Colour = entry.Source == AnalysisDataSource.Lazer ? Color4.Cyan : Color4.Orange;
        diagnosis.Text = AdvancedAnalyzerMetrics.Diagnosis(entry);
        objectDetail.Text = $"Object: {entry.ObjectType}";
        timing.Text = $"Input: {AdvancedAnalyzerMetrics.FormatInputTiming(entry)}";
        aim.Text = $"Cursor distance: {AdvancedAnalyzerMetrics.FormatDistance(entry.DistanceFromTarget)}  |  radius {entry.TargetRadius:0}px";
        heatmap.SetEntry(entry);
    }

    public void SetPlaying(bool playing)
    {
        playButton.Icon = playing ? FontAwesome.Regular.PauseCircle : FontAwesome.Regular.PlayCircle;
        playButton.TooltipText = playing ? "Pause" : "Play";
    }

    private static SpriteText heading() => new()
    {
        Font = FontUsage.Default.With(size: 20, weight: "bold"),
        Colour = Color4.White,
    };

    private static SpriteText text(float size, bool bold = false) => new()
    {
        Font = FontUsage.Default.With(size: size, weight: bold ? "bold" : null),
        Colour = Color4.White.Opacity(0.78f),
        RelativeSizeAxes = Axes.X,
    };

    private static Box divider() => new()
    {
        RelativeSizeAxes = Axes.X,
        Height = 1,
        Colour = Color4.White.Opacity(0.12f),
    };

    private static SettingsButton fullButton(string label, Action action) => new()
    {
        Text = label,
        Action = action,
        RelativeSizeAxes = Axes.X,
        Height = 36,
    };

    private static IconButton iconButton(IconUsage icon, string tooltip, Action action, float scale = 1) => new()
    {
        Icon = icon,
        TooltipText = tooltip,
        Action = action,
        Scale = new Vector2(scale),
        IconScale = new Vector2(scale),
    };

    private static FillFlowContainer playbackRow(params Drawable[] children) => new()
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Horizontal,
        Spacing = new Vector2(8, 0),
        Children = children,
    };
}
