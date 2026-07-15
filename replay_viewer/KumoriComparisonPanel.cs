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
            Spacing = new Vector2(0, 6),
        };
        foreach (var option in options)
            list.Add(new ReplayCard(option, option.AttemptId == selectedAttemptId, comparisonColour, () => select(option)));

        var children = new List<Drawable>
        {
            label("Choose a replay to compare.", 10.5f, true),
            label("Same map - matching gameplay mods", 9.2f),
        };

        // Keep the primary choice first: users should choose (and see) the replay
        // before adjusting the colours used to render it.
        if (options.Count == 0)
        {
            children.Add(label("No matching replay with cursor data.", 9.8f));
        }
        else
        {
            children.Add(sectionLabel(options.Count == 1 ? "1 ELIGIBLE REPLAY" : $"{options.Count} ELIGIBLE REPLAYS"));
            children.Add(new OsuScrollContainer(Direction.Vertical)
            {
                RelativeSizeAxes = Axes.X,
                Height = Math.Min(options.Count * 78, 228),
                Child = list,
            });
        }

        children.AddRange(
        [
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = Colour4.White.Opacity(0.12f),
            },
            compactColour("Comparison cursor", comparisonColour),
            compactColour("Comparison trail", comparisonTrailColour),
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = Colour4.White.Opacity(0.12f),
            },
            new CompactActionButton("Compare a .osr file", chooseOsr, Colour4.FromHex("#386FA4")),
            label("Temporary - never added to history", 8.5f),
            new ImportStatusText(importStatus),
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = Colour4.White.Opacity(0.12f),
            },
        ]);

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
        private Box selectionWash = null!;

        public ReplayCard(ComparisonContract attempt, bool selected, Bindable<Colour4> colour, Action action)
        {
            RelativeSizeAxes = Axes.X;
            this.attempt = attempt;
            this.selected = selected;
            this.colour = colour;
            this.action = action;
            Height = 72;
            Masking = true;
            CornerRadius = 7;
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
                Height = 16,
                Children =
                [
                    new SpriteText
                    {
                        Text = heading,
                        RelativeSizeAxes = Axes.X,
                        Width = selected ? 0.68f : 1,
                        Truncate = true,
                        Font = FontUsage.Default.With(size: 10.8f, weight: "bold"),
                        Colour = Colour4.White.Opacity(0.96f),
                    },
                ],
            };

            if (selected)
            {
                topLine.Add(new StatusBadge(colour));
            }

            IReadOnlyList<string> orderedMods = ReplayModDisplayOrder.FromKey(attempt.ModsKey);
            var overview = new List<Drawable>();
            if (orderedMods.Count == 0)
                overview.Add(new ModBadge("NM", colour, muted: true));
            else
                overview.AddRange(orderedMods.Select(acronym => new ModBadge(acronym, colour)));

            overview.Add(stat($"{attempt.Accuracy:0.00}%", Colour4.White, 10.2f, true));
            string combo = attempt.MaxCombo > 0
                ? $"{attempt.Combo}/{attempt.MaxCombo}× combo"
                : $"{attempt.Combo}× combo";
            overview.Add(stat(combo, Colour4.White.Opacity(0.76f), 9.2f));

            InternalChildren =
            [
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.FromHex(selected ? "#171B1E" : "#111416"),
                },
                selectionWash = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colour.Value.Opacity(selected ? 0.09f : 0.02f),
                },
                accent = new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 4,
                    Colour = colour.Value,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = 11, Right = 9, Top = 7, Bottom = 6 },
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 4),
                    Children =
                    [
                        topLine,
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(6, 0),
                            Children = overview,
                        },
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(10, 0),
                            Children =
                            [
                                stat($"SCORE  {attempt.Score:N0}", Colour4.White.Opacity(0.7f), 8.6f, true),
                                stat($"100  {attempt.N100}", Colour4.FromHex("#73D89A"), 8.8f, true),
                                stat($"50  {attempt.N50}", Colour4.FromHex("#F0CA63"), 8.8f, true),
                                stat($"MISS  {attempt.Misses}", Colour4.FromHex("#FF6C9D"), 8.8f, true),
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
            selectionWash.Colour = colour.Value.Opacity(selected ? 0.09f : 0.02f);
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(Colour4.FromHex("#202529"), 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
            => background.FadeColour(Colour4.FromHex(selected ? "#171B1E" : "#111416"), 100);

        private static SpriteText stat(string text, Colour4 colour, float size = 9.5f, bool bold = false) => new()
        {
            Text = text,
            Font = bold
                ? FontUsage.Default.With(size: size, weight: "bold")
                : FontUsage.Default.With(size: size),
            Colour = colour,
        };

        private partial class ModBadge : CompositeDrawable
        {
            private readonly Bindable<Colour4> colour;
            private readonly bool muted;
            private Box background = null!;

            public ModBadge(string acronym, Bindable<Colour4> colour, bool muted = false)
            {
                this.colour = colour;
                this.muted = muted;
                Width = acronym.Length > 2 ? 29 : 24;
                Height = 15;
                Masking = true;
                CornerRadius = 4;
                BorderThickness = 1;
                InternalChildren =
                [
                    background = new Box { RelativeSizeAxes = Axes.Both },
                    new SpriteText
                    {
                        Text = acronym,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Font = FontUsage.Default.With(size: 8.2f, weight: "bold"),
                        Colour = muted ? Colour4.White.Opacity(0.62f) : Colour4.White,
                    },
                ];
            }

            protected override void Update()
            {
                base.Update();
                Colour4 badgeColour = muted ? Colour4.White : colour.Value;
                background.Colour = badgeColour.Opacity(muted ? 0.07f : 0.22f);
                BorderColour = badgeColour.Opacity(muted ? 0.18f : 0.55f);
            }
        }

        private partial class StatusBadge : CompositeDrawable
        {
            private readonly Bindable<Colour4> colour;
            private Box background = null!;
            private SpriteText text = null!;

            public StatusBadge(Bindable<Colour4> colour)
            {
                this.colour = colour;
                Anchor = Anchor.TopRight;
                Origin = Anchor.TopRight;
                Width = 60;
                Height = 14;
                Masking = true;
                CornerRadius = 7;
                InternalChildren =
                [
                    background = new Box { RelativeSizeAxes = Axes.Both },
                    text = new SpriteText
                    {
                        Text = "COMPARING",
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Font = FontUsage.Default.With(size: 7.2f, weight: "bold"),
                    },
                ];
            }

            protected override void Update()
            {
                base.Update();
                background.Colour = colour.Value.Opacity(0.2f);
                text.Colour = colour.Value.Lighten(0.3f);
            }
        }
    }

    private static SpriteText label(string text, float size, bool bold = false) => new()
    {
        Text = text,
        RelativeSizeAxes = Axes.X,
        Truncate = true,
        Font = bold
            ? FontUsage.Default.With(size: size, weight: "bold")
            : FontUsage.Default.With(size: size),
        Colour = Colour4.White.Opacity(bold ? 0.94f : 0.74f),
    };

    private static SpriteText sectionLabel(string text) => new()
    {
        Text = text,
        RelativeSizeAxes = Axes.X,
        Truncate = true,
        Font = FontUsage.Default.With(size: 8.5f, weight: "bold"),
        Colour = Colour4.White.Opacity(0.7f),
        Margin = new MarginPadding { Top = 4 },
    };

    private partial class ImportStatusText : SpriteText
    {
        private readonly IBindable<string> status;

        public ImportStatusText(IBindable<string> status)
        {
            this.status = status;
            RelativeSizeAxes = Axes.X;
            Truncate = true;
            Font = FontUsage.Default.With(size: 8.5f);
            Colour = Colour4.White.Opacity(0.74f);
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
        const float scale = 0.85f;
        return new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 53,
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
            Height = 34;
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
                    Font = FontUsage.Default.With(size: 10.5f, weight: "bold"),
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
