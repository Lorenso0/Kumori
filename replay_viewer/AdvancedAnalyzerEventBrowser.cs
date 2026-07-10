using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerEventBrowser : PlayerSettingsGroup
{
    private readonly AdvancedAnalyzerViewModel viewModel;
    private FillFlowContainer list = null!;
    private SpriteText count = null!;
    private EventTimeline timeline = null!;

    public AdvancedAnalyzerEventBrowser(AdvancedAnalyzerViewModel viewModel)
        : base("Review events")
    {
        this.viewModel = viewModel;
        Width = KumoriAnalyzerSidebar.COMPACT_WIDTH - 20;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Children = new Drawable[]
        {
            count = new SpriteText
            {
                Font = FontUsage.Default.With(size: 12, weight: "bold"),
                Colour = Color4.White.Opacity(0.72f),
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Children = new Drawable[]
                {
                    filterRow(
                        new FilterCard("Misses", viewModel.CountFor(KumoriTimelineMarkerKind.Miss), KumoriTimelineMarkerKind.Miss, viewModel.ShowMisses),
                        new FilterCard("Slider breaks", viewModel.CountFor(KumoriTimelineMarkerKind.SliderBreak), KumoriTimelineMarkerKind.SliderBreak, viewModel.ShowSliderBreaks)),
                    filterRow(
                        new FilterCard("50s", viewModel.CountFor(KumoriTimelineMarkerKind.Meh), KumoriTimelineMarkerKind.Meh, viewModel.ShowMehs),
                        new FilterCard("100s", viewModel.CountFor(KumoriTimelineMarkerKind.Ok), KumoriTimelineMarkerKind.Ok, viewModel.ShowOks)),
                },
            },
            timeline = new EventTimeline(viewModel.AllEntries),
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 1,
                Colour = Color4.White.Opacity(0.14f),
            },
            list = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 5),
            },
        };

        viewModel.FiltersChanged += rebuild;
        viewModel.SelectionChanged += rebuild;
        rebuild();
    }

    private void rebuild()
    {
        if (list == null)
            return;

        list.Clear();
        count.Text = $"Showing {viewModel.VisibleEntries.Count} of {viewModel.TotalCount}";
        timeline.SetVisible(viewModel.VisibleEntries);

        if (viewModel.VisibleEntries.Count == 0)
        {
            list.Add(new SpriteText
            {
                Text = viewModel.TotalCount == 0 ? "No bad hits to inspect." : "No events match the filters.",
                Font = FontUsage.Default.With(size: 14),
                Colour = Color4.White.Opacity(0.6f),
            });
            return;
        }

        for (int i = 0; i < viewModel.VisibleEntries.Count; i++)
        {
            int index = i;
            list.Add(new EventButton(viewModel.VisibleEntries[i], i == viewModel.SelectedIndex, () => viewModel.Select(index)));
        }
    }

    private static FillFlowContainer filterRow(params Drawable[] children) => new()
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Horizontal,
        Spacing = new Vector2(6, 0),
        Children = children,
    };

    protected override void Dispose(bool isDisposing)
    {
        viewModel.FiltersChanged -= rebuild;
        viewModel.SelectionChanged -= rebuild;
        base.Dispose(isDisposing);
    }

    private partial class EventButton : CompositeDrawable
    {
        private readonly MissAnalysisEntry entry;
        private readonly Action action;
        private readonly bool selected;
        private Box background = null!;

        public EventButton(MissAnalysisEntry entry, bool selected, Action action)
        {
            this.entry = entry;
            this.action = action;
            this.selected = selected;
            RelativeSizeAxes = Axes.X;
            Height = 52;
            Masking = true;
            CornerRadius = 4;
            BorderThickness = selected ? 1 : 0;
            BorderColour = AdvancedAnalyzerColours.For(entry.Kind).Opacity(0.55f);
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.White.Opacity(selected ? 0.15f : 0.045f) },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AdvancedAnalyzerColours.For(entry.Kind) },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 9, Vertical = 7 },
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        new GridContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 17,
                            ColumnDimensions = new[]
                            {
                                new Dimension(GridSizeMode.Relative, 0.55f),
                                new Dimension(GridSizeMode.Relative, 0.45f),
                            },
                            Content = new[]
                            {
                                new Drawable[]
                                {
                                    new SpriteText
                                    {
                                        Text = $"#{entry.Index}  {entry.Label}",
                                        Font = FontUsage.Default.With(size: 12, weight: "bold"),
                                        Colour = AdvancedAnalyzerColours.For(entry.Kind),
                                        RelativeSizeAxes = Axes.X,
                                        Truncate = true,
                                    },
                                    new SpriteText
                                    {
                                        Text = AdvancedAnalyzerMetrics.FormatTime(entry.EventTime),
                                        Font = FontUsage.Default.With(size: 11, weight: "bold"),
                                        Colour = Color4.White.Opacity(0.72f),
                                        Anchor = Anchor.TopRight,
                                        Origin = Anchor.TopRight,
                                    },
                                },
                            },
                        },
                        new SpriteText
                        {
                            Text = $"{entry.ObjectType}  -  {AdvancedAnalyzerMetrics.Diagnosis(entry)}",
                            Font = FontUsage.Default.With(size: 10),
                            Colour = Color4.White.Opacity(0.62f),
                            RelativeSizeAxes = Axes.X,
                            Truncate = true,
                        },
                    },
                },
            };
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(Color4.White.Opacity(0.13f), 120);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(Color4.White.Opacity(0.06f), 180);
            base.OnHoverLost(e);
        }
    }

    private partial class FilterCard : CompositeDrawable
    {
        private readonly Bindable<bool> current;
        private readonly Color4 colour;
        private Box background = null!;

        public FilterCard(string label, int value, KumoriTimelineMarkerKind kind, BindableBool source)
        {
            current = source.GetBoundCopy();
            colour = AdvancedAnalyzerColours.For(kind);
            Width = 86;
            Height = 56;
            Masking = true;
            CornerRadius = 4;

            InternalChildren = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(7),
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 1),
                    Children = new Drawable[]
                    {
                        new SpriteText
                        {
                            Text = label,
                            Font = FontUsage.Default.With(size: 10, weight: "bold"),
                            Colour = Color4.White.Opacity(0.76f),
                            RelativeSizeAxes = Axes.X,
                            Truncate = true,
                        },
                        new SpriteText
                        {
                            Text = value.ToString(),
                            Font = FontUsage.Default.With(size: 17, weight: "bold"),
                            Colour = colour,
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 3,
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = colour.Opacity(0.14f) },
                                new Box
                                {
                                    RelativeSizeAxes = Axes.Y,
                                    Width = 70 * Math.Clamp(value / 30f, 0.08f, 1),
                                    Colour = colour.Opacity(0.78f),
                                },
                            },
                        },
                    },
                },
            };

            current.BindValueChanged(_ => updateState(), true);
        }

        private void updateState()
        {
            BorderThickness = current.Value ? 1.5f : 1;
            BorderColour = colour.Opacity(current.Value ? 0.9f : 0.28f);
            background.Colour = current.Value ? colour.Opacity(0.1f) : Color4.White.Opacity(0.025f);
        }

        protected override bool OnClick(ClickEvent e)
        {
            current.Value = !current.Value;
            return true;
        }
    }

    private partial class EventTimeline : CompositeDrawable
    {
        private readonly IReadOnlyList<MissAnalysisEntry> allEntries;
        private IReadOnlyList<MissAnalysisEntry> visibleEntries = [];
        private float renderedWidth;

        public EventTimeline(IReadOnlyList<MissAnalysisEntry> allEntries)
        {
            this.allEntries = allEntries;
            RelativeSizeAxes = Axes.X;
            Height = 54;
            Masking = true;
            CornerRadius = 4;
        }

        public void SetVisible(IReadOnlyList<MissAnalysisEntry> entries)
        {
            visibleEntries = entries;
            rebuild();
        }

        protected override void Update()
        {
            base.Update();
            if (Math.Abs(renderedWidth - DrawWidth) > 1)
                rebuild();
        }

        private void rebuild()
        {
            renderedWidth = DrawWidth;
            ClearInternal();
            if (DrawWidth <= 1)
                return;

            AddInternal(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.White.Opacity(0.035f) });
            float left = 8;
            float width = DrawWidth - 16;
            float baseline = 27;
            AddInternal(new Box
            {
                Position = new Vector2(left, baseline),
                Size = new Vector2(width, 1),
                Colour = Color4.White.Opacity(0.28f),
            });

            double endTime = Math.Max(1, allEntries.Count == 0 ? 1 : allEntries.Max(entry => entry.EventTime));
            foreach (MissAnalysisEntry entry in visibleEntries)
            {
                AddInternal(new Box
                {
                    Position = new Vector2(left + width * (float)(entry.EventTime / endTime), 14),
                    Size = new Vector2(2, 13),
                    Colour = AdvancedAnalyzerColours.For(entry.Kind),
                });
            }

            AddInternal(new SpriteText
            {
                Text = "00:00",
                Position = new Vector2(left, 33),
                Font = FontUsage.Default.With(size: 9),
                Colour = Color4.White.Opacity(0.5f),
            });
            AddInternal(new SpriteText
            {
                Text = AdvancedAnalyzerMetrics.FormatTime(endTime),
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new Vector2(-left, 33),
                Font = FontUsage.Default.With(size: 9),
                Colour = Color4.White.Opacity(0.5f),
            });
        }
    }
}
