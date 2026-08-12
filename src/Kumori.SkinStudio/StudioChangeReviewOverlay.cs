using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioChangeReviewOverlay : CompositeDrawable
{
    private readonly Func<string, bool> discard;
    private readonly FillFlowContainer changesFlow;

    public StudioChangeReviewOverlay(Func<string, bool> discard)
    {
        this.discard = discard;
        RelativeSizeAxes = Axes.Both;
        Depth = -95;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.78f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Horizontal = 110,
                    Vertical = 64,
                },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    Children =
                    [
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.FromHex("#1B1925"),
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 86,
                            Padding = new MarginPadding(24),
                            Child = label("REVIEW STAGED CHANGES", 21, true),
                        },
                        new OsuScrollContainer(Direction.Vertical)
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding
                            {
                                Top = 86,
                                Bottom = 74,
                                Horizontal = 24,
                            },
                            ScrollbarVisible = true,
                            Child = changesFlow = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 10),
                            },
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 74,
                            Anchor = Anchor.BottomLeft,
                            Origin = Anchor.BottomLeft,
                            Padding = new MarginPadding
                            {
                                Horizontal = 24,
                                Vertical = 14,
                            },
                            Child = new StudioActionButton("Close review", Hide),
                        },
                    ],
                },
            },
        ];
        Hide();
    }

    public void Present(SkinDraftManifest draft)
    {
        changesFlow.Clear();
        foreach (var change in draft.Changes.OrderBy(
                     change => change.Filename,
                     StringComparer.OrdinalIgnoreCase))
        {
            changesFlow.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 5),
                Padding = new MarginPadding
                {
                    Bottom = 8,
                },
                Children =
                [
                    label(
                        $"{(change.Kind == SkinDraftChangeKind.Delete ? "DELETE" : "UPSERT")} · {change.Filename}",
                        13,
                        true),
                    label(change.Description, 11, false),
                    label(
                        $"expected {shortHash(change.ExpectedHash)} · content {shortHash(change.ContentHash)} · {change.SizeBytes:N0} byte(s)",
                        10,
                        false),
                    new StudioActionButton(
                        $"Discard only {change.Filename}",
                        () =>
                        {
                            if (discard(change.Filename))
                                Hide();
                        }),
                ],
            });
        }
        if (draft.Changes.Count == 0)
            changesFlow.Add(label("No staged changes.", 13, false));
        Show();
    }

    internal static string ShortHash(string? hash) => shortHash(hash);

    private static string shortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? "none"
            : hash[..Math.Min(12, hash.Length)];

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
