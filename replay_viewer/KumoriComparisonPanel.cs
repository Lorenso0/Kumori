using Kumori.Core;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Overlays.Settings;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;

namespace Kumori.ReplayViewer;

internal partial class KumoriComparisonPanel : PlayerSettingsGroup
{
    internal const float NativePanelWidth = 270;
    private readonly KumoriViewerConfig config;
    private readonly Bindable<Colour4> comparisonColour;
    private readonly Bindable<Colour4> comparisonTrailColour;

    public KumoriComparisonPanel(
        KumoriViewerConfig config,
        IReadOnlyList<ComparisonContract> options,
        long? selectedAttemptId,
        Action<ComparisonContract> select,
        Action chooseOsr,
        IBindable<string> importStatus,
        Action stop,
        Action close)
        : base("Replay comparison")
    {
        this.config = config;
        // ReplaySettingsOverlay auto-sizes itself from its groups. A relative
        // width here creates a circular dependency and collapses the panel.
        Width = NativePanelWidth;
        comparisonColour = config.GetBindable<Colour4>(KumoriViewerSetting.ComparisonReplayCursorColour);
        comparisonTrailColour = config.GetBindable<Colour4>(KumoriViewerSetting.ComparisonReplayCursorTrailColour);
        comparisonColour.ValueChanged += colourChanged;
        comparisonTrailColour.ValueChanged += colourChanged;

        var list = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
        };
        foreach (var option in options)
            list.Add(new ReplayCard(option, option.AttemptId == selectedAttemptId, comparisonColour, () => select(option)));

