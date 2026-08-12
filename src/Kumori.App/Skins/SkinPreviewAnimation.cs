using System.IO;
using System.Windows;
using Point = System.Windows.Point;

namespace Kumori.App.Skins;

internal enum SkinPreviewAnimationRole
{
    None,
    ApproachCircle,
    HitCircle,
    Followpoint,
    SliderBall,
    SliderFollowCircle,
    ReverseArrow,
    CursorTrail,
    CursorMiddle,
    Cursor,
    SpinnerCircle,
    SpinnerGlow,
    SpinnerBottom,
    SpinnerTop,
    SpinnerMiddle2,
    SpinnerMiddle,
    SpinnerApproach,
    SpinnerMetre,
    SpinnerSpin,
    SpinnerClear,
    ScorebarMarker,
}

internal readonly record struct SkinPreviewCursorState(
    Point Position,
    double Scale,
    double Rotation);

internal readonly record struct SkinPreviewApproachState(
    double Scale,
    double Opacity);

internal readonly record struct SkinPreviewHitObjectState(
    double Scale,
    double Opacity);

internal readonly record struct SkinPreviewFollowpointState(
    double Scale,
    double Opacity,
    double TravelProgress,
    double AnimationTime);

internal readonly record struct SkinPreviewSliderState(
    double Progress,
    bool Reversed,
    double BallOpacity,
    double FollowScale,
    double FollowOpacity,
    double ReverseScale,
    double ReverseOpacity,
    double ReverseRotation);

internal readonly record struct SkinPreviewSpinnerState(
    double Progress,
    double Rotation,
    double ApproachScale,
    double GlowOpacity,
    double BodyScale,
    double BodyOpacity,
    double MetreFill,
    double SpinOpacity,
    double ClearScale,
    double ClearOpacity);

/// <summary>
/// Pure evaluators for the synthetic Skin Studio showcase. Transform values are
/// ported from ppy/osu commit 5da71008b082d1a77e4bb301dc98886f1f24b895
/// (legacy cursor, slider, follow-circle, hit-circle, spinner and HUD drawables).
/// The object positions are synthetic; their timing and transforms follow lazer.
/// </summary>
internal static class SkinPreviewAnimation
{
    public const double LoopMilliseconds = 6000;
    public const double CursorRevolutionMilliseconds = 10000;
    public const double DisjointTrailFadeMilliseconds = 150;
    public const double SmoothTrailFadeMilliseconds = 500;
    public const double DisjointTrailIntervalMilliseconds = 1000d / 60;
    public const double LegacyTrailTextureScale = 1 / 1.6;
    public const double ObjectPreemptMilliseconds = 1200;
    public const double ObjectFadeInMilliseconds = 400;
    public const double SliderStartMilliseconds = ObjectPreemptMilliseconds;
    public const double SliderSpanMilliseconds = 1600;
    public const double SliderEndMilliseconds =
        SliderStartMilliseconds + SliderSpanMilliseconds * 2;
    public const double SpinnerStartMilliseconds = ObjectPreemptMilliseconds;
    public const double SpinnerDurationMilliseconds = 3200;
    public const int SpinnerRequiredSpins = 8;

    private const double cursor_pressed_scale = 1.3;
    private const double cursor_scale_duration = 100;
    private const double auto_spinner_radians_per_millisecond = 0.05;

    public static bool ShouldRender(
        bool visible,
        bool elementsWorkspace,
        bool autoplayEnabled,
        bool interactiveCursorActive) =>
        visible
        && elementsWorkspace
        && (autoplayEnabled || interactiveCursorActive);

    public static bool ShouldRenderExtras(
        bool visible,
        bool previewVisible,
        bool autoplayEnabled) =>
        visible && previewVisible && autoplayEnabled;

    public static bool CanActivateInteractiveCursor(
        bool assetCanvas,
        bool cursorComposition,
        bool visibleCursorHead) =>
        assetCanvas && cursorComposition && visibleCursorHead;

    public static double LoopTime(double elapsedMilliseconds) =>
        PositiveModulo(elapsedMilliseconds, LoopMilliseconds);

    public static SkinPreviewCursorState Cursor(
        double elapsedMilliseconds,
        double width,
        double height,
        bool expand,
        bool rotate)
    {
        var loop = LoopTime(elapsedMilliseconds);
        var position = AutoCursorPosition(loop, width, height);
        var scale = expand ? AutoCursorScale(loop) : 1;
        var rotation = rotate
            ? PositiveModulo(
                  elapsedMilliseconds,
                  CursorRevolutionMilliseconds)
              / CursorRevolutionMilliseconds * 360
            : 0;
        return new SkinPreviewCursorState(position, scale, rotation);
    }

