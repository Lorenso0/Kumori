using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Textures;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Skinning;
using osu.Game.Rulesets.Osu.UI.Cursor;
using osu.Game.Skinning;
using osuTK;

namespace Kumori.ReplayViewer;

/// <summary>
/// Renders captured movement with the active skin's cursor. The comparison
/// trail uses a neutral mask while inheriting the skin trail's rendered size,
/// opacity and blending, allowing an exact colour without baked-in RGB.
/// </summary>
internal partial class KumoriComparisonCursor : CompositeDrawable
{
    private const double maximumContinuousTrailStepMs = 100;
    private const float maximumTrailPartDiameter = 10;

    private readonly MovementSample[] samples;
    private readonly Func<double> currentTime;
    private readonly IBindable<Colour4> cursorColour;
    private readonly IBindable<Colour4> trailColour;
    private readonly OsuCursor cursor;
    private readonly Container trailContainer;
    private ReplayCursorTrail trail;
    private readonly SkinnableDrawable trailSkinSource;
    private Texture neutralTrailTexture = null!;
    private Vector2? lastTrailPosition;
    private double? lastTrailTime;

    public KumoriComparisonCursor(
        IReadOnlyList<MovementSample> movementSamples,
        Func<double> currentTime,
        IBindable<Colour4> cursorColour,
        IBindable<Colour4> trailColour)
    {
        this.currentTime = currentTime;
        this.cursorColour = cursorColour;
        this.trailColour = trailColour;
        samples = KumoriComparisonMovement.Prepare(movementSamples);

        RelativeSizeAxes = Axes.Both;
        InternalChildren =
        [
            trailContainer = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Child = trail = createTrail(),
            },
            // This invisible drawable tracks skin changes and exposes the exact
            // CursorTrail implementation selected by the active skin.
            trailSkinSource = new SkinnableDrawable(
                new OsuSkinComponentLookup(OsuSkinComponents.CursorTrail),
                _ => new FallbackCursorTrail(),
                confineMode: ConfineMode.NoScaling)
            {
                RelativeSizeAxes = Axes.Both,
                Alpha = 0,
                AlwaysPresent = true,
            },
            cursor = new OsuCursor
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Alpha = 0,
                Colour = cursorColour.Value,
            },
        ];
    }

    [BackgroundDependencyLoader]
    private void load(TextureStore textures)
    {
        // This texture is deliberately neutral white. It acts as an alpha
        // shape, so the configured comparison colour is never multiplied by
        // RGB baked into a custom skin's cursortrail image.
        neutralTrailTexture = textures.Get(@"Cursor/cursortrail");
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        trailSkinSource.OnSkinChanged += syncTrailTexture;
        cursor.CursorScale.BindValueChanged(_ => syncTrailTexture(), true);
        syncTrailTexture();
    }

    protected override void Update()
    {
        base.Update();
        cursor.Colour = visibleCursorColour(cursorColour.Value);
        // The trail texture is a neutral alpha mask, so use the configured
        // value exactly. Applying the cursor's anti-darkening transform here
        // made trail colour selection inaccurate.
        trail.Colour = trailColour.Value;

        double time = currentTime();
        if (!KumoriComparisonMovement.TryPositionAt(samples, time, out var position))
        {
            cursor.Alpha = 0;
            lastTrailPosition = null;
            lastTrailTime = null;
            return;
        }

        cursor.Position = position;
        cursor.Alpha = 1;

        if (lastTrailTime is { } previousTime
            && lastTrailPosition is { } previousPosition
            && time >= previousTime
            && time - previousTime <= maximumContinuousTrailStepMs)
        {
            // Native absolute replay input emits every actual position change,
            // including sub-pixel movement. Do not quantise slow movement.
            if ((position - previousPosition).LengthSquared > 0)
                trail.AddLocalSegment(previousPosition, position);
        }
        else
            resetTrail(position);

        lastTrailPosition = position;
        lastTrailTime = time;
    }

    private void syncTrailTexture()
    {
        if (trailSkinSource.Drawable is CursorTrail skinTrail)
        {
            trail.Texture = neutralTrailTexture;

            // The skin implementations apply important sizing and blending on
            // the CursorTrail drawable itself (Argon, for example, scales the
            // trail separately from the cursor). Applying that drawable scale
            // directly here would also scale replay coordinates around (0,0),
            // causing the huge loops seen in comparison mode. Fold it into the
            // individual trail-part size instead and cap pathological custom
            // textures to a thin, readable trail.
            Vector2 sourcePartScale = new(
                Math.Abs(skinTrail.Scale.X * skinTrail.NewPartScale.X),
                Math.Abs(skinTrail.Scale.Y * skinTrail.NewPartScale.Y));
            float cursorScale = Math.Max(0.01f, cursor.CursorScale.Value);
            float sourceWidth = skinTrail.Texture?.DisplayWidth ?? neutralTrailTexture.DisplayWidth;
            float sourceHeight = skinTrail.Texture?.DisplayHeight ?? neutralTrailTexture.DisplayHeight;
            float renderedWidth = sourceWidth * cursorScale * sourcePartScale.X;
            float renderedHeight = sourceHeight * cursorScale * sourcePartScale.Y;
            float largestDimension = Math.Max(renderedWidth, renderedHeight);
            if (largestDimension > maximumTrailPartDiameter)
            {
                float cap = maximumTrailPartDiameter / largestDimension;
                renderedWidth *= cap;
                renderedHeight *= cap;
            }

            trail.NewPartScale = new Vector2(
                renderedWidth / Math.Max(0.01f, neutralTrailTexture.DisplayWidth * cursorScale),
                renderedHeight / Math.Max(0.01f, neutralTrailTexture.DisplayHeight * cursorScale));
            trail.Blending = skinTrail.Blending;
            trail.Alpha = skinTrail.Alpha;
            trail.PartRotation = skinTrail.PartRotation;
        }
        trail.CursorScale = new Vector2(cursor.CursorScale.Value);
    }

    private ReplayCursorTrail createTrail() => new()
    {
        RelativeSizeAxes = Axes.Both,
        Colour = Colour4.White,
    };

    private static Colour4 visibleCursorColour(Colour4 colour)
    {
        var hsv = colour.ToHSV();
        // Capping saturation keeps every RGB channel bright enough for
        // multiplicative skin tinting while retaining the selected hue.
        return Colour4.FromHSV(hsv.X, Math.Min(hsv.Y, 0.55f), Math.Max(hsv.Z, 0.95f), colour.A);
    }

    private void resetTrail(Vector2 position)
    {
        trailContainer.Child = trail = createTrail();
        syncTrailTexture();
        trail.AddLocalPosition(position);
    }

    protected override void Dispose(bool isDisposing)
    {
        trailSkinSource.OnSkinChanged -= syncTrailTexture;
        base.Dispose(isDisposing);
    }

    private partial class ReplayCursorTrail : CursorTrail
    {
        // InputResampler is intended for the live input stream and can
        // overshoot sharp direction changes when driven manually by a second
        // synthetic replay, producing large fans and loops. The cursor itself
        // is already linearly interpolated at the current gameplay time, so
        // subdivide only the exact straight segment rendered this frame.
        protected override bool InterpolateMovements => false;

        public void AddLocalPosition(Vector2 position) => AddTrail(ToScreenSpace(position));

        public void AddLocalSegment(Vector2 from, Vector2 to)
        {
            float distance = (to - from).Length;
            float renderedPartWidth = Math.Max(1, Texture?.DisplayWidth * CursorScale.X * NewPartScale.X ?? 1);
            float spacing = Math.Max(1, renderedPartWidth / 2.5f);
            int steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));

            for (int step = 1; step <= steps; step++)
                AddLocalPosition(Vector2.Lerp(from, to, step / (float)steps));
        }
    }

    private partial class FallbackCursorTrail : CursorTrail
    {
        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            Texture = textures.Get(@"Cursor/cursortrail");
            Scale = new Vector2(1 / Texture.ScaleAdjust);
        }
    }
}
