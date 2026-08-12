using System.Globalization;
using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioImageTransformOverlay : CompositeDrawable
{
    private readonly SpriteText title;
    private readonly SpriteText validation;
    private readonly OsuTextBox colour;
    private readonly OsuTextBox hue;
    private readonly OsuTextBox saturation;
    private readonly OsuTextBox lightness;
    private readonly OsuColourPicker colourPicker;
    private readonly SkinStudioSwatchStore swatchStore;
    private readonly StudioActionButton nextSwatchButton;
    private readonly StudioActionButton scopeButton;
    private readonly StudioActionButton frameButton;
    private IReadOnlyList<SkinStudioSwatch> swatches = [];
    private IReadOnlyList<int> animationFrames = [];
    private int nextSwatchIndex = -1;
    private int animationFrameIndex;
    private SkinImageTransformScope scope = SkinImageTransformScope.FullFamily;
    private Func<SkinImageTransform, SkinImageTransformScope, int?, bool>? commit;
    private bool updatingColour;

    public StudioImageTransformOverlay(string workspaceRoot)
    {
        swatchStore = new SkinStudioSwatchStore(workspaceRoot);
        RelativeSizeAxes = Axes.Both;
        Depth = -95;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.74f),
            },
            new Container
            {
                Width = 540,
                Height = 900,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Masking = true,
                CornerRadius = 12,
                Children =
                [
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.FromHex("#1B1925"),
                    },
                    new OsuScrollContainer(Direction.Vertical)
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding(28),
                        Child = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 10),
                            Children =
                            [
                                title = label("IMAGE TRANSFORM", 20, true),
                                label(
                                    "Choose a family, resolution, or exact animation-frame pair. Each transform is one undoable revision.",
                                    11,
                                    false),
                                scopeButton = new StudioActionButton(
                                    "Scope: full family",
                                    cycleScope),
                                frameButton = new StudioActionButton(
                                    "Animation frame: none",
                                    cycleFrame,
                                    enabled: false),
                                label("Colour (#RRGGBB)", 11, true),
                                colour = input("#FFFFFF"),
                                colourPicker = new OsuColourPicker
                                {
                                    RelativeSizeAxes = Axes.X,
                                },
                                new StudioActionButton("Save current swatch", saveSwatch),
                                nextSwatchButton = new StudioActionButton(
                                    "Use next saved swatch",
                                    useNextSwatch,
                                    enabled: false),
                                new StudioActionButton(
                                    "Colorize selection",
                                    () => apply(SkinImageTransformMode.Colorize)),
                                new StudioActionButton(
                                    "Luminance tint selection",
                                    () => apply(SkinImageTransformMode.Tint)),
                                new StudioActionButton(
                                    "Multiplicative tint selection",
                                    () => apply(SkinImageTransformMode.MultiplicativeTint)),
                                label("Hue shift (degrees)", 11, true),
                                hue = input("0"),
                                label("Saturation multiplier", 11, true),
                                saturation = input("1"),
                                label("Lightness multiplier", 11, true),
                                lightness = input("1"),
                                new StudioActionButton(
                                    "Apply HSL to selection",
                                    () => apply(SkinImageTransformMode.HueSaturationLightness),
                                    accent: true),
                                validation = label("", 11, false),
                                new StudioActionButton("Cancel", Hide),
                            ],
                        },
                    },
                ],
            },
        ];
        colourPicker.Current.BindValueChanged(change =>
        {
            if (updatingColour)
                return;
            updatingColour = true;
            colour.Current.Value = ToRgbHex(change.NewValue);
            updatingColour = false;
        });
        colour.Current.BindValueChanged(change =>
        {
            if (updatingColour
                || !TryParseHexColour(change.NewValue, out var parsed))
                return;
            updatingColour = true;
            colourPicker.Current.Value = new Colour4(parsed.Red, parsed.Green, parsed.Blue, byte.MaxValue);
            updatingColour = false;
        });
        Hide();
    }

    public void Present(
        string componentName,
        IReadOnlyList<int> animationFrames,
        Func<SkinImageTransform, SkinImageTransformScope, int?, bool> commit)
    {
        title.Text = $"TRANSFORM {componentName.ToUpperInvariant()}";
        validation.Text = "";
        this.commit = commit;
        this.animationFrames = animationFrames
            .Distinct()
            .Order()
            .ToArray();
        animationFrameIndex = 0;
        scope = SkinImageTransformScope.FullFamily;
        updateScopeLabel();
        swatches = swatchStore.List();
        nextSwatchIndex = -1;
        nextSwatchButton.SetEnabled(swatches.Count > 0);
        updateFrameLabel();
        Show();
    }

    private void cycleScope()
    {
        scope = scope switch
        {
            SkinImageTransformScope.FullFamily =>
                SkinImageTransformScope.PrimaryPair,
            SkinImageTransformScope.PrimaryPair =>
                SkinImageTransformScope.OneXVariants,
            SkinImageTransformScope.OneXVariants =>
                SkinImageTransformScope.TwoXVariants,
            SkinImageTransformScope.TwoXVariants when animationFrames.Count > 0 =>
                SkinImageTransformScope.AnimationFramePair,
            _ => SkinImageTransformScope.FullFamily,
        };
        updateScopeLabel();
    }

    private void updateScopeLabel()
    {
        scopeButton.SetText(scope switch
        {
            SkinImageTransformScope.FullFamily => "Scope: full family",
            SkinImageTransformScope.PrimaryPair => "Scope: primary 1x + 2x pair",
            SkinImageTransformScope.OneXVariants => "Scope: 1x variants only",
            SkinImageTransformScope.TwoXVariants => "Scope: @2x variants only",
            SkinImageTransformScope.AnimationFramePair => "Scope: exact animation-frame pair",
            _ => "Scope: full family",
        });
        frameButton.SetEnabled(
            scope == SkinImageTransformScope.AnimationFramePair
            && animationFrames.Count > 0);
        updateFrameLabel();
    }

    private void cycleFrame()
    {
        if (animationFrames.Count == 0)
            return;
        animationFrameIndex = (animationFrameIndex + 1) % animationFrames.Count;
        updateFrameLabel();
    }

    private void updateFrameLabel()
    {
        frameButton.SetText(animationFrames.Count == 0
            ? "Animation frame: none"
            : $"Animation frame: {animationFrames[animationFrameIndex]} ({animationFrameIndex + 1}/{animationFrames.Count})");
    }

    private void saveSwatch()
    {
        try
        {
            swatches = swatchStore.Add(colour.Current.Value);
            colour.Current.Value = swatches[0].Hex;
            nextSwatchIndex = 0;
            nextSwatchButton.SetEnabled(true);
            validation.Text = $"Saved {swatches[0].Hex} ({swatches.Count}/32 swatches).";
        }
        catch (Exception ex)
        {
            validation.Text = ex.Message;
        }
    }

    private void useNextSwatch()
    {
        if (swatches.Count == 0)
            return;
        nextSwatchIndex = (nextSwatchIndex + 1) % swatches.Count;
        colour.Current.Value = swatches[nextSwatchIndex].Hex;
        validation.Text =
            $"Using saved swatch {nextSwatchIndex + 1}/{swatches.Count}: {colour.Current.Value}.";
    }

    private void apply(SkinImageTransformMode mode)
    {
        if (!TryParseHexColour(colour.Current.Value, out var parsedColour))
        {
            validation.Text = "Colour must use #RRGGBB.";
            return;
        }
        if (!double.TryParse(
                hue.Current.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedHue)
            || !double.TryParse(
                saturation.Current.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedSaturation)
            || !double.TryParse(
                lightness.Current.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedLightness)
            || !double.IsFinite(parsedHue)
            || !double.IsFinite(parsedSaturation)
            || !double.IsFinite(parsedLightness)
            || parsedSaturation < 0
            || parsedLightness < 0)
        {
            validation.Text = "Hue and multipliers must be finite numbers; multipliers cannot be negative.";
            return;
        }

        var transform = new SkinImageTransform(
            mode,
            parsedColour,
            parsedHue,
            parsedSaturation,
            parsedLightness);
        int? frame = scope == SkinImageTransformScope.AnimationFramePair
            ? animationFrames.ElementAtOrDefault(animationFrameIndex)
            : null;
        if (commit?.Invoke(transform, scope, frame) == true)
            Hide();
    }

    internal static bool TryParseHexColour(string value, out SkinRgb colour)
    {
        colour = default;
        var normalized = value.Trim().TrimStart('#');
        if (normalized.Length != 6
            || !byte.TryParse(
                normalized[..2],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var red)
            || !byte.TryParse(
                normalized[2..4],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var green)
            || !byte.TryParse(
                normalized[4..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var blue))
        {
            return false;
        }
        colour = new SkinRgb(red, green, blue);
        return true;
    }

    internal static string ToRgbHex(Colour4 value)
    {
        static byte channel(float component) =>
            (byte)Math.Clamp(
                (int)Math.Round(
                    component * byte.MaxValue,
                    MidpointRounding.AwayFromZero),
                byte.MinValue,
                byte.MaxValue);

        return $"#{channel(value.R):X2}{channel(value.G):X2}{channel(value.B):X2}";
    }

    internal void SetAcceptanceColour(string value) =>
        colour.Current.Value = value;

    internal void SetAcceptancePickerColour(Colour4 value) =>
        colourPicker.Current.Value = value;

    internal string AcceptanceColour => colour.Current.Value;

    internal void SetAcceptanceHsl(
        string hueValue,
        string saturationValue,
        string lightnessValue)
    {
        hue.Current.Value = hueValue;
        saturation.Current.Value = saturationValue;
        lightness.Current.Value = lightnessValue;
    }

    internal void SaveAcceptanceSwatch() => saveSwatch();

    internal void CycleAcceptanceScope() => cycleScope();

    internal SkinImageTransformScope AcceptanceScope => scope;

    internal void ApplyAcceptanceTransform(SkinImageTransformMode mode) =>
        apply(mode);

    private static OsuTextBox input(string value)
    {
        var textBox = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = 36,
        };
        textBox.Current.Value = value;
        return textBox;
    }

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