    public static double CursorTransitionScale(
        double from,
        double to,
        double elapsedMilliseconds) =>
        Lerp(
            from,
            to,
            EaseOut(elapsedMilliseconds / cursor_scale_duration));

    public static SkinPreviewApproachState Approach(
        double elapsedMilliseconds)
    {
        var loop = LoopTime(elapsedMilliseconds);
        if (loop < SliderStartMilliseconds)
        {
            var progress = loop / ObjectPreemptMilliseconds;
            var fadeDuration = Math.Min(
                ObjectFadeInMilliseconds * 2,
                ObjectPreemptMilliseconds);
            return new SkinPreviewApproachState(
                Lerp(4, 1, progress),
                0.9 * Math.Clamp(loop / fadeDuration, 0, 1));
        }

        var fadeOut = Math.Clamp(
            (loop - SliderStartMilliseconds) / 50,
            0,
            1);
        return new SkinPreviewApproachState(1, 0.9 * (1 - fadeOut));
    }

    public static SkinPreviewHitObjectState HitObject(
        double elapsedMilliseconds,
        bool shortNumberFade = false)
    {
        var loop = LoopTime(elapsedMilliseconds);
        if (loop < SliderStartMilliseconds)
        {
            return new SkinPreviewHitObjectState(
                1,
                Math.Clamp(loop / ObjectFadeInMilliseconds, 0, 1));
        }

        const double legacyFadeDuration = 240;
        var hitElapsed = loop - SliderStartMilliseconds;
        var duration = shortNumberFade
            ? legacyFadeDuration / 4
            : legacyFadeDuration;
        var opacity = 1 - Math.Clamp(hitElapsed / duration, 0, 1);
        var scale = shortNumberFade
            ? 1
            : Lerp(
                1,
                1.4,
                EaseOut(hitElapsed / legacyFadeDuration));
        return new SkinPreviewHitObjectState(scale, opacity);
    }

    public static SkinPreviewFollowpointState Followpoint(
        double elapsedMilliseconds,
        double fraction)
    {
        var loop = LoopTime(elapsedMilliseconds);
        fraction = Math.Clamp(fraction, 0, 1);
        const double startObjectEnd = SliderStartMilliseconds;
        const double nextObjectStart = SliderEndMilliseconds;
        const double followpointPreempt = 800;
        var fadeOutTime = startObjectEnd
                          + fraction
                          * (nextObjectStart - startObjectEnd);
        var fadeInTime = fadeOutTime - followpointPreempt;
        var fadeInProgress = Math.Clamp(
            (loop - fadeInTime) / ObjectFadeInMilliseconds,
            0,
            1);
        var fadeOutProgress = Math.Clamp(
            (loop - fadeOutTime) / ObjectFadeInMilliseconds,
            0,
            1);
        return new SkinPreviewFollowpointState(
            Lerp(1.5, 1, EaseOut(fadeInProgress)),
            fadeInProgress * (1 - fadeOutProgress),
            EaseOut(fadeInProgress),
            loop - fadeInTime);
    }

