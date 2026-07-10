using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays.Settings;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerSettingsGroup : PlayerSettingsGroup
{
    private readonly AdvancedAnalyzerViewModel viewModel;
    private readonly SingleLineText title;
    private readonly SingleLineText source;
    private readonly SingleLineText summary;
    private readonly SingleLineText timingBoundary;
    private readonly SingleLineText objectDetail;
    private readonly IconButton playButton;
    private readonly AdvancedAnalyzerAccuracyHeatmap heatmap;
    private readonly AdvancedAnalyzerTimingBar timingBar;

    public AdvancedAnalyzerSettingsGroup(AdvancedAnalyzerViewModel viewModel, AdvancedAnalyzerOverlay overlay)
        : base("Advanced analyzer")
    {
        this.viewModel = viewModel;

        Children =
        [
            section("PLAYBACK"),
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

            section("EVENT ANALYSIS"),
            title = singleLine(20, true),
            source = singleLine(10, true),
            summary = singleLine(10, true),
            timingBoundary = singleLine(10),
            objectDetail = singleLine(12),
            timingBar = new AdvancedAnalyzerTimingBar(),
            divider(),

            section("CURSOR PATH"),
            heatmap = new AdvancedAnalyzerAccuracyHeatmap(viewModel),
            new PlayerCheckbox { LabelText = "Miss click marker", Current = viewModel.ShowInputMarkers },
            new PlayerCheckbox { LabelText = "Cursor movement samples", Current = viewModel.ShowMovementSamples },
            new PlayerCheckbox { LabelText = "Button-held samples", Current = viewModel.ShowHeldSamples },
            divider(),

            section("SELECTED NOTE"),
            new SettingsColour
            {
                LabelText = "Selected note color",
                Current = viewModel.SelectedNoteColour,
            },
            new PlayerCheckbox { LabelText = "Recolor selected note", Current = viewModel.RecolourSelectedNote },
            new PlayerCheckbox { LabelText = "Show note indicator", Current = viewModel.ShowSelectedNoteIndicator },
            new PlayerCheckbox { LabelText = "Show selected click marker", Current = viewModel.ShowSelectedClickMarker },
            divider(),

            section("SHORTCUTS"),
            labelText("Left/Right event  ·  A/D frame  ·  Space play", 9),
            fullButton("Close analyzer", overlay.Close),
        ];

        UpdateEntry(viewModel.SelectedEntry);
    }

    public void UpdateEntry(MissAnalysisEntry? entry)
    {
        if (entry == null)
        {
            title.Text = "No review events";
            source.Text = viewModel.TotalCount == 0 ? "No bad hits were found." : "No events match the filters.";
            summary.Text = timingBoundary.Text = objectDetail.Text = string.Empty;
            heatmap.SetEntry(null);
            timingBar.SetEntry(null);
            return;
        }

        title.Text = $"{entry.Label} at {AdvancedAnalyzerMetrics.FormatTime(entry.EventTime)}";
        source.Text = AdvancedAnalyzerMetrics.EvidenceLabel(entry);
        source.Colour = AdvancedAnalyzerMetrics.Confidence(entry) == AnalyzerEvidenceConfidence.High ? Color4.Cyan : Color4.Orange;
        summary.Text = AdvancedAnalyzerMetrics.EventSummary(entry);
        timingBoundary.Text = AdvancedAnalyzerMetrics.TimingBoundaryExplanation(entry);
        objectDetail.Text = $"Object: {entry.ObjectType}";
        timingBar.SetEntry(entry);
        heatmap.SetEntry(entry);
    }

    public void SetPlaying(bool playing)
    {
        playButton.Icon = playing ? FontAwesome.Regular.PauseCircle : FontAwesome.Regular.PlayCircle;
        playButton.TooltipText = playing ? "Pause" : "Play";
    }

    private static SingleLineText singleLine(float size, bool bold = false) => new()
    {
        Font = FontUsage.Default.With(size: size, weight: bold ? "bold" : null),
        Colour = Color4.White.Opacity(0.78f),
    };

    private static SpriteText section(string value) => new()
    {
        Text = value,
        Font = FontUsage.Default.With(size: 10, weight: "bold"),
        Colour = Color4.Cyan.Opacity(0.72f),
        RelativeSizeAxes = Axes.X,
    };

    private static SpriteText labelText(string value, float size) => new()
    {
        Text = value,
        Font = FontUsage.Default.With(size: size),
        Colour = Color4.White.Opacity(0.62f),
        RelativeSizeAxes = Axes.X,
        Truncate = true,
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

    private partial class SingleLineText : SpriteText, IHasTooltip
    {
        public LocalisableString TooltipText => Text;

        public SingleLineText()
        {
            RelativeSizeAxes = Axes.X;
            Truncate = true;
        }
    }
}
