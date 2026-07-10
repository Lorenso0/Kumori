using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

/// <summary>
/// Kumori's own seek bar: a floating rounded pill near the bottom of the
/// window. Rendered underneath the gameplay layer, so hit objects, the cursor
/// and the HUD always draw over it. Time and seeking are supplied as
/// delegates. It overlays simulated judgement markers above the track and
/// tosu actual markers below it.
/// </summary>
internal partial class KumoriSeekBar : CompositeDrawable
{
    public const float ReservedBottomHeight = bottom_offset + bar_height + 10;
    // Kumori theme palette (Python_old/osu_tracker/core/themes.py).
    private static readonly Color4 fill_colour = Color4Extensions.FromHex("#8b5cf6"); // PURPLE
    private static readonly Color4 miss_colour = Color4Extensions.FromHex("#ff4f7b"); // HIT_MISS
    private static readonly Color4 meh_colour = Color4Extensions.FromHex("#ffd43b"); // HIT_MEH (50)
    private static readonly Color4 ok_colour = Color4Extensions.FromHex("#9bdc28"); // HIT_OK (100)

    private const float bar_height = 46;
    private const float bottom_offset = 22;
    private const float track_height = 8;
    private const float marker_lane_height = 16;

    /// <summary>Per-kind marker visibility; bind these to persisted config.</summary>
    public readonly BindableBool ShowMisses = new BindableBool(true);
    public readonly BindableBool ShowMehs = new BindableBool(true);
    public readonly BindableBool ShowOks = new BindableBool(true);
    public readonly BindableBool ShowSliderBreaks = new BindableBool(true);

    private readonly double firstHitTime;
    private readonly double lastHitTime;
    private readonly Func<double> currentTime;
    private readonly Action<double> performSeek;

    private readonly HashSet<(int time, KumoriTimelineMarkerKind kind)> recorded = new HashSet<(int, KumoriTimelineMarkerKind)>();
    private readonly Dictionary<KumoriTimelineMarkerKind, int> simulatedCounts = new Dictionary<KumoriTimelineMarkerKind, int>();

    private Container track = null!;
    private Box fill = null!;
    private Circle playhead = null!;
    private Container missLane = null!;
    private Container mehLane = null!;
    private Container okLane = null!;
    private Container sliderBreakLane = null!;
    private SpriteText statusText = null!;
    private AdvancedAnalyzerViewModel? analyzerViewModel;
    private Action<MissAnalysisEntry>? activateAnalysisEntry;
    private Action<MissAnalysisEntry, Vector2>? showAnalysisPopup;
    private Action? hideAnalysisPopup;
    private InputManager? inputManager;
    private AnalysisTimelineMarker? hoveredAnalysisMarker;
    private readonly List<AnalysisTimelineMarker> analysisMarkers = [];

    private FinalHitsContract? finalHits;
    private double? actualAccuracy;
    private int framesSeen;
    private bool geometryLogged;
    private bool comparisonLogged;
    private bool inputBlocked;

    public override bool HandlePositionalInput => !inputBlocked;

    public void SetInputBlocked(bool blocked) => Schedule(() =>
    {
        inputBlocked = blocked;
        if (!blocked)
            return;

        if (hoveredAnalysisMarker != null)
            analyzerViewModel?.ClearHovered(hoveredAnalysisMarker.Entry);
        hoveredAnalysisMarker = null;
        hidePopup();
    });

    public KumoriSeekBar(double firstHitTime, double lastHitTime, Func<double> currentTime, Action<double> performSeek)
    {
        this.firstHitTime = firstHitTime;
        this.lastHitTime = Math.Max(firstHitTime + 1, lastHitTime);
        this.currentTime = currentTime;
        this.performSeek = performSeek;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Anchor = Anchor.BottomCentre;
        Origin = Anchor.BottomCentre;
        RelativeSizeAxes = Axes.X;
        Width = 0.94f;
        Height = bar_height;
        Y = -bottom_offset;

        Masking = true;
        CornerRadius = bar_height / 2;
        EdgeEffect = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Colour = Color4.Black.Opacity(0.35f),
            Radius = 10,
        };

