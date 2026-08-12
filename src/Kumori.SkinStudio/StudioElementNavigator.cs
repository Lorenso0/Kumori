using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal sealed partial class StudioElementNavigator : CompositeDrawable
{
    private readonly SkinManager skins;
    private readonly Action<string> selectCategory;
    private readonly Func<IReadOnlyDictionary<string, byte[]>> effectiveFiles;
    private readonly Func<IReadOnlySet<string>> suppliedComponents;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer categories;
    private readonly Dictionary<string, StudioCategoryButton> buttons =
        new(StringComparer.OrdinalIgnoreCase);
    private string? selectedCategory = StudioSkinWorkbench.DefaultCategoryTitle;

    public StudioElementNavigator(
        SkinManager skins,
        float width,
        float topPadding,
        float bottomPadding,
        Action<string> selectCategory,
        Func<IReadOnlyDictionary<string, byte[]>> effectiveFiles,
        Func<IReadOnlySet<string>> suppliedComponents)
    {
        this.skins = skins;
        this.selectCategory = selectCategory;
        this.effectiveFiles = effectiveFiles;
        this.suppliedComponents = suppliedComponents;
        Width = width;
        RelativeSizeAxes = Axes.Y;
        Depth = -20;
        InternalChildren =
        [
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 88,
                Y = topPadding + 14,
                Padding = new MarginPadding { Horizontal = 16 },
                Children =
                [
                    new SpriteText
                    {
                        Text = "ASSET LIBRARY",
                        Font = FontUsage.Default.With(size: 12, weight: "Bold"),
                        Colour = Colour4.FromHex("#FFB7D5"),
                    },
                    new SpriteText
                    {
                        Text = "Choose a category",
                        Y = 18,
                        Font = FontUsage.Default.With(size: 10),
                        Colour = Colour4.FromHex("#A991A2"),
                    },
                    search = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 38,
                        Y = 40,
                        PlaceholderText = "Search library",
                    },
                ],
            },
            new OsuScrollContainer(Direction.Vertical)
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Top = topPadding + 112,
                    Bottom = bottomPadding + 14,
                    Left = 14,
                    Right = 14,
                },
                ScrollbarVisible = true,
                Child = categories = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 7),
                },
            },
        ];
        search.Current.BindValueChanged(_ => Rebuild());
        Rebuild();
    }

    public void Rebuild()
    {
        var files = effectiveFiles();
        var supplied = suppliedComponents();
        var allCategories = StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditional(files))
            .ToArray();
        var filtered = StudioSkinWorkbench.FilterCategories(
            allCategories,
            search.Current.Value);

        if (selectedCategory is null
            || allCategories.All(category => !category.Title.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase)))
        {
            selectedCategory = allCategories.FirstOrDefault()?.Title;
        }

        buttons.Clear();
        categories.Clear();
        foreach (var category in filtered)
        {
            int suppliedCount = category.Elements.Count(element =>
                supplied.Contains(element.ComponentName));
            var representative = category.Elements.FirstOrDefault(element =>
                                     supplied.Contains(element.ComponentName))
                                 ?? category.Elements.FirstOrDefault();
            var visual = category.IsAudio || representative is null
                ? null
                : skins.GetAnimation(
                    representative.ComponentName,
                    animatable: false,
                    looping: true,
                    applyConfigFrameRate: true);
            var button = new StudioCategoryButton(
                category.Title,
                category.IsAudio ? "AUDIO" : null,
                $"{suppliedCount}/{category.Elements.Count} supplied",
                visual,
                () =>
                {
                    SetSelectedCategory(category.Title);
                    selectCategory(category.Title);
                });
            button.SetSelected(category.Title.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase));
            buttons[category.Title] = button;
            categories.Add(button);
        }

        if (filtered.Count == 0)
        {
            categories.Add(new SpriteText
            {
                Text = "No categories or elements match this search.",
                Font = FontUsage.Default.With(size: 12),
                Colour = Colour4.FromHex("#A991A2"),
            });
        }
    }

    public void SetSelectedCategory(string? title)
    {
        selectedCategory = title;
        foreach (var (category, button) in buttons)
        {
            button.SetSelected(category.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    public void SetSelectedComponent(string? componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
            return;
        var files = effectiveFiles();
        var category = StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditional(files))
            .FirstOrDefault(candidate => candidate.Elements.Any(element =>
                element.ComponentName.Equals(
                    componentName,
                    StringComparison.OrdinalIgnoreCase)));
        if (category is not null)
            SetSelectedCategory(category.Title);
    }

    private static IReadOnlyList<StudioSkinCoverageCategory> discoverAdditional(
        IReadOnlyDictionary<string, byte[]> files)
    {
        var covered = StudioSkinCoverageCatalog.Categories
            .SelectMany(category => category.Elements)
            .Select(element => element.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, StudioSkinCoverageElement>(
            StringComparer.OrdinalIgnoreCase);
        var audio = new Dictionary<string, StudioSkinCoverageElement>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var filename in files.Keys)
        {
            var component = SkinDraftAssetService.ComponentName(filename);
            if (covered.Contains(component))
                continue;
            if (SkinMediaTypes.IsImage(filename))
            {
                images.TryAdd(
                    component,
                    new StudioSkinCoverageElement(
                        Path.GetFileName(component),
                        component,
                        null));
            }
            else if (SkinMediaTypes.IsAudio(filename))
            {
                audio.TryAdd(
                    component,
                    new StudioSkinCoverageElement(
                        Path.GetFileName(component),
                        component,
                        null));
            }
        }

        var result = new List<StudioSkinCoverageCategory>();
        if (images.Count > 0)
        {
            result.Add(new StudioSkinCoverageCategory(
                "Additional images",
                "Other-ruleset and custom images preserved by this draft.",
                false,
                images.Values.OrderBy(element => element.Label).ToArray()));
        }
        if (audio.Count > 0)
        {
            result.Add(new StudioSkinCoverageCategory(
                "Additional audio",
                "Other-ruleset and custom audio preserved by this draft.",
                true,
                audio.Values.OrderBy(element => element.Label).ToArray()));
        }
        return result;
    }
}

internal sealed partial class StudioCategoryButton : ClickableContainer
{
    private readonly Box background;
    private readonly Action action;

    public StudioCategoryButton(
        string title,
        string? iconText,
        string detail,
        Drawable? visual,
        Action action)
    {
        this.action = action;
        RelativeSizeAxes = Axes.X;
        Height = 52;
        Masking = true;
        CornerRadius = 8;
        BorderThickness = 1;
        BorderColour = Colour4.FromHex("#39313F");
        Children =
        [
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.FromHex("#282430"),
            },
            new Container
            {
                Size = new Vector2(38),
                Position = new Vector2(7),
                Masking = true,
                CornerRadius = 6,
                Children =
                [
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.FromHex("#17141D"),
                    },
                    visual is null
                        ? new SpriteText
                        {
                            Text = iconText ?? title[..1].ToUpperInvariant(),
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Font = FontUsage.Default.With(size: 9, weight: "Bold"),
                            Colour = Colour4.FromHex("#FFB7D5"),
                        }
                        : new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding(4),
                            Child = new StudioAssetPreview(visual, featured: false)
                            {
                                RelativeSizeAxes = Axes.Both,
                            },
                        },
                ],
            },
            new SpriteText
            {
                Text = title,
                Position = new Vector2(54, 8),
                Font = FontUsage.Default.With(size: 12, weight: "SemiBold"),
                Colour = Colour4.White,
            },
            new SpriteText
            {
                Text = detail,
                Position = new Vector2(54, 29),
                Font = FontUsage.Default.With(size: 8),
                Colour = Colour4.FromHex("#A991A2"),
            },
        ];
    }

    public void SetSelected(bool selected)
    {
        background.Colour = Colour4.FromHex(
            selected ? "#67344F" : "#282430");
        BorderColour = Colour4.FromHex(
            selected ? "#D36C9B" : "#39313F");
    }

    protected override bool OnClick(ClickEvent e)
    {
        action();
        return true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        this.FadeTo(0.82f, 80);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        this.FadeTo(1, 80);
        base.OnHoverLost(e);
    }
}
