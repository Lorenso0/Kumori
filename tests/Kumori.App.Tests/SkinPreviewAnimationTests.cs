using Kumori.App.Skins;
using Kumori.Core.Settings;
using System.Text.Json;
using Xunit;
using Point = System.Windows.Point;

namespace Kumori.App.Tests;

public sealed class SkinPreviewAnimationTests
{
    [Fact]
    public void Cursor_uses_osu_rotation_and_click_expansion_timing()
    {
        var idle = SkinPreviewAnimation.Cursor(
            0,
            640,
            480,
            expand: true,
            rotate: true);
        var clicked = SkinPreviewAnimation.Cursor(
            1300,
            640,
            480,
            expand: true,
            rotate: true);
        var halfTurn = SkinPreviewAnimation.Cursor(
            5000,
            640,
            480,
            expand: false,
            rotate: true);

        Assert.Equal(1, idle.Scale, precision: 6);
        Assert.Equal(1.3, clicked.Scale, precision: 6);
        Assert.Equal(180, halfTurn.Rotation, precision: 6);
        Assert.InRange(clicked.Position.X, 0, 640);
        Assert.InRange(clicked.Position.Y, 0, 480);
    }

    [Fact]
    public void Cursor_expansion_uses_the_legacy_100ms_out_easing()
    {
        var expanding = SkinPreviewAnimation.Cursor(
            1250,
            640,
            480,
            expand: true,
            rotate: false);
        var contracting = SkinPreviewAnimation.Cursor(
            1370,
            640,
            480,
            expand: true,
            rotate: false);

        Assert.Equal(1.225, expanding.Scale, precision: 6);
        Assert.Equal(1.075, contracting.Scale, precision: 6);
        Assert.Equal(
            1.225,
            SkinPreviewAnimation.CursorTransitionScale(1, 1.3, 50),
            precision: 6);
    }

    [Fact]
    public void Slider_uses_linear_curve_progress_and_exact_repeat_boundaries()
    {
        var start = SkinPreviewAnimation.Slider(1200);
        var forwardHalf = SkinPreviewAnimation.Slider(2000);
        var repeat = SkinPreviewAnimation.Slider(2800);
        var reverseHalf = SkinPreviewAnimation.Slider(3600);
        var end = SkinPreviewAnimation.Slider(4400);

        Assert.Equal(0, start.Progress, precision: 6);
        Assert.False(start.Reversed);
        Assert.Equal(1, start.BallOpacity, precision: 6);
        Assert.Equal(0.5, forwardHalf.Progress, precision: 6);
        Assert.Equal(1, repeat.Progress, precision: 6);
        Assert.True(repeat.Reversed);
        Assert.Equal(2.2, repeat.FollowScale, precision: 6);
        Assert.Equal(1, repeat.ReverseScale, precision: 6);
        Assert.Equal(0.5, reverseHalf.Progress, precision: 6);
        Assert.Equal(0, end.Progress, precision: 6);
        Assert.True(end.Reversed);
        Assert.Equal(0, end.BallOpacity, precision: 6);
    }

    [Fact]
    public void Slider_follow_circle_uses_lazer_press_tick_and_end_transforms()
    {
        var pressedHalfway = SkinPreviewAnimation.Slider(1290);
        var repeat = SkinPreviewAnimation.Slider(2800);
        var ending = SkinPreviewAnimation.Slider(4500);

        Assert.Equal(1.75, pressedHalfway.FollowScale, precision: 6);
        Assert.Equal(1, pressedHalfway.FollowOpacity, precision: 6);
        Assert.Equal(2.2, repeat.FollowScale, precision: 6);
        Assert.Equal(1.7, ending.FollowScale, precision: 6);
        Assert.Equal(0.75, ending.FollowOpacity, precision: 6);
    }