        var children = new List<Drawable>
        {
            label("Choose a replay to compare.", 9.5f, true),
            label("Same map - matching gameplay mods", 8),
            compactColour("Comparison cursor", comparisonColour),
            compactColour("Comparison trail", comparisonTrailColour),
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = Colour4.White.Opacity(0.12f),
            },
            new CompactActionButton("Compare a .osr file", chooseOsr, Colour4.FromHex("#386FA4")),
            label("Temporary - never added to history", 7.5f),
            new ImportStatusText(importStatus),
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = Colour4.White.Opacity(0.12f),
            },
        };

        if (options.Count == 0)
        {
            children.Add(label("No matching replay with cursor data.", 9));
        }
        else
        {
            children.Add(sectionLabel(options.Count == 1 ? "1 ELIGIBLE REPLAY" : $"{options.Count} ELIGIBLE REPLAYS"));
            children.Add(new OsuScrollContainer(Direction.Vertical)
            {
                RelativeSizeAxes = Axes.X,
                Height = Math.Min(options.Count * 59, 172),
                Child = list,
            });
        }

        if (selectedAttemptId is not null)
            children.Add(new CompactActionButton("Stop comparison", stop, Colour4.FromHex("#b72f69")));

        children.Add(new CompactActionButton("Close", close, Colour4.FromHex("#5b35d5")));

        Children = children;
    }

    private void colourChanged(ValueChangedEvent<Colour4> _) => config.Save();

    protected override void Dispose(bool isDisposing)
    {
        comparisonColour.ValueChanged -= colourChanged;
        comparisonTrailColour.ValueChanged -= colourChanged;
        base.Dispose(isDisposing);
    }

    private partial class ReplayCard : CompositeDrawable
    {
        private readonly ComparisonContract attempt;
        private readonly bool selected;
        private readonly Bindable<Colour4> colour;
        private readonly Action action;
        private Box background = null!;
        private Box accent = null!;
        private SpriteText modText = null!;
        private SpriteText? activeText;

        public ReplayCard(ComparisonContract attempt, bool selected, Bindable<Colour4> colour, Action action)
        {
            RelativeSizeAxes = Axes.X;
            this.attempt = attempt;
            this.selected = selected;
            this.colour = colour;
            this.action = action;
            Height = 54;
            Masking = true;
            CornerRadius = 6;
            BorderThickness = selected ? 2 : 1;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            string date = DisplayDateTime.FormatLocalDateTime(attempt.StartedAt);
            string heading = attempt.Ephemeral && !string.IsNullOrWhiteSpace(attempt.SourceName)
                ? attempt.SourceName
                : date;

            var topLine = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 13,
                Children =
                [
                    new SpriteText
                    {
                        Text = heading,
                        RelativeSizeAxes = Axes.X,
                        Width = selected ? 0.68f : 1,
                        Truncate = true,
                        Font = FontUsage.Default.With(size: 9.5f, weight: "bold"),
                        Colour = Colour4.White.Opacity(0.96f),
                    },
                ],
            };

            if (selected)
            {
                activeText = new SpriteText
                {
                    Text = "COMPARING",
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Font = FontUsage.Default.With(size: 7.5f, weight: "bold"),
                };
                topLine.Add(activeText);
            }

            InternalChildren =
            [
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.White.Opacity(selected ? 0.14f : 0.055f),
                },
                accent = new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = colour.Value,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = 10, Right = 8, Top = 5, Bottom = 4 },
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 2),
                    Children =
                    [
                        topLine,
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(9, 0),
                            Children =
                            [
                                modText = stat(attempt.ModsKey, Colour4.White, true),
                                stat($"{attempt.Accuracy:0.00}%", Colour4.White.Opacity(0.9f)),
                                stat($"{attempt.Combo}x combo", Colour4.White.Opacity(0.62f)),
                            ],
                        },
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(9, 0),
                            Children =
                            [
                                stat($"{attempt.Score:N0} score", Colour4.White.Opacity(0.58f)),
                                stat($"100  {attempt.N100}", Colour4.FromHex("#73D89A")),
                                stat($"50  {attempt.N50}", Colour4.FromHex("#F0CA63")),
                                stat($"Miss  {attempt.Misses}", Colour4.FromHex("#FF6C9D")),
                            ],
                        },
                    ],
                },
            ];
        }

        protected override void Update()
        {
            base.Update();
            BorderColour = colour.Value.Opacity(selected ? 0.9f : 0.35f);
            accent.Colour = colour.Value;
            modText.Colour = colour.Value.Lighten(0.25f);
            if (activeText != null)
                activeText.Colour = colour.Value.Lighten(0.25f);
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(Colour4.White.Opacity(0.18f), 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
            => background.FadeColour(Colour4.White.Opacity(selected ? 0.14f : 0.055f), 100);

        private static SpriteText stat(string text, Colour4 colour, bool bold = false) => new()
        {
            Text = text,
            Font = bold
                ? FontUsage.Default.With(size: 8.2f, weight: "bold")
                : FontUsage.Default.With(size: 8.2f),
            Colour = colour,
        };
    }

    private static SpriteText label(string text, float size, bool bold = false) => new()
    {
        Text = text,
        RelativeSizeAxes = Axes.X,
        Truncate = true,
        Font = bold
            ? FontUsage.Default.With(size: size, weight: "bold")
            : FontUsage.Default.With(size: size),
        Colour = Colour4.White.Opacity(bold ? 0.88f : 0.62f),
    };

    private static SpriteText sectionLabel(string text) => new()
    {
        Text = text,
        RelativeSizeAxes = Axes.X,
        Truncate = true,
        Font = FontUsage.Default.With(size: 7.5f, weight: "bold"),
        Colour = Colour4.White.Opacity(0.48f),
        Margin = new MarginPadding { Top = 2 },
    };

    private partial class ImportStatusText : SpriteText
    {
        private readonly IBindable<string> status;

        public ImportStatusText(IBindable<string> status)
        {
            this.status = status;
            RelativeSizeAxes = Axes.X;
            Truncate = true;
            Font = FontUsage.Default.With(size: 7.5f);
            Colour = Colour4.White.Opacity(0.62f);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            status.BindValueChanged(statusChanged, true);
        }

        private void statusChanged(ValueChangedEvent<string> change)
        {
            Text = change.NewValue;
            Alpha = string.IsNullOrWhiteSpace(change.NewValue) ? 0 : 1;
        }

        protected override void Dispose(bool isDisposing)
        {
            status.ValueChanged -= statusChanged;
            base.Dispose(isDisposing);
        }
    }

    private static Drawable compactColour(string text, Bindable<Colour4> colour)
    {
        const float scale = 0.7f;
        return new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 43,
            Masking = true,
            Child = new SettingsColour
            {
                LabelText = text,
                Current = colour,
                RelativeSizeAxes = Axes.X,
                Width = 1 / scale,
                Scale = new Vector2(scale),
            },
        };
    }

    private partial class CompactActionButton : CompositeDrawable
    {
        private readonly Action action;
        private readonly Colour4 baseColour;
        private Box background = null!;

        public CompactActionButton(string text, Action action, Colour4 colour)
        {
            this.action = action;
            baseColour = colour;
            RelativeSizeAxes = Axes.X;
            Height = 30;
            Masking = true;
            CornerRadius = 6;
            InternalChildren =
            [
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                new SpriteText
                {
                    Text = text,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = FontUsage.Default.With(size: 10, weight: "bold"),
                    Colour = Colour4.White,
                },
            ];
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(baseColour.Lighten(0.18f), 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
            => background.FadeColour(baseColour, 100);
    }
}
