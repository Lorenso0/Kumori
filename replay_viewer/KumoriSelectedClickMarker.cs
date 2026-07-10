using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Extensions.Color4Extensions;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Osu.UI.ReplayAnalysis;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class KumoriSelectedClickMarker : CompositeDrawable
{
    private const double display_length = 600;
    private const double time_match_tolerance = 3;

    private readonly OsuPlayfield playfield;
    private MissAnalysisEntry? selectedEntry;
    private Color4 selectedColour;
    private bool recolourSelectedNote;
    private DrawableOsuHitObject? highlightedDrawable;
    private HitObject? highlightedHitObject;
    private Color4 originalColour;

    public KumoriSelectedClickMarker(OsuPlayfield playfield)
    {
        this.playfield = playfield;
        RelativeSizeAxes = Axes.Both;
    }

    public void Set(
        MissAnalysisEntry? entry,
        bool showClickMarker,
        bool recolourNote,
        bool showNoteIndicator,
        Colour4 colour)
    {
        restoreHitObjectColour();
        selectedEntry = entry;
        selectedColour = colour;
        recolourSelectedNote = recolourNote;
        ClearInternal();
        if (entry == null)
            return;

        if (showNoteIndicator)
            AddInternal(createTargetIndicator(entry, colour));

        if (!showClickMarker || entry.InputFrame is not { } input)
            return;

        var markers = new ClickMarkerContainer();
        AddInternal(markers);
        markers.Add(new AnalysisFrameEntry(
            input.Time,
            display_length,
            input.Position,
            OsuAction.LeftButton));
    }

    protected override void Update()
    {
        base.Update();

        if (selectedEntry == null || !recolourSelectedNote)
            return;

        DrawableOsuHitObject[] alive = allAliveHitObjects().ToArray();
        if (highlightedDrawable != null)
        {
            if (ReferenceEquals(highlightedDrawable.HitObject, highlightedHitObject) && alive.Contains(highlightedDrawable))
                return;
            restoreHitObjectColour();
        }

        DrawableOsuHitObject? drawable = alive
            .Where(candidate => Math.Abs(candidate.HitObject.StartTime - selectedEntry.TargetStartTime) <= time_match_tolerance)
            .OrderBy(candidate => matchesObjectType(candidate, selectedEntry.ObjectType) ? 0 : 1)
            .ThenBy(candidate => (candidate.HitObject.StackedPosition - selectedEntry.TargetPosition).LengthSquared)
            .FirstOrDefault();

        if (drawable == null)
            return;

        highlightedDrawable = drawable;
        highlightedHitObject = drawable.HitObject;
        originalColour = drawable.AccentColour.Value;
        drawable.OnUpdate += applyHitObjectColour;
        applyHitObjectColour(drawable);
    }

    private IEnumerable<DrawableOsuHitObject> allAliveHitObjects()
        => playfield.HitObjectContainer.AliveObjects.SelectMany(flatten).OfType<DrawableOsuHitObject>();

    private static IEnumerable<DrawableHitObject> flatten(DrawableHitObject drawable)
    {
        yield return drawable;
        foreach (DrawableHitObject nested in drawable.NestedHitObjects)
        foreach (DrawableHitObject descendant in flatten(nested))
            yield return descendant;
    }

    private static bool matchesObjectType(DrawableOsuHitObject drawable, string objectType) => objectType switch
    {
        "Slider head" => drawable is DrawableSliderHead,
        "Slider tick" => drawable is DrawableSliderTick,
        "Slider repeat" => drawable is DrawableSliderRepeat,
        "Slider tail" => drawable is DrawableSliderTail,
        "Slider" => drawable is DrawableSlider,
        "Circle" => drawable is DrawableHitCircle
                    && drawable is not DrawableSliderHead
                    && drawable is not DrawableSliderTail,
        "Spinner" => drawable is DrawableSpinner,
        _ => false,
    };

    private void applyHitObjectColour(Drawable drawable)
    {
        if (highlightedDrawable != null && ReferenceEquals(highlightedDrawable.HitObject, highlightedHitObject))
            highlightedDrawable.AccentColour.Value = selectedColour;
    }

    private void restoreHitObjectColour()
    {
        if (highlightedDrawable == null)
            return;

        highlightedDrawable.OnUpdate -= applyHitObjectColour;
        if (ReferenceEquals(highlightedDrawable.HitObject, highlightedHitObject))
            highlightedDrawable.AccentColour.Value = originalColour;
        highlightedDrawable = null;
        highlightedHitObject = null;
    }

    private static Drawable createTargetIndicator(MissAnalysisEntry entry, Colour4 colour)
    {
        return new Container
        {
            Position = entry.TargetPosition,
            Origin = Anchor.Centre,
            Size = new Vector2(10),
            Rotation = 45,
            Children =
            [
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Black.Opacity(0.8f),
                },
                new Box
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(6),
                    Colour = colour,
                },
            ],
        };
    }

    protected override void Dispose(bool isDisposing)
    {
        restoreHitObjectColour();
        base.Dispose(isDisposing);
    }
}