    [Fact]
    public void Reverse_arrow_uses_legacy_300ms_pulse_and_hit_fade()
    {
        var pulseStart = SkinPreviewAnimation.Slider(
            1500,
            legacyVersionOne: true);
        var pulseMiddle = SkinPreviewAnimation.Slider(
            1650,
            legacyVersionOne: false);
        var hitMiddle = SkinPreviewAnimation.Slider(2950);

        Assert.Equal(1.3, pulseStart.ReverseScale, precision: 6);
        Assert.Equal(5.625, pulseStart.ReverseRotation, precision: 6);
        Assert.Equal(1.075, pulseMiddle.ReverseScale, precision: 6);
        Assert.Equal(1.3, hitMiddle.ReverseScale, precision: 6);
        Assert.Equal(0.25, hitMiddle.ReverseOpacity, precision: 6);
    }

    [Fact]
    public void Approach_and_hit_circle_match_ar5_legacy_transforms()
    {
        var approachStart = SkinPreviewAnimation.Approach(0);
        var approachVisible = SkinPreviewAnimation.Approach(800);
        var approachHit = SkinPreviewAnimation.Approach(1225);
        var hitHalfway = SkinPreviewAnimation.HitObject(1320);
        var numberDone = SkinPreviewAnimation.HitObject(
            1260,
            shortNumberFade: true);

        Assert.Equal(4, approachStart.Scale, precision: 6);
        Assert.Equal(0, approachStart.Opacity, precision: 6);
        Assert.Equal(2, approachVisible.Scale, precision: 6);
        Assert.Equal(0.9, approachVisible.Opacity, precision: 6);
        Assert.Equal(0.45, approachHit.Opacity, precision: 6);
        Assert.Equal(1.3, hitHalfway.Scale, precision: 6);
        Assert.Equal(0.5, hitHalfway.Opacity, precision: 6);
        Assert.Equal(1, numberDone.Scale, precision: 6);
        Assert.Equal(0, numberDone.Opacity, precision: 6);
    }

    [Fact]
    public void Extras_hitcircle_restarts_as_soon_as_its_hit_fade_finishes()
    {
        Assert.Equal(
            0,
            SkinPreviewAnimation.ExtrasTime(
                "osu.hitcircles",
                SkinPreviewAnimation.HitCircleLoopMilliseconds),
            precision: 6);
        Assert.Equal(
            100,
            SkinPreviewAnimation.ExtrasTime(
                "osu.hitcircles",
                SkinPreviewAnimation.HitCircleLoopMilliseconds + 100),
            precision: 6);
        Assert.Equal(
            5000,
            SkinPreviewAnimation.ExtrasTime("osu.cursor", 5000),
            precision: 6);
    }