    public static SkinPreviewSliderState Slider(
        double elapsedMilliseconds,
        bool legacyVersionOne = false)
    {
        var loop = LoopTime(elapsedMilliseconds);
        var repeatTime = SliderStartMilliseconds + SliderSpanMilliseconds;
        var reversed = loop >= repeatTime;
        double curveProgress;
        if (loop < SliderStartMilliseconds)
            curveProgress = 0;
        else if (loop < repeatTime)
            curveProgress = (loop - SliderStartMilliseconds)
                            / SliderSpanMilliseconds;
        else if (loop < SliderEndMilliseconds)
            curveProgress = 1 - (loop - repeatTime)
                            / SliderSpanMilliseconds;
        else
            curveProgress = 0;

        var ballOpacity =
            loop >= SliderStartMilliseconds && loop < SliderEndMilliseconds
                ? 1
                : 0;

        double followScale;
        double followOpacity;
        if (loop < SliderStartMilliseconds)
        {
            followScale = 1;
            followOpacity = 0;
        }
        else if (loop < repeatTime)
        {
            var sincePress = loop - SliderStartMilliseconds;
            followScale = Lerp(1, 2, EaseOut(sincePress / 180));
            followOpacity = Math.Clamp(sincePress / 60, 0, 1);
        }
        else if (loop < SliderEndMilliseconds)
        {
            var sinceRepeat = loop - repeatTime;
            followScale = sinceRepeat < 200
                ? Lerp(2.2, 2, sinceRepeat / 200)
                : 2;
            followOpacity = 1;
        }
        else
        {
            var sinceEnd = loop - SliderEndMilliseconds;
            followScale = Lerp(2, 1.6, EaseOut(sinceEnd / 200));
            followOpacity = 1 - EaseIn(sinceEnd / 200);
        }

        var reverseElapsed = loop - repeatTime;
        double reverseScale;
        double reverseOpacity;
        double reverseRotation;
        if (reverseElapsed >= 0)
        {
            var hitProgress = Math.Clamp(
                reverseElapsed / Math.Min(300, SliderSpanMilliseconds),
                0,
                1);
            reverseScale = Lerp(1, 1.4, EaseOut(hitProgress));
            reverseOpacity = 1 - EaseOut(hitProgress);
            reverseRotation = 0;
        }
        else
        {
            var pulseTime = PositiveModulo(loop, 300);
            var pulseProgress = pulseTime / 300;
            reverseScale = legacyVersionOne
                ? Lerp(1.3, 1, pulseProgress)
                : Lerp(1.3, 1, EaseOut(pulseProgress));
            reverseOpacity = Math.Clamp(loop / 150, 0, 1);
            reverseRotation = legacyVersionOne
                ? Lerp(5.625, -5.625, pulseProgress)
                : 0;
        }

        return new SkinPreviewSliderState(
            Math.Clamp(curveProgress, 0, 1),
            reversed,
            ballOpacity,
            followScale,
            Math.Clamp(followOpacity, 0, 1),
            reverseScale,
            Math.Clamp(reverseOpacity, 0, 1),
            reverseRotation);
    }

    public static SkinPreviewSpinnerState Spinner(
        double elapsedMilliseconds,
        bool noBlink = false)
    {
        var loop = LoopTime(elapsedMilliseconds);
        var activeTime = Math.Clamp(
            loop - SpinnerStartMilliseconds,
            0,
            SpinnerDurationMilliseconds);
        var rotation = activeTime
                       * auto_spinner_radians_per_millisecond
                       * 180
                       / Math.PI;
        var progress = Math.Clamp(
            rotation / 360 / SpinnerRequiredSpins,
            0,
            1);
        var approachProgress = Math.Clamp(
            activeTime / SpinnerDurationMilliseconds,
            0,
            1);
        var approachScale = Lerp(
            1,
            0.1 / 1.86,
            approachProgress);
        var bodyScale = 0.8 + EaseOut(progress) * 0.2;

        var fadeInStart = SpinnerStartMilliseconds
                          - ObjectFadeInMilliseconds;
        double bodyOpacity;
        if (loop < SpinnerStartMilliseconds)
        {
            bodyOpacity = Math.Clamp(
                (loop - fadeInStart) / ObjectFadeInMilliseconds,
                0,
                1);
        }
        else
        {
            bodyOpacity = 1 - Math.Clamp(
                (loop
                 - (SpinnerStartMilliseconds
                    + SpinnerDurationMilliseconds))
                / 240,
                0,
                1);
        }

        var spinFadeInStart = SpinnerStartMilliseconds
                              - ObjectFadeInMilliseconds / 2;
        var spinOpacity = loop < SpinnerStartMilliseconds
            ? Math.Clamp(
                (loop - spinFadeInStart)
                / (ObjectFadeInMilliseconds / 2),
                0,
                1)
            : 1;
        var spinnerEnd = SpinnerStartMilliseconds
                         + SpinnerDurationMilliseconds;
        var spinFadeOutStart = spinnerEnd
                               - Math.Min(
                                   400,
                                   SpinnerDurationMilliseconds);
        if (loop >= spinFadeOutStart)
        {
            spinOpacity *= 1 - Math.Clamp(
                (loop - spinFadeOutStart)
                / Math.Min(400, SpinnerDurationMilliseconds),
                0,
                1);
        }

        var degreesPerMillisecond =
            auto_spinner_radians_per_millisecond * 180 / Math.PI;
        var completionTime = SpinnerStartMilliseconds
                             + SpinnerRequiredSpins
                             * 360
                             / degreesPerMillisecond;
        var clearElapsed = loop - completionTime;
        var clearOpacity = clearElapsed < 0
            ? 0
            : EaseOut(clearElapsed / 400);
        var clearFadeOutStart = spinnerEnd - 50;
        if (loop >= clearFadeOutStart)
        {
            clearOpacity *= 1 - Math.Clamp(
                (loop - clearFadeOutStart) / 50,
                0,
                1);
        }

        double clearScale;
        if (clearElapsed < 0)
            clearScale = 1;
        else if (clearElapsed < 240)
            clearScale = Lerp(2, 0.8, EaseOut(clearElapsed / 240));
        else
            clearScale = Lerp(0.8, 1, (clearElapsed - 240) / 160);

        return new SkinPreviewSpinnerState(
            progress,
            rotation,
            approachScale,
            progress,
            bodyScale,
            Math.Clamp(bodyOpacity, 0, 1),
            SpinnerMetreFill(progress, noBlink, elapsedMilliseconds),
            Math.Clamp(spinOpacity, 0, 1),
            clearScale,
            Math.Clamp(clearOpacity, 0, 1));
    }