        static Container makeLane() => new Container
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.BottomLeft,
            RelativeSizeAxes = Axes.X,
            Height = marker_lane_height,
            Y = -(track_height + 3),
        };

        static Container makeActualLane() => new Container
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.TopLeft,
            RelativeSizeAxes = Axes.X,
            Height = marker_lane_height,
            Y = -(track_height - 1),
        };

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.Black.Opacity(0.6f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Horizontal = 26, Vertical = 7 },
                Children = new Drawable[]
                {
                    okLane = makeLane(),
                    mehLane = makeLane(),
                    missLane = makeLane(),
                    sliderBreakLane = makeActualLane(),
                    track = new Container
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        RelativeSizeAxes = Axes.X,
                        Height = track_height,
                        Masking = true,
                        CornerRadius = track_height / 2,
                        Children = new Drawable[]
                        {
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = Color4.White.Opacity(0.3f),
                            },
                            fill = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Width = 0,
                                Colour = fill_colour,
                            },
                        },
                    },
                    playhead = new Circle
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.Centre,
                        RelativePositionAxes = Axes.X,
                        Size = new Vector2(14),
                        Y = -track_height / 2,
                        Colour = Color4.White,
                    },
                    statusText = new SpriteText
                    {
                        Anchor = Anchor.BottomRight,
                        Origin = Anchor.BottomRight,
                        X = -4,
                        Y = -track_height - 9,
                        Font = FontUsage.Default.With(size: 13),
                        Alpha = 0,
                    },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        inputManager = GetContainingInputManager();

        ShowMisses.BindValueChanged(v => missLane.Alpha = v.NewValue ? 1 : 0, true);
        ShowMehs.BindValueChanged(v => mehLane.Alpha = v.NewValue ? 1 : 0, true);
        ShowOks.BindValueChanged(v => okLane.Alpha = v.NewValue ? 1 : 0, true);
        ShowSliderBreaks.BindValueChanged(v => sliderBreakLane.Alpha = v.NewValue ? 1 : 0, true);
    }

    /// <summary>
    /// Records a simulated judgement marker. Markers are keyed by rounded map
    /// time and kind, so seeking backwards never duplicates them.
    /// </summary>
    public bool AddMarker(double time, KumoriTimelineMarkerKind kind)
    {
        if (!recorded.Add(((int)Math.Round(time), kind)))
            return false;

        simulatedCounts[kind] = simulatedCounts.GetValueOrDefault(kind) + 1;
        Schedule(() => addMarker(time, kind));
        return true;
    }

    public void AddMarkers(IEnumerable<KumoriTimelineMarker> markers) => Schedule(() =>
    {
        foreach (KumoriTimelineMarker marker in markers)
        {
            if (!recorded.Add(((int)Math.Round(marker.Time), marker.Kind)))
                continue;

            simulatedCounts[marker.Kind] = simulatedCounts.GetValueOrDefault(marker.Kind) + 1;
            addMarker(marker.Time, marker.Kind);
        }
    });

    public void SetMarkers(IEnumerable<KumoriTimelineMarker> markers) => Schedule(() =>
    {
        recorded.Clear();
        simulatedCounts.Clear();
        missLane.Clear();
        mehLane.Clear();
        okLane.Clear();
        sliderBreakLane.Clear();
        analysisMarkers.Clear();
        comparisonLogged = false;
        foreach (KumoriTimelineMarker marker in markers)
        {
            if (!recorded.Add(((int)Math.Round(marker.Time), marker.Kind)))
                continue;
            simulatedCounts[marker.Kind] = simulatedCounts.GetValueOrDefault(marker.Kind) + 1;
            addMarker(marker.Time, marker.Kind);
        }
    });

    public void SetFinalHits(FinalHitsContract? hits) => finalHits = hits;

    public void SetActualAccuracy(double accuracy) => actualAccuracy = accuracy;

    public void BindAnalyzer(AdvancedAnalyzerViewModel viewModel, Action<MissAnalysisEntry> activateEntry)
    {
        unbindAnalyzer();
        analyzerViewModel = viewModel;
        activateAnalysisEntry = activateEntry;
        viewModel.FiltersChanged += rebuildAnalysisMarkers;
        viewModel.SelectionChanged += updateAnalysisMarkerStates;
        viewModel.HoverChanged += updateAnalysisMarkerStates;
        viewModel.AnalyzerVisibilityChanged += updateAnalysisMarkerStates;
        rebuildAnalysisMarkers();
    }

    public void SetAnalysisPopup(Action<MissAnalysisEntry, Vector2> show, Action hide)
    {
        showAnalysisPopup = show;
        hideAnalysisPopup = hide;
    }

    public void AddActualJudgements(IEnumerable<JudgementEventContract> events)
        => AddMarkers(KumoriTimelineMarkers.FromContract(events));

    protected override void Update()
    {
        base.Update();

        float progress = fraction(currentTime());
        fill.Width = progress;
        playhead.X = progress;
        updateTimelineHover();

        // One-shot geometry diagnostic around 2 s in, so runtime.log can
        // prove the bar's on-screen placement if it is reported invisible.
        if (!geometryLogged && ++framesSeen == 120)
        {
            geometryLogged = true;
            Logger.Log($"Kumori: seek bar geometry quad={ScreenSpaceDrawQuad.AABBFloat}, "
                       + $"drawSize={DrawSize}, alpha={Alpha}, present={IsPresent}, "
                       + $"parent={Parent?.GetType().Name}");
        }
    }

    private float fraction(double time)
        => (float)Math.Clamp((time - firstHitTime) / (lastHitTime - firstHitTime), 0, 1);

    private void addMarker(double time, KumoriTimelineMarkerKind kind)
    {
        (Container lane, Color4 colour, float height, float width) = kind switch
        {
            KumoriTimelineMarkerKind.Miss => (missLane, miss_colour, marker_lane_height, 3.5f),
            KumoriTimelineMarkerKind.Meh => (mehLane, meh_colour, marker_lane_height * 0.55f, 3f),
            KumoriTimelineMarkerKind.Ok => (okLane, ok_colour, marker_lane_height * 0.55f, 3f),
            _ => (sliderBreakLane, miss_colour.Opacity(0.45f), marker_lane_height * 0.55f, 5.5f),
        };

        Drawable marker = kind == KumoriTimelineMarkerKind.SliderBreak
            ? new Box
            {
                RelativePositionAxes = Axes.X,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                X = fraction(time),
                Width = width,
                Height = height,
                Colour = colour,
            }
            : new Circle
            {
                RelativePositionAxes = Axes.X,
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomCentre,
                X = fraction(time),
                Width = width,
                Height = height,
                Colour = colour,
            };

        lane.Add(marker);
    }

    private void rebuildAnalysisMarkers() => Schedule(() =>
    {
        if (analyzerViewModel == null)
            return;

        recorded.Clear();
        simulatedCounts.Clear();
        missLane.Clear();
        mehLane.Clear();
        okLane.Clear();
        sliderBreakLane.Clear();
        analysisMarkers.Clear();
        comparisonLogged = false;

        foreach (MissAnalysisEntry entry in analyzerViewModel.AllEntries)
        {
            recorded.Add(((int)Math.Round(entry.EventTime), entry.Kind));
            simulatedCounts[entry.Kind] = simulatedCounts.GetValueOrDefault(entry.Kind) + 1;
            addAnalysisMarker(entry);
        }
        updateAnalysisMarkerStates();
    });

    private void addAnalysisMarker(MissAnalysisEntry entry)
    {
        (Container lane, Color4 colour, float height, float width, bool belowTrack) = entry.Kind switch
        {
            KumoriTimelineMarkerKind.Miss => (missLane, miss_colour, marker_lane_height, 3.5f, false),
            KumoriTimelineMarkerKind.Meh => (mehLane, meh_colour, marker_lane_height * 0.55f, 3f, false),
            KumoriTimelineMarkerKind.Ok => (okLane, ok_colour, marker_lane_height * 0.55f, 3f, false),
            _ => (sliderBreakLane, miss_colour.Opacity(0.45f), marker_lane_height * 0.55f, 5.5f, true),
        };

        var marker = new AnalysisTimelineMarker(
            entry,
            analyzerViewModel!,
            activateAnalysisEntry!,
            colour,
            width,
            height,
            belowTrack)
        {
            RelativePositionAxes = Axes.X,
            X = fraction(entry.EventTime),
        };
        analysisMarkers.Add(marker);
        lane.Add(marker);
    }

    private void updateAnalysisMarkerStates()
    {
        if (analyzerViewModel == null)
            return;
        foreach (AnalysisTimelineMarker marker in analysisMarkers)
            marker.SetState(
                analyzerViewModel.AnalyzerOpen && ReferenceEquals(marker.Entry, analyzerViewModel.SelectedEntry),
                ReferenceEquals(marker.Entry, analyzerViewModel.HoveredEntry));

        // Direct seek-bar hover is positioned by the polling path below.
        // Hover arriving from the sidebar or mini timeline should display the
        // same card at the corresponding bottom timeline marker.
        if (hoveredAnalysisMarker != null)
            return;

        AnalysisTimelineMarker? externallyHovered = analyzerViewModel.HoveredEntry == null
            ? null
            : analysisMarkers.FirstOrDefault(marker =>
                ReferenceEquals(marker.Entry, analyzerViewModel.HoveredEntry)
                && analyzerViewModel.VisibleEntries.Contains(marker.Entry));
        if (externallyHovered == null)
        {
            hidePopup();
            return;
        }

        showPopup(
            externallyHovered.Entry,
            new Vector2(externallyHovered.ScreenSpaceDrawQuad.Centre.X, externallyHovered.ScreenSpaceDrawQuad.TopLeft.Y));
    }

    private void showPopup(MissAnalysisEntry entry, Vector2 screenPosition)
        => showAnalysisPopup?.Invoke(entry, screenPosition);

    private void hidePopup() => hideAnalysisPopup?.Invoke();

    private void updateTimelineHover()
    {
        if (inputBlocked || inputManager == null || analyzerViewModel == null)
            return;

        Vector2 mousePosition = inputManager.CurrentState.Mouse.Position;
        AnalysisTimelineMarker? next = analysisMarkers.LastOrDefault(marker =>
            analyzerViewModel.VisibleEntries.Contains(marker.Entry)
            && marker.IsPresent
            && marker.ReceivePositionalInputAt(mousePosition));
        if (ReferenceEquals(next, hoveredAnalysisMarker))
            return;

        if (hoveredAnalysisMarker != null)
            analyzerViewModel.ClearHovered(hoveredAnalysisMarker.Entry);

        hoveredAnalysisMarker = next;
        if (next == null)
        {
            hidePopup();
            return;
        }

        analyzerViewModel.SetHovered(next.Entry);
        showPopup(next.Entry, new Vector2(next.ScreenSpaceDrawQuad.Centre.X, next.ScreenSpaceDrawQuad.TopLeft.Y));
        Logger.Log($"Kumori: showing timeline event popup for #{next.Entry.Index} at {next.Entry.EventTime:0}ms.");
    }

    private void addActualMarker(JudgementEventContract judgement)
    {
        if (KumoriTimelineMarkers.KindFromContract(judgement.Kind) is { } kind)
            addMarker(judgement.MapTimeMs, kind);
    }

    public static KumoriTimelineMarkerKind? MarkerKindFromContract(string? kind)
        => KumoriTimelineMarkers.KindFromContract(kind);

    public static KumoriTimelineMarkerKind? MarkerKindFromHitResult(osu.Game.Rulesets.Scoring.HitResult result)
        => KumoriTimelineMarkers.KindFromHitResult(result);

    private void updateComparisonStatus()
    {
        if (finalHits is not FinalHitsContract hits)
            return;
        if (currentTime() < lastHitTime)
            return;

        int simMisses = simulatedCounts.GetValueOrDefault(KumoriTimelineMarkerKind.Miss);
        int sim50s = simulatedCounts.GetValueOrDefault(KumoriTimelineMarkerKind.Meh);
        int sim100s = simulatedCounts.GetValueOrDefault(KumoriTimelineMarkerKind.Ok);
        int totalObjects = Math.Max(0, hits.N300 + hits.N100 + hits.N50 + hits.Misses);
        int sim300s = Math.Max(0, totalObjects - sim100s - sim50s - simMisses);

        List<(string label, Color4 colour)> differences = [];
        if (simMisses != hits.Misses)
            differences.Add(($"sim {simMisses}xmiss / play {hits.Misses}xmiss", miss_colour));
        if (sim50s != hits.N50)
            differences.Add(($"sim {sim50s}x50 / play {hits.N50}x50", meh_colour));
        if (sim100s != hits.N100)
            differences.Add(($"sim {sim100s}x100 / play {hits.N100}x100", ok_colour));
        if (sim300s != hits.N300)
            differences.Add(($"sim {sim300s}x300 / play {hits.N300}x300", Color4.White));

        if (differences.Count == 0)
        {
            statusText.Text = actualAccuracy is { } accuracy
                ? $"play {accuracy:0.00}%  |  sim matches"
                : "sim matches play";
            statusText.Colour = Color4.White.Opacity(0.55f);
            statusText.Alpha = 0.6f;
        }
        else
        {
            var prefix = actualAccuracy is { } accuracy
                ? $"play {accuracy:0.00}%"
                : "play result";
            statusText.Text = $"{prefix}  |  sim drift: " + string.Join("  |  ", differences.Select(d => d.label));
            statusText.Colour = differences[0].colour;
            statusText.Alpha = 1;
        }

        if (!comparisonLogged)
        {
            comparisonLogged = true;
            Logger.Log($"Kumori: judgement comparison sim={{300:{sim300s},100:{sim100s},50:{sim50s},miss:{simMisses}}} "
                       + $"play={{300:{hits.N300},100:{hits.N100},50:{hits.N50},miss:{hits.Misses}}} "
                       + $"match={differences.Count == 0}");
        }
    }

    // Seeking

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        seekToPosition(e.ScreenSpaceMousePosition);
        return true;
    }

    protected override bool OnDragStart(DragStartEvent e) => true;

    protected override void OnDrag(DragEvent e) => seekToPosition(e.ScreenSpaceMousePosition);

    protected override bool OnHover(HoverEvent e)
    {
        track.ResizeHeightTo(track_height * 1.5f, 200, Easing.OutQuint);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        track.ResizeHeightTo(track_height, 400, Easing.OutQuint);
        base.OnHoverLost(e);
    }

    private void seekToPosition(Vector2 screenSpacePosition)
    {
        float frac = Math.Clamp(track.ToLocalSpace(screenSpacePosition).X / Math.Max(1, track.DrawWidth), 0, 1);
        performSeek(firstHitTime + frac * (lastHitTime - firstHitTime));
    }

    private void unbindAnalyzer()
    {
        if (analyzerViewModel == null)
            return;
        analyzerViewModel.FiltersChanged -= rebuildAnalysisMarkers;
        analyzerViewModel.SelectionChanged -= updateAnalysisMarkerStates;
        analyzerViewModel.HoverChanged -= updateAnalysisMarkerStates;
        analyzerViewModel.AnalyzerVisibilityChanged -= updateAnalysisMarkerStates;
        analyzerViewModel = null;
        activateAnalysisEntry = null;
        hoveredAnalysisMarker = null;
        hidePopup();
        showAnalysisPopup = null;
        hideAnalysisPopup = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        unbindAnalyzer();
        base.Dispose(isDisposing);
    }

    private partial class AnalysisTimelineMarker : CompositeDrawable
    {
        private readonly AdvancedAnalyzerViewModel viewModel;
        private readonly Action<MissAnalysisEntry> activate;
        private readonly Drawable visual;
        private readonly float normalWidth;
        private readonly float normalHeight;

        public MissAnalysisEntry Entry { get; }
        public override bool HandlePositionalInput => true;

        public AnalysisTimelineMarker(
            MissAnalysisEntry entry,
            AdvancedAnalyzerViewModel viewModel,
            Action<MissAnalysisEntry> activate,
            Color4 colour,
            float width,
            float height,
            bool belowTrack)
        {
            Entry = entry;
            this.viewModel = viewModel;
            this.activate = activate;
            normalWidth = width;
            normalHeight = height;
            Width = 16;
            Height = marker_lane_height;
            Anchor = belowTrack ? Anchor.TopLeft : Anchor.BottomLeft;
            Origin = belowTrack ? Anchor.TopCentre : Anchor.BottomCentre;

            visual = belowTrack
                ? new Box
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Size = new Vector2(width, height),
                    Colour = colour,
                }
                : new Circle
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    Size = new Vector2(width, height),
                    Colour = colour,
                };
            InternalChild = visual;
        }

        public void SetState(bool selected, bool hovered)
        {
            bool emphasized = selected || hovered;
            visual.ResizeTo(new Vector2(emphasized ? Math.Max(6, normalWidth * 1.7f) : normalWidth,
                emphasized ? normalHeight * 1.2f : normalHeight), 100, Easing.OutQuint);
            visual.Alpha = emphasized ? 1 : 0.82f;
        }

        protected override bool OnMouseDown(MouseDownEvent e) => true;

        protected override bool OnClick(ClickEvent e)
        {
            activate(Entry);
            return true;
        }

    }
}