    [Theory]
    [InlineData("osu.hitcircles", false, true)]
    [InlineData("osu.hitcircles", true, false)]
    [InlineData("osu.slider", false, false)]
    public void Paused_extras_use_a_static_frame_only_for_hitcircles(
        string familyId,
        bool animationsEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinExtrasPickerWindow.UsesStaticHitCirclePreview(
                familyId,
                animationsEnabled));
    }

    [Fact]
    public void Followpoints_use_source_fade_move_and_scale_timing()
    {
        var start = SkinPreviewAnimation.Followpoint(2000, 0.5);
        var halfway = SkinPreviewAnimation.Followpoint(2200, 0.5);
        var fadeOut = SkinPreviewAnimation.Followpoint(3000, 0.5);

        Assert.Equal(1.5, start.Scale, precision: 6);
        Assert.Equal(0, start.Opacity, precision: 6);
        Assert.Equal(1.125, halfway.Scale, precision: 6);
        Assert.Equal(0.5, halfway.Opacity, precision: 6);
        Assert.Equal(0.75, halfway.TravelProgress, precision: 6);
        Assert.Equal(0.5, fadeOut.Opacity, precision: 6);
    }

    [Fact]
    public void Spinner_uses_lazer_auto_rotation_completion_and_clear_timing()
    {
        var early = SkinPreviewAnimation.Spinner(1600, noBlink: true);
        var complete = SkinPreviewAnimation.Spinner(2300, noBlink: true);
        var ending = SkinPreviewAnimation.Spinner(4375, noBlink: true);
        var fading = SkinPreviewAnimation.Spinner(4520, noBlink: true);

        Assert.Equal(400 * 0.05 * 180 / Math.PI, early.Rotation, precision: 6);
        Assert.Equal(
            early.Rotation / 360 / SkinPreviewAnimation.SpinnerRequiredSpins,
            early.Progress,
            precision: 6);
        Assert.Equal(1, complete.Progress, precision: 6);
        Assert.True(complete.ClearOpacity > 0);
        Assert.Equal(0.5, ending.ClearOpacity, precision: 2);
        Assert.Equal(0.5, fading.BodyOpacity, precision: 6);
        Assert.Equal(0, fading.SpinOpacity, precision: 6);
    }

    [Fact]
    public void Spinner_metre_is_quantized_to_the_ten_legacy_bars()
    {
        Assert.Equal(
            0.5,
            SkinPreviewAnimation.SpinnerMetreFill(
                0.56,
                noBlink: true,
                elapsedMilliseconds: 0),
            precision: 6);
        Assert.Equal(
            1,
            SkinPreviewAnimation.SpinnerMetreFill(
                1,
                noBlink: true,
                elapsedMilliseconds: 0),
            precision: 6);
    }

    [Fact]
    public void Trail_fades_match_disjoint_and_smooth_legacy_windows()
    {
        Assert.Equal(
            0.5,
            SkinPreviewAnimation.TrailOpacity(75, smooth: false),
            precision: 6);
        Assert.Equal(
            0.5,
            SkinPreviewAnimation.TrailOpacity(250, smooth: true),
            precision: 6);
        Assert.Equal(
            0,
            SkinPreviewAnimation.TrailOpacity(501, smooth: true),
            precision: 6);
    }

    [Fact]
    public void Polyline_sampling_uses_distance_and_honours_endpoints()
    {
        Point[] path =
        [
            new(0, 0),
            new(10, 0),
            new(10, 30),
        ];

        Assert.Equal(new Point(0, 0), SkinPreviewAnimation.SamplePolyline(path, 0));
        Assert.Equal(new Point(10, 10), SkinPreviewAnimation.SamplePolyline(path, 0.5));
        Assert.Equal(new Point(10, 30), SkinPreviewAnimation.SamplePolyline(path, 1));
    }

    [Fact]
    public void Smooth_trail_sampling_keeps_one_texture_interval_clear_of_cursor()
    {
        var parts = SkinPreviewAnimation.SmoothTrailParts(
            new Point(0, 0),
            new Point(35, 0),
            interval: 10);
        var tooClose = SkinPreviewAnimation.SmoothTrailParts(
            new Point(0, 0),
            new Point(20, 0),
            interval: 10);

        Assert.Equal([new Point(10, 0), new Point(20, 0)], parts);
        Assert.Empty(tooClose);
        Assert.Equal(
            20,
            SkinPreviewAnimation.TrailInterval(50),
            precision: 6);
    }

    [Fact]
    public void Frame_resolution_requires_contiguous_root_frames_and_prefers_two_x()
    {
        var frames = SkinPreviewAnimation.ResolveFrames(
        [
            "sliderb0.png",
            "sliderb0@2x.png",
            "sliderb1.png",
            "sliderb3.png",
            "folder/sliderb2.png",
        ],
            "sliderb",
            "");

        Assert.Equal(["sliderb0@2x.png", "sliderb1.png"], frames);
    }

    [Fact]
    public void Frame_resolution_falls_back_to_static_asset()
    {
        var frames = SkinPreviewAnimation.ResolveFrames(
            ["sliderfollowcircle.png", "sliderfollowcircle@2x.png"],
            "sliderfollowcircle",
            "-");

        Assert.Equal(["sliderfollowcircle@2x.png"], frames);
    }

    [Theory]
    [InlineData(260, 4, -1, false, 1, double.PositiveInfinity)]
    [InlineData(126, 4, 20, false, 2, double.PositiveInfinity)]
    [InlineData(51, 4, -1, true, 2, 0.1)]
    public void Frame_index_applies_configured_and_slider_ball_rates(
        double elapsed,
        int count,
        int framerate,
        bool sliderBall,
        int expected,
        double velocity)
    {
        Assert.Equal(
            expected,
            SkinPreviewAnimation.FrameIndex(
                elapsed,
                count,
                framerate,
                sliderBall,
                velocity));
    }

    [Fact]
    public void Health_display_uses_lazers_frame_time_clamp_and_out_quint()
    {
        Assert.Equal(
            0.40951,
            SkinPreviewAnimation.SmoothHealth(0, 1, 20),
            precision: 5);
        Assert.Equal(
            1,
            SkinPreviewAnimation.SmoothHealth(0, 1, 500),
            precision: 6);
        Assert.InRange(SkinPreviewAnimation.HealthTarget(3000), 0, 1);
        Assert.Equal(
            -36,
            SkinPreviewAnimation.ScorebarOffsetFromHealth(0.5),
            precision: 6);
    }

    [Fact]
    public void Preview_animations_default_to_enabled()
    {
        Assert.True(new KumoriSettings.SkinEditorSettings()
            .PreviewAnimationsEnabled);
    }

    [Fact]
    public void Preview_animation_preference_round_trips_in_settings_json()
    {
        var settings = new KumoriSettings();
        settings.SkinEditor.PreviewAnimationsEnabled = false;

        var reopened = JsonSerializer.Deserialize<KumoriSettings>(
            JsonSerializer.Serialize(settings));

        Assert.NotNull(reopened);
        Assert.False(reopened.SkinEditor.PreviewAnimationsEnabled);
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    public void Render_lifecycle_requires_a_visible_element_preview_or_interaction(
        bool visible,
        bool elements,
        bool autoplay,
        bool interactive,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinPreviewAnimation.ShouldRender(
                visible,
                elements,
                autoplay,
                interactive));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void Interactive_cursor_requires_the_asset_cursor_scene_and_visible_head(
        bool asset,
        bool cursorScene,
        bool visibleHead,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinPreviewAnimation.CanActivateInteractiveCursor(
                asset,
                cursorScene,
                visibleHead));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void Extras_render_lifecycle_stops_when_hidden_or_paused(
        bool visible,
        bool previewVisible,
        bool autoplay,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinPreviewAnimation.ShouldRenderExtras(
                visible,
                previewVisible,
                autoplay));
    }

    [Theory]
    [InlineData("cursor@2x.png", (int)SkinPreviewAnimationRole.Cursor)]
    [InlineData("cursortrail.png", (int)SkinPreviewAnimationRole.CursorTrail)]
    [InlineData("followpoint-3@2x.png", (int)SkinPreviewAnimationRole.Followpoint)]
    [InlineData("sliderb0.png", (int)SkinPreviewAnimationRole.SliderBall)]
    [InlineData("sliderfollowcircle.png", (int)SkinPreviewAnimationRole.SliderFollowCircle)]
    [InlineData("spinner-middle2.png", (int)SkinPreviewAnimationRole.SpinnerMiddle2)]
    [InlineData("scorebar-kidanger.png", (int)SkinPreviewAnimationRole.ScorebarMarker)]
    [InlineData("default-1.png", (int)SkinPreviewAnimationRole.None)]
    public void Extras_composition_assigns_animation_roles(
        string logicalKey,
        int expected)
    {
        Assert.Equal(
            expected,
            (int)SkinExtrasPickerWindow.ExtrasAnimationRole(logicalKey));
    }
}