    public static double SpinnerMetreFill(
        double progress,
        bool noBlink,
        double elapsedMilliseconds)
    {
        var percentage = Math.Clamp(progress, 0, 1) * 100;
        if (!noBlink)
            percentage = Math.Min(99, percentage);
        var bars = (int)percentage / 10;
        if (!noBlink)
        {
            var probability = ((int)percentage % 10) / 10d;
            var frame = (int)Math.Floor(
                elapsedMilliseconds / DisjointTrailIntervalMilliseconds);
            var sample = PositiveModulo(
                Math.Sin(frame * 12.9898 + 78.233) * 43758.5453,
                1);
            if (sample < probability)
                bars++;
        }
        return Math.Clamp(bars / 10d, 0, 1);
    }

    public static double HealthTarget(double elapsedMilliseconds)
    {
        var loop = LoopTime(elapsedMilliseconds);
        if (loop < 1200)
            return 1;
        if (loop < 2800)
            return Lerp(1, 0.42, (loop - 1200) / 1600);
        if (loop < 4400)
            return Lerp(0.42, 0.84, (loop - 2800) / 1600);
        return Lerp(0.84, 1, (loop - 4400) / 1600);
    }

    public static double SmoothHealth(
        double current,
        double target,
        double elapsedFrameMilliseconds)
    {
        var amount = EaseOutQuint(
            Math.Clamp(elapsedFrameMilliseconds, 0, 200) / 200);
        return Lerp(current, target, amount);
    }

    public static double ScorebarOffsetFromHealth(double health) =>
        -72 * (1 - Math.Clamp(health, 0, 1));

    public static double TrailOpacity(
        double ageMilliseconds,
        bool smooth) =>
        Math.Clamp(
            1 - ageMilliseconds / (smooth
                ? SmoothTrailFadeMilliseconds
                : DisjointTrailFadeMilliseconds),
            0,
            1);

    public static double TrailInterval(
        double textureDisplayWidth,
        double cursorScale = 1,
        double cursorSize = 1) =>
        textureDisplayWidth
        * cursorScale
        / 2.5
        / Math.Max(cursorSize, 1);

    public static IReadOnlyList<Point> SmoothTrailParts(
        Point lastPosition,
        Point currentPosition,
        double interval)
    {
        if (interval <= 0)
            return [];
        var difference = currentPosition - lastPosition;
        var distance = difference.Length;
        if (distance <= double.Epsilon)
            return [];
        var direction = difference / distance;
        var stopAt = distance - interval;
        var result = new List<Point>();
        for (var travelled = interval;
             travelled < stopAt;
             travelled += interval)
        {
            result.Add(lastPosition + direction * travelled);
        }
        return result;
    }

