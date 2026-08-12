using Kumori.Tracking;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioInstalledSkinBrowserOverlay : CompositeDrawable
{
    private readonly Action<Guid> import;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer skinsFlow;
    private IReadOnlyList<LazerSkinInfo> skins = [];

    public StudioInstalledSkinBrowserOverlay(Action<Guid> import)
    {
        this.import = import;
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
                                    label("OPEN INSTALLED LAZER SKIN", 21, true),
                                    search = new OsuTextBox
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        PlaceholderText = "Search installed skin or creator",
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
                            Child = skinsFlow = new FillFlowContainer
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

    public void Present(IReadOnlyList<LazerSkinInfo> skins)
    {
        this.skins = skins;
        search.Current.Value = "";
        rebuild();
        Show();
    }

    internal static IReadOnlyList<LazerSkinInfo> Filter(
        IEnumerable<LazerSkinInfo> skins,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(skins);
        var term = query?.Trim();
        return (string.IsNullOrWhiteSpace(term)
                ? skins
                : skins.Where(skin =>
                    skin.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || skin.Creator.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(skin => skin.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void rebuild()
    {
        skinsFlow.Clear();
        var visible = Filter(skins, search.Current.Value);
        foreach (var skin in visible)
        {
            skinsFlow.Add(new StudioActionButton(
                $"{skin.DisplayName} · {skin.Files.Count} file(s)",
                () =>
                {
                    import(skin.Id);
                    Hide();
                }));
        }
        if (visible.Count == 0)
            skinsFlow.Add(label("No installed skins match this search.", 13, false));
    }

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
