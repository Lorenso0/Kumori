using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal sealed record StudioExtrasCompositionSummary(
    int FamilyCount,
    int ImageFiles,
    int AudioFiles,
    int IniSettings,
    IReadOnlyList<string> Areas,
    bool HasSkinIni,
    bool HasVisualFamily,
    bool ExportReady)
{
    public static StudioExtrasCompositionSummary Build(
        SkinDraftManifest draft,
        IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(files);
        var source = new SkinExtrasExtractionService().BuildSource(
            draft.Name,
            $"Kumori draft {draft.DraftId:N}",
            files.Select(pair => new SkinExtractionFile(
                pair.Key,
                pair.Value)).ToArray());
        var families = new SkinExtrasExtractionService().Analyze(source);
        var imageFiles = files.Keys.Count(filename =>
            Path.GetExtension(filename).Equals(
                ".png",
                StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filename).Equals(
                ".jpg",
                StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filename).Equals(
                ".jpeg",
                StringComparison.OrdinalIgnoreCase));
        var audioFiles = files.Keys.Count(filename =>
            Path.GetExtension(filename).Equals(
                ".wav",
                StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filename).Equals(
                ".mp3",
                StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filename).Equals(
                ".ogg",
                StringComparison.OrdinalIgnoreCase));
        var iniSettings = families.Sum(family => family.IniPatch.Count);
        var hasSkinIni = files.ContainsKey("skin.ini");
        var hasVisualFamily = families.Any(family =>
            family.Files.Any(file =>
                Path.GetExtension(file.Filename).Equals(
                    ".png",
                    StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(file.Filename).Equals(
                    ".jpg",
                    StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(file.Filename).Equals(
                    ".jpeg",
                    StringComparison.OrdinalIgnoreCase)));
        return new StudioExtrasCompositionSummary(
            families.Count,
            imageFiles,
            audioFiles,
            iniSettings,
            families.Select(family => family.Definition.Area)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            hasSkinIni,
            hasVisualFamily,
            hasSkinIni && hasVisualFamily);
    }
}

internal partial class StudioExtrasCompositionOverlay : CompositeDrawable
{
    private readonly FillFlowContainer content;
    private readonly Action continueToExtras;
    internal StudioExtrasCompositionSummary? AcceptanceSummary { get; private set; }

    public StudioExtrasCompositionOverlay(Action continueToExtras)
    {
        this.continueToExtras = continueToExtras;
        RelativeSizeAxes = Axes.Both;
        Depth = -96;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.78f),
            },
            new Container
            {
                Width = 760,
                Height = 680,
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
                        Padding = new MarginPadding(30),
                        Child = content = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 12),
                        },
                    },
                ],
            },
        ];
        Hide();
    }

    public void Present(
        SkinDraftManifest draft,
        IReadOnlyDictionary<string, byte[]> files)
    {
        var summary = StudioExtrasCompositionSummary.Build(draft, files);
        AcceptanceSummary = summary;
        content.Clear();
        content.AddRange(
        [
            label("EXTRAS COMPOSITION READINESS", 22, true),
            label(
                $"{draft.Name} by {draft.Creator}",
                15,
                true),
            label(
                $"{summary.FamilyCount} complete family/families | "
                + $"{summary.ImageFiles} image file(s) | "
                + $"{summary.AudioFiles} audio file(s) | "
                + $"{summary.IniSettings} family setting(s)",
                12,
                false),
            label(
                summary.Areas.Count == 0
                    ? "Areas: none detected"
                    : $"Areas: {string.Join(", ", summary.Areas)}",
                12,
                false),
            readiness(
                summary.HasSkinIni,
                "skin.ini is present and line-preserving"),
            readiness(
                summary.HasVisualFamily,
                "At least one complete visual family is present"),
            readiness(
                summary.FamilyCount > 1,
                "Multiple Extras families are composed"),
            label(
                summary.ExportReady
                    ? "READY: this draft can be reviewed and exported as a complete .osk. Add more families if desired."
                    : "NOT READY: add at least one complete visual family before publishing.",
                14,
                true),
            new StudioActionButton(
                "Continue choosing Extras families",
                () =>
                {
                    Hide();
                    continueToExtras();
                },
                accent: true),
            new StudioActionButton("Close readiness summary", Hide),
        ]);
        Show();
    }

    private static SpriteText readiness(bool passed, string text) =>
        label($"{(passed ? "PASS" : "NEEDS WORK")} | {text}", 13, passed);

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(
            size: size,
            weight: bold ? "SemiBold" : "Regular"),
        Colour = bold
            ? Colour4.FromHex("#FFB7D5")
            : Colour4.FromHex("#C6A8BA"),
    };
}