    public static Point SamplePolyline(
        IReadOnlyList<Point> points,
        double progress)
    {
        if (points.Count == 0)
            return default;
        if (points.Count == 1)
            return points[0];

        progress = Math.Clamp(progress, 0, 1);
        var totalLength = PolylineLength(points);
        if (totalLength <= double.Epsilon)
            return points[0];

        var target = totalLength * progress;
        var traversed = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var segment = points[index] - points[index - 1];
            var length = segment.Length;
            if (traversed + length >= target)
            {
                var local = length <= double.Epsilon
                    ? 0
                    : (target - traversed) / length;
                return points[index - 1] + segment * local;
            }
            traversed += length;
        }
        return points[^1];
    }

    public static double PolylineLength(IReadOnlyList<Point> points)
    {
        var totalLength = 0d;
        for (var index = 1; index < points.Count; index++)
            totalLength += (points[index] - points[index - 1]).Length;
        return totalLength;
    }

    public static double PolylineRotation(
        IReadOnlyList<Point> points,
        double progress,
        bool reversed)
    {
        const double delta = 0.0002;
        var before = SamplePolyline(points, Math.Max(0, progress - delta));
        var after = SamplePolyline(points, Math.Min(1, progress + delta));
        var direction = reversed ? before - after : after - before;
        return direction.Length <= double.Epsilon
            ? 0
            : Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
    }

    public static IReadOnlyList<string> ResolveFrames(
        IEnumerable<string> filenames,
        string baseStem,
        string separator)
    {
        var available = filenames
            .Where(filename => !filename.Replace('\\', '/').Trim('/').Contains('/'))
            .ToArray();
        var result = new List<string>();
        for (var index = 0; index < 512; index++)
        {
            var expected = $"{baseStem}{separator}{index}";
            var frame = available
                .Where(filename => LogicalStem(filename).Equals(
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(IsHighResolution)
                .ThenBy(filename => filename, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (frame is null)
                break;
            result.Add(frame);
        }

        if (result.Count > 0)
            return result;
        var fallback = available
            .Where(filename => LogicalStem(filename).Equals(
                baseStem,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(IsHighResolution)
            .ThenBy(filename => filename, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return fallback is null ? [] : [fallback];
    }

    public static double SliderBallFrameLength(double velocity) =>
        double.IsFinite(velocity) && velocity > 0
            ? Math.Max(
                0.15 / velocity * DisjointTrailIntervalMilliseconds,
                DisjointTrailIntervalMilliseconds)
            : DisjointTrailIntervalMilliseconds;

    public static int FrameIndex(
        double elapsedMilliseconds,
        int frameCount,
        int configuredFramerate,
        bool sliderBall,
        double sliderVelocity = double.PositiveInfinity)
    {
        if (frameCount <= 1)
            return 0;
        var frameLength = sliderBall
            ? SliderBallFrameLength(sliderVelocity)
            : configuredFramerate > 0
                ? 1000d / configuredFramerate
                : 1000d / frameCount;
        return (int)Math.Floor(
            PositiveModulo(elapsedMilliseconds, frameLength * frameCount)
            / frameLength);
    }

    private static Point AutoCursorPosition(
        double loop,
        double width,
        double height)
    {
        ReadOnlySpan<(double Time, double X, double Y)> frames =
        [
            (0, 0.18, 0.68),
            (1100, 0.34, 0.34),
            (2100, 0.77, 0.28),
            (3300, 0.66, 0.72),
            (4500, 0.28, 0.64),
            (6000, 0.18, 0.68),
        ];
        for (var index = 1; index < frames.Length; index++)
        {
            if (loop > frames[index].Time)
                continue;
            var previous = frames[index - 1];
            var next = frames[index];
            var progress = EaseOut(
                (loop - previous.Time) / (next.Time - previous.Time));
            return new Point(
                width * Lerp(previous.X, next.X, progress),
                height * Lerp(previous.Y, next.Y, progress));
        }
        return new Point(width * frames[^1].X, height * frames[^1].Y);
    }

    private static double AutoCursorScale(double loop)
    {
        ReadOnlySpan<double> presses = [1200, 4400];
        foreach (var press in presses)
        {
            const double heldDuration = 120;
            if (loop < press || loop >= press + heldDuration + 100)
                continue;
            if (loop < press + 100)
            {
                return Lerp(
                    1,
                    cursor_pressed_scale,
                    EaseOut((loop - press) / 100));
            }
            if (loop < press + heldDuration)
                return cursor_pressed_scale;
            return Lerp(
                cursor_pressed_scale,
                1,
                EaseOut((loop - press - heldDuration) / 100));
        }
        return 1;
    }

    private static double EaseIn(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value;
    }

    private static double EaseOut(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * (2 - value);
    }

    private static double EaseOutQuint(double value)
    {
        value = Math.Clamp(value, 0, 1);
        var inverse = 1 - value;
        return 1 - inverse * inverse * inverse * inverse * inverse;
    }

    private static double Lerp(double from, double to, double amount) =>
        from + (to - from) * Math.Clamp(amount, 0, 1);

    private static double PositiveModulo(double value, double divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static string LogicalStem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        return stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)
            ? stem[..^3]
            : stem;
    }

    private static bool IsHighResolution(string filename) =>
        Path.GetFileNameWithoutExtension(filename)
            .EndsWith("@2x", StringComparison.OrdinalIgnoreCase);
}
