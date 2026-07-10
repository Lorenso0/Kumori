using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Screens.Play.PlayerSettings;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerEventBrowser : PlayerSettingsGroup
{
    private readonly AdvancedAnalyzerViewModel viewModel;
    private readonly Action<Drawable> scrollIntoView;
    private FillFlowContainer list = null!;
    private SpriteText count = null!;
    private OsuTextFlowContainer patterns = null!;
    private OsuTextFlowContainer trends = null!;
    private EventTimeline timeline = null!;
    private readonly List<EventButton> eventButtons = [];

    public AdvancedAnalyzerEventBrowser(AdvancedAnalyzerViewModel viewModel, Action<Drawable> scrollIntoView)
        : base("Review events")
    {
        this.viewModel = viewModel;
        this.scrollIntoView = scrollIntoView;
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
            patterns = new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: 10))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Colour = Color4.White.Opacity(0.62f),
            },
            trends = new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: 10))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Colour = Color4.Cyan.Opacity(0.68f),
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
            timeline = new EventTimeline(viewModel),
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
        viewModel.HoverChanged += updateHover;
        rebuild();
    }

    private void rebuild()
    {
        if (list == null)
            return;

        list.Clear();
        eventButtons.Clear();
        count.Text = $"Showing {viewModel.VisibleEntries.Count} of {viewModel.TotalCount}";
        patterns.Text = AdvancedAnalyzerMetrics.PatternSummary(viewModel.AllEntries);
        trends.Text = viewModel.RecentTrendSummary;
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

        EventButton? selectedButton = null;
        for (int i = 0; i < viewModel.VisibleEntries.Count; i++)
        {
            int index = i;
            MissAnalysisEntry entry = viewModel.VisibleEntries[i];
            var button = new EventButton(
                entry,
                i == viewModel.SelectedIndex,
                () => viewModel.Select(index),
                () => viewModel.SetHovered(entry),
                () => viewModel.ClearHovered(entry));
            eventButtons.Add(button);
            list.Add(button);
            if (i == viewModel.SelectedIndex)
                selectedButton = button;
        }
        updateHover();
        if (selectedButton != null)
        {
            EventButton button = selectedButton;
            Scheduler.Add(() => scrollIntoView(button));
        }
    }

    private void updateHover()
    {
        timeline?.SetHovered(viewModel.HoveredEntry);
        foreach (EventButton button in eventButtons)
            button.SetExternallyHovered(ReferenceEquals(button.Entry, viewModel.HoveredEntry));
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
        viewModel.HoverChanged -= updateHover;
        base.Dispose(isDisposing);
    }

    private partial class EventButton : CompositeDrawable
    {
        private readonly MissAnalysisEntry entry;
        private readonly Action action;
        private readonly Action hoverAction;
        private readonly Action hoverLostAction;
        private readonly bool selected;
        private Box background = null!;

        public MissAnalysisEntry Entry => entry;

        public EventButton(MissAnalysisEntry entry, bool selected, Action action, Action hoverAction, Action hoverLostAction)
        {
            this.entry = entry;
            this.action = action;
            this.hoverAction = hoverAction;
            this.hoverLostAction = hoverLostAction;
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
            hoverAction();
            background.FadeColour(Color4.White.Opacity(0.13f), 120);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hoverLostAction();
            background.FadeColour(Color4.White.Opacity(selected ? 0.15f : 0.045f), 180);
            base.OnHoverLost(e);
        }

        public void SetExternallyHovered(bool hovered)
        {
            if (background == null || IsHovered)
                return;
            background.FadeColour(Color4.White.Opacity(hovered ? 0.13f : selected ? 0.15f : 0.045f), 100);
            BorderThickness = selected || hovered ? 1 : 0;
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
        private readonly AdvancedAnalyzerViewModel viewModel;
        private IReadOnlyList<MissAnalysisEntry> visibleEntries = [];
        private readonly List<EventTimelineMarker> markers = [];
        private float renderedWidth;

        public EventTimeline(AdvancedAnalyzerViewModel viewModel)
        {
            this.viewModel = viewModel;
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

        public void SetHovered(MissAnalysisEntry? entry)
        {
            foreach (EventTimelineMarker marker in markers)
                marker.SetState(
                    ReferenceEquals(marker.Entry, viewModel.SelectedEntry),
                    ReferenceEquals(marker.Entry, entry));
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
            markers.Clear();
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

            IReadOnlyList<MissAnalysisEntry> allEntries = viewModel.AllEntries;
            double endTime = Math.Max(1, allEntries.Count == 0 ? 1 : allEntries.Max(entry => entry.EventTime));
            foreach (MissAnalysisEntry entry in visibleEntries)
            {
                var marker = new EventTimelineMarker(
                    entry,
                    viewModel,
                    new Vector2(left + width * (float)(entry.EventTime / endTime), 14),
                    ReferenceEquals(entry, viewModel.SelectedEntry),
                    ReferenceEquals(entry, viewModel.HoveredEntry));
                markers.Add(marker);
                AddInternal(marker);
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

        private partial class EventTimelineMarker : CompositeDrawable, IHasCustomTooltip<MissAnalysisEntry>
        {
            private readonly MissAnalysisEntry entry;
            private readonly AdvancedAnalyzerViewModel viewModel;
            private readonly Box marker;

            public MissAnalysisEntry Entry => entry;

            public EventTimelineMarker(
                MissAnalysisEntry entry,
                AdvancedAnalyzerViewModel viewModel,
                Vector2 position,
                bool selected,
                bool hovered)
            {
                this.entry = entry;
                this.viewModel = viewModel;
                Position = position;
                Size = new Vector2(10, 16);
                Origin = Anchor.TopCentre;
                InternalChild = marker = new Box
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Colour = AdvancedAnalyzerColours.For(entry.Kind),
                };
                SetState(selected, hovered);
            }

            public void SetState(bool selected, bool hovered)
                => marker.Size = new Vector2(selected || hovered ? 4 : 2, selected || hovered ? 16 : 13);

            public override bool HandlePositionalInput => true;
            public MissAnalysisEntry TooltipContent => entry;
            public ITooltip<MissAnalysisEntry> GetCustomTooltip() => new AdvancedAnalyzerEventTooltip();

            protected override bool OnHover(HoverEvent e)
            {
                viewModel.SetHovered(entry);
                return true;
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                viewModel.ClearHovered(entry);
                base.OnHoverLost(e);
            }

            protected override bool OnClick(ClickEvent e)
            {
                viewModel.Select(entry);
                return true;
            }
        }
    }
}
