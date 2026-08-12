using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioDraftBrowserOverlay : CompositeDrawable
{
    private readonly Action<Guid> openDraft;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer draftsFlow;
    private IReadOnlyList<SkinDraftManifest> drafts = [];
    private Guid? currentDraftId;

    public StudioDraftBrowserOverlay(Action<Guid> openDraft)
    {
        this.openDraft = openDraft;
        RelativeSizeAxes = Axes.Both;
        Depth = -93;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.76f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Horizontal = 120,
                    Vertical = 70,
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
                            Height = 124,
                            Padding = new MarginPadding(24),
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 10),
                                Children =
                                [
                                    label("OPEN DRAFT", 21, true),
                                    search = new OsuTextBox
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        PlaceholderText = "Search draft name, author, or source",
                                    },
                                ],
                            },
                        },
                        new OsuScrollContainer(Direction.Vertical)
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding
                            {
                                Top = 124,
                                Bottom = 74,
                                Horizontal = 24,
                            },
                            ScrollbarVisible = true,
                            Child = draftsFlow = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 8),
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
                            Child = new StudioActionButton("Cancel", Hide),
                        },
                    ],
                },
            },
        ];
        search.Current.BindValueChanged(_ => rebuild());
        Hide();
    }

    public void Present(
        IReadOnlyList<SkinDraftManifest> drafts,
        Guid? currentDraftId)
    {
        this.drafts = drafts;
        this.currentDraftId = currentDraftId;
        search.Current.Value = "";
        rebuild();
        Show();
    }

    internal static IReadOnlyList<SkinDraftManifest> FilterDrafts(
        IEnumerable<SkinDraftManifest> drafts,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var term = query?.Trim();
        var filtered = string.IsNullOrWhiteSpace(term)
            ? drafts
            : drafts.Where(draft =>
                draft.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || draft.Creator.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (draft.SourcePath?.Contains(
                    term,
                    StringComparison.OrdinalIgnoreCase) ?? false));
        return filtered
            .OrderByDescending(draft => draft.UpdatedAt)
            .ThenBy(draft => draft.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void rebuild()
    {
        draftsFlow.Clear();
        var visible = FilterDrafts(drafts, search.Current.Value);
        foreach (var draft in visible)
        {
            var source = draft.SourcePath is null
                ? "blank"
                : Path.GetFileName(draft.SourcePath);
            var button = new StudioActionButton(
                $"{draft.Name} · {draft.Creator} · {source} · {draft.UpdatedAt.LocalDateTime:g}",
                () =>
                {
                    openDraft(draft.DraftId);
                    Hide();
                });
            button.SetSelected(draft.DraftId == currentDraftId);
            draftsFlow.Add(button);
        }
        if (visible.Count == 0)
            draftsFlow.Add(label("No drafts match this search.", 13, false));
    }

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
