using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Audio;
using osu.Game.Database;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Rulesets.Objects;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal sealed record StudioSkinFileSnapshot(
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlySet<string> VisibleSuppliedComponents)
{
    public static StudioSkinFileSnapshot Empty { get; } = new(
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

internal partial class StudioSkinWorkbench : CompositeDrawable
{
    internal static string DefaultCategoryTitle =>
        StudioSkinCoverageCatalog.Categories.First().Title;

    private readonly SkinManager skins;
    private readonly Action<string> selectAsset;
    private readonly Action<string> reportStatus;
    private readonly Action filterStateChanged;
    private readonly Func<IReadOnlyDictionary<string, byte[]>>?
        effectiveFiles;
    private readonly Func<IReadOnlySet<string>>? suppliedComponents;
    private readonly FillFlowContainer categories;
    private readonly OsuTextBox search;
    private readonly StudioActionButton categoryFilterButton;
    private readonly StudioActionButton fallbackFilterButton;
    private readonly List<StudioAudioTile> audioTiles = [];
    private IReadOnlyList<StudioSkinCoverageCategory> lastFiltered = [];
    private string? selectedCategory = DefaultCategoryTitle;
    private string? focusedComponent;
    private bool hideFallbackOnly;

    public StudioSkinWorkbench(
        SkinManager skins,
        Action<string> replaceAsset,
        Action<string> reportStatus,
        Action? filterStateChanged = null,
        Func<IReadOnlyDictionary<string, byte[]>>? effectiveFiles = null,
        Func<IReadOnlySet<string>>? suppliedComponents = null)
    {
        this.skins = skins;
        selectAsset = replaceAsset;
        this.reportStatus = reportStatus;
        this.filterStateChanged = filterStateChanged ?? (() => { });
        this.effectiveFiles = effectiveFiles;
        this.suppliedComponents = suppliedComponents;
        RelativeSizeAxes = Axes.Both;
        InternalChildren =
        [
            new OsuScrollContainer(Direction.Vertical)
            {
                RelativeSizeAxes = Axes.Both,
                ScrollbarVisible = true,
                Child = categories = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 22),
                    Padding = new MarginPadding
                    {
                        Top = 108,
                        Bottom = 30,
                        Horizontal = 24,
                    },
                },
            },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 42,
                Y = 12,
                Padding = new MarginPadding { Horizontal = 24 },
                Child = search = new OsuTextBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 40,
                    PlaceholderText = "Search elements in this category",
                },
            },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 34,
                Y = 60,
                Padding = new MarginPadding { Horizontal = 24 },
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(8, 0),
                    Children =
                    [
                        categoryFilterButton = new StudioActionButton(
                            $"Category: {selectedCategory}",
                            cycleCategory)
                        {
                            RelativeSizeAxes = Axes.None,
                            Width = 260,
                            Height = 34,
                        },
                        fallbackFilterButton = new StudioActionButton(
                            "Show: skin + fallback",
                            toggleFallbackFilter)
                        {
                            RelativeSizeAxes = Axes.None,
                            Width = 220,
                            Height = 34,
                        },
                        new StudioActionButton(
                            "Stop audio",
                            () =>
                            {
                                stopAudio();
                                reportStatus("Stopped all workbench preview audio.");
                            })
                        {
                            RelativeSizeAxes = Axes.None,
                            Width = 126,
                            Height = 34,
                        },
                    ],
                },
            },
        ];
        search.Current.BindValueChanged(change =>
        {
            if (!string.Equals(
                    change.NewValue,
                    focusedComponent,
                    StringComparison.OrdinalIgnoreCase))
            {
                focusedComponent = null;
            }
            if (!string.IsNullOrWhiteSpace(change.NewValue))
            {
                selectedCategory = null;
                categoryFilterButton.SetText("Category: all");
            }
            Rebuild();
        });
        Rebuild();
    }

    public void Rebuild()
    {
        stopAudio();
        audioTiles.Clear();
        categories.Clear();
        var allCategories = StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditionalCategories())
            .ToArray();
        if (selectedCategory is not null
            && allCategories.All(category => !category.Title.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase)))
        {
            selectedCategory = null;
        }
        var suppliedComponents = suppliedComponentsForCurrentSkin();
        var filtered = FilterCategories(
            allCategories,
            search.Current.Value,
            selectedCategory,
            hideFallbackOnly,
            suppliedComponents.Contains);
        if (!string.IsNullOrWhiteSpace(focusedComponent))
        {
            filtered = filtered
                .Select(category => category with
                {
                    Elements = category.Elements
                        .Where(element => element.ComponentName.Equals(
                            focusedComponent,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                })
                .Where(category => category.Elements.Count > 0)
                .ToArray();
        }
        lastFiltered = filtered;
        categories.AddRange(filtered.Select(category =>
            category.IsAudio
                ? audioCategory(category, suppliedComponents)
                : imageCategory(category, suppliedComponents)));
        if (filtered.Count == 0)
        {
            categories.Add(body(
                "No skin elements match this search.",
                14,
                Colour4.FromHex("#AD99A7")));
        }
        filterStateChanged();
    }

    internal static IReadOnlyList<StudioSkinCoverageCategory> FilterCategories(
        IEnumerable<StudioSkinCoverageCategory> source,
        string? query,
        string? categoryTitle = null,
        bool hideFallbackOnly = false,
        Func<string, bool>? suppliedBySkin = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var available = source;
        if (!string.IsNullOrWhiteSpace(categoryTitle))
        {
            available = available.Where(category => category.Title.Equals(
                categoryTitle,
                StringComparison.OrdinalIgnoreCase));
        }
        if (hideFallbackOnly)
        {
            ArgumentNullException.ThrowIfNull(suppliedBySkin);
            available = available
                .Select(category => category with
                {
                    Elements = category.Elements
                        .Where(element => suppliedBySkin(element.ComponentName))
                        .ToArray(),
                })
                .Where(category => category.Elements.Count > 0);
        }

        var term = query?.Trim();
        if (string.IsNullOrEmpty(term))
            return available.ToArray();

        var result = new List<StudioSkinCoverageCategory>();
        foreach (var category in available)
        {
            if (category.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || category.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(category);
                continue;
            }

            var elements = category.Elements
                .Where(element =>
                    element.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || element.ComponentName.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (elements.Length > 0)
                result.Add(category with { Elements = elements });
        }
        return result;
    }

    private void cycleCategory()
    {
        var titles = StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditionalCategories())
            .Select(category => category.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var current = selectedCategory is null
            ? -1
            : Array.FindIndex(titles, title => title.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase));
        selectedCategory = current + 1 >= titles.Length
            ? titles.FirstOrDefault()
            : titles[current + 1];
        categoryFilterButton.SetText(
            $"Category: {selectedCategory}");
        Rebuild();
    }

    private void toggleFallbackFilter()
    {
        hideFallbackOnly = !hideFallbackOnly;
        fallbackFilterButton.SetText(
            hideFallbackOnly
                ? "Show: non-empty skin files"
                : "Show: skin + fallback");
        fallbackFilterButton.SetSelected(hideFallbackOnly);
        Rebuild();
    }

    private HashSet<string> suppliedComponentsForCurrentSkin()
    {
        if (suppliedComponents is not null)
        {
            return suppliedComponents()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        if (effectiveFiles is not null)
            return VisibleSuppliedComponents(effectiveFiles());
        return skins.CurrentSkin.Value.SkinInfo.PerformRead(info =>
            info.Files
                .Select(file => componentName(file.Filename))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal static HashSet<string> VisibleSuppliedComponents(
        IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var supplied = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (filename, bytes) in files)
        {
            if (SkinMediaTypes.IsImage(filename))
            {
                try
                {
                    if (!SkinMediaValidationService.ValidateImage(
                            filename,
                            bytes).HasVisiblePixels)
                    {
                        continue;
                    }
                }
                catch
                {
                    // A malformed skin file must stay selectable for replacement.
                }
            }
            supplied.Add(componentName(filename));
        }
        return supplied;
    }

    private IReadOnlyList<StudioSkinCoverageCategory> discoverAdditionalCategories()
    {
        var covered = StudioSkinCoverageCatalog.Categories
            .SelectMany(category => category.Elements)
            .Select(element => element.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = skins.CurrentSkin.Value.SkinInfo.PerformRead(
            info => info.Files.Select(file => file.Filename).ToArray());
        var images = new Dictionary<string, StudioSkinCoverageElement>(
            StringComparer.OrdinalIgnoreCase);
        var audio = new Dictionary<string, StudioSkinCoverageElement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var filename in files)
        {
            var extension = Path.GetExtension(filename).ToLowerInvariant();
            var component = componentName(filename);
            if (covered.Contains(component))
                continue;
            var label = Path.GetFileName(component);
            if (extension is ".png" or ".jpg" or ".jpeg")
            {
                images.TryAdd(
                    component,
                    new StudioSkinCoverageElement(label, component, null));
            }
            else if (extension is ".wav" or ".mp3" or ".ogg")
            {
                audio.TryAdd(
                    component,
                    new StudioSkinCoverageElement(
                        label,
                        component,
                        () => new SampleInfo(component)));
            }
        }

        var result = new List<StudioSkinCoverageCategory>();
        if (images.Count > 0)
        {
            result.Add(new StudioSkinCoverageCategory(
                "Additional skin images",
                "Every remaining image discovered in this draft, including other-ruleset and custom assets.",
                false,
                images.Values.OrderBy(element => element.ComponentName).ToArray()));
        }
        if (audio.Count > 0)
        {
            result.Add(new StudioSkinCoverageCategory(
                "Additional skin audio",
                "Every remaining audio resource discovered in this draft.",
                true,
                audio.Values.OrderBy(element => element.ComponentName).ToArray()));
        }
        return result;
    }

    internal static string ComponentName(string filename) =>
        SkinDraftAssetService.ComponentName(filename);

    internal static bool IsAudioComponent(string componentName) =>
        StudioSkinCoverageCatalog.Categories
            .SelectMany(category => category.Elements)
            .Any(element =>
                element.SampleFactory is not null
                && element.ComponentName.Equals(
                    componentName,
                    StringComparison.OrdinalIgnoreCase));

    internal string? ActiveCategoryTitle => selectedCategory;

    internal IReadOnlyList<string> ActiveCategoryComponents()
    {
        if (selectedCategory is null)
            return [];
        return StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditionalCategories())
            .FirstOrDefault(category => category.Title.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase))
            ?.Elements.Select(element => element.ComponentName).ToArray()
            ?? [];
    }

    internal void FocusComponent(string componentName)
    {
        var matchingCategory = StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditionalCategories())
            .FirstOrDefault(category => category.Elements.Any(element =>
                element.ComponentName.Equals(
                    componentName,
                    StringComparison.OrdinalIgnoreCase)));
        focusedComponent = componentName;
        search.Current.Value = componentName;
        selectedCategory = matchingCategory?.Title ?? selectedCategory;
        categoryFilterButton.SetText($"Category: {selectedCategory}");
        Rebuild();
    }

    internal void SetCategory(string title)
    {
        var available = StudioSkinCoverageCatalog.Categories
            .Concat(discoverAdditionalCategories());
        if (!available.Any(category => category.Title.Equals(
                title,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        selectedCategory = title;
        focusedComponent = null;
        search.Current.Value = string.Empty;
        categoryFilterButton.SetText($"Category: {title}");
        Rebuild();
    }

    internal void SetAcceptanceCategory(string title)
    {
        if (!StudioSkinCoverageCatalog.Categories.Any(category =>
                category.Title.Equals(title, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Unknown visual-acceptance category “{title}”.");
        }
        SetCategory(title);
    }

    internal void SetAcceptanceSearch(string value)
    {
        search.Current.Value = value;
        Rebuild();
    }

    internal void ToggleAcceptanceFallbackFilter() =>
        toggleFallbackFilter();

    internal bool AcceptanceHidesFallbackOnly => hideFallbackOnly;

    internal IReadOnlyList<string> AcceptanceVisibleComponents =>
        lastFiltered
            .SelectMany(category => category.Elements)
            .Select(element => element.ComponentName)
            .ToArray();

    internal void ClearAcceptanceFilters()
    {
        selectedCategory = DefaultCategoryTitle;
        hideFallbackOnly = false;
        search.Current.Value = string.Empty;
        categoryFilterButton.SetText($"Category: {selectedCategory}");
        fallbackFilterButton.SetText("Show: skin + fallback");
        fallbackFilterButton.SetSelected(false);
        Rebuild();
    }

    internal void ToggleAcceptanceAudio(string componentName)
    {
        var tile = audioTiles.FirstOrDefault(candidate =>
            candidate.ComponentName.Equals(
                componentName,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Audio workbench tile '{componentName}' is unavailable.");
        tile.ToggleAcceptance();
    }

    internal bool IsAcceptanceAudioPlaying(string componentName) =>
        audioTiles.FirstOrDefault(candidate =>
            candidate.ComponentName.Equals(
                componentName,
                StringComparison.OrdinalIgnoreCase))?.IsPlaying == true;

    private static string componentName(string filename) =>
        SkinDraftAssetService.ComponentName(filename);

    private Drawable imageCategory(
        StudioSkinCoverageCategory category,
        IReadOnlySet<string> suppliedComponents)
    {
        var focusedPreview = category.Elements.Count == 1
                             && !string.IsNullOrWhiteSpace(search.Current.Value);
        var tiles = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(10),
        };
        StudioAssetTile? featuredTile = null;
        foreach (var element in category.Elements)
        {
            var suppliedByDraft = suppliedComponents.Contains(element.ComponentName);
            var visual = skins.GetAnimation(
                element.ComponentName,
                animatable: focusedPreview,
                looping: true,
                applyConfigFrameRate: true);
            var tile = new StudioAssetTile(
                element.Label,
                element.ComponentName,
                visual,
                suppliedByDraft,
                focusedPreview,
                () =>
                {
                    reportStatus($"Choose a replacement for {element.ComponentName}.");
                    selectAsset(element.ComponentName);
                });
            if (focusedPreview)
                featuredTile = tile;
            else
                tiles.Add(tile);
        }
        Drawable content = tiles;
        if (featuredTile is not null)
        {
            featuredTile.Anchor = Anchor.TopCentre;
            featuredTile.Origin = Anchor.TopCentre;
            content = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 510,
                Child = featuredTile,
            };
        }
        return categoryContainer(category.Title, category.Description, content);
    }

    private Drawable audioCategory(
        StudioSkinCoverageCategory category,
        IReadOnlySet<string> suppliedComponents)
    {
        var tiles = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(10),
        };
        foreach (var element in category.Elements)
        {
            var sample = element.SampleFactory?.Invoke()
                         ?? new SampleInfo(element.ComponentName);
            StudioAudioTile? tile = null;
            tile = new StudioAudioTile(
                element.Label,
                element.ComponentName,
                sample,
                suppliedComponents.Contains(element.ComponentName),
                playing =>
                {
                    if (playing)
                    {
                        foreach (var other in audioTiles.Where(other => other != tile))
                            other.Stop(notify: false);
                    }
                    selectAsset(element.ComponentName);
                    reportStatus(playing
                        ? $"Playing {element.Label} through lazer's active skin pipeline. Click again to stop."
                        : $"Stopped {element.Label}.");
                });
            audioTiles.Add(tile);
            tiles.Add(tile);
        }
        return categoryContainer(category.Title, category.Description, tiles);
    }

    private void stopAudio()
    {
        foreach (var tile in audioTiles)
            tile.Stop(notify: false);
    }

    internal void StopAudioPreviews() => stopAudio();

    private static Drawable categoryContainer(
        string title,
        string description,
        Drawable tiles) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 8),
            Children =
            [
                new SpriteText
                {
                    Text = title.ToUpperInvariant(),
                    Font = FontUsage.Default.With(size: 17, weight: "Bold"),
                    Colour = Colour4.FromHex("#FFB7D5"),
                },
                new SpriteText
                {
                    Text = description,
                    Font = FontUsage.Default.With(size: 11),
                    Colour = Colour4.FromHex("#AD99A7"),
                },
                tiles,
            ],
        };

    private static SpriteText body(string text, float size, Colour4 colour) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size),
        Colour = colour,
    };
}

internal partial class StudioAssetTile : ClickableContainer
{
    private readonly Box background;
    private readonly Action replace;

    public StudioAssetTile(
        string label,
        string componentName,
        Drawable? visual,
        bool suppliedByDraft,
        bool featured,
        Action replace)
    {
        this.replace = replace;
        Width = featured ? 560 : 150;
        Height = featured ? 470 : 132;
        Masking = true;
        CornerRadius = 8;
        BorderThickness = 1;
        BorderColour = suppliedByDraft
            ? Colour4.FromHex("#6F4961")
            : Colour4.FromHex("#38313E");
        Children =
        [
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.FromHex("#211E28"),
            },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = featured ? 392 : 86,
                Padding = new MarginPadding(featured ? 22 : 8),
                Child = FitVisualForPreview(visual, featured) ?? new SpriteText
                {
                    Text = "NO IMAGE",
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = FontUsage.Default.With(size: 10, weight: "Bold"),
                    Colour = Colour4.FromHex("#786A75"),
                },
            },
            new SpriteText
            {
                Text = label,
                X = 10,
                Y = featured ? 405 : 91,
                Font = FontUsage.Default.With(
                    size: featured ? 18 : 11,
                    weight: "SemiBold"),
                Colour = Colour4.White,
            },
            new SpriteText
            {
                Text = suppliedByDraft ? "SKIN" : "FALLBACK",
                X = 10,
                Y = featured ? 439 : 111,
                Font = FontUsage.Default.With(
                    size: featured ? 10 : 8,
                    weight: "Bold"),
                Colour = suppliedByDraft
                    ? Colour4.FromHex("#FFB7D5")
                    : Colour4.FromHex("#8F8290"),
            },
            new SpriteText
            {
                Text = componentName,
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Margin = new MarginPadding { Right = 8, Bottom = 6 },
                Font = FontUsage.Default.With(size: featured ? 9 : 7),
                Colour = Colour4.FromHex("#776B75"),
            },
        ];
    }

    internal static Drawable? FitVisualForPreview(
        Drawable? visual,
        bool featured)
    {
        if (visual is null)
            return null;

        return new StudioAssetPreview(visual, featured);
    }

    protected override bool OnClick(ClickEvent e)
    {
        replace();
        return true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        background.FadeColour(Colour4.FromHex("#352B38"), 100);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        background.FadeColour(Colour4.FromHex("#211E28"), 100);
        base.OnHoverLost(e);
    }
}

internal partial class StudioAssetPreview : CompositeDrawable
{
    private readonly Drawable visual;
    private readonly float maximumUpscale;

    public StudioAssetPreview(Drawable visual, bool featured)
    {
        this.visual = visual;
        maximumUpscale = featured ? 3 : 1.25f;
        RelativeSizeAxes = Axes.Both;
        InternalChild = visual;
        visual.Anchor = Anchor.Centre;
        visual.Origin = Anchor.Centre;
    }

    protected override void Update()
    {
        base.Update();

        var contentSize = visual.DrawSize;
        if (contentSize.X <= 0 || contentSize.Y <= 0)
            return;

        var scale = MathF.Min(
            DrawWidth / contentSize.X,
            DrawHeight / contentSize.Y);
        if (!float.IsFinite(scale) || scale <= 0)
            return;

        visual.Scale = new Vector2(MathF.Min(scale, maximumUpscale));
    }
}

internal partial class StudioAudioTile : ClickableContainer
{
    private readonly SkinnableSound sound;
    private readonly Box background;
    private readonly SpriteText icon;
    private readonly Action<bool> reportPlayback;

    public string ComponentName { get; }

    internal bool IsPlaying => sound.IsPlaying;

    public StudioAudioTile(
        string label,
        string componentName,
        ISampleInfo sample,
        bool suppliedByDraft,
        Action<bool> reportPlayback)
    {
        this.reportPlayback = reportPlayback;
        ComponentName = componentName;
        Width = 150;
        Height = 86;
        Masking = true;
        CornerRadius = 8;
        BorderThickness = 1;
        BorderColour = suppliedByDraft
            ? Colour4.FromHex("#6F4961")
            : Colour4.FromHex("#38313E");
        Children =
        [
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.FromHex("#211E28"),
            },
            sound = new SkinnableSound(sample),
            icon = new SpriteText
            {
                Text = "▶",
                X = 12,
                Y = 12,
                Font = FontUsage.Default.With(size: 20, weight: "Bold"),
                Colour = Colour4.FromHex("#FFB7D5"),
            },
            new SpriteText
            {
                Text = label,
                X = 43,
                Y = 14,
                Font = FontUsage.Default.With(size: 11, weight: "SemiBold"),
                Colour = Colour4.White,
            },
            new SpriteText
            {
                Text = componentName,
                X = 12,
                Y = 58,
                Font = FontUsage.Default.With(size: 8),
                Colour = Colour4.FromHex("#8F8290"),
            },
            new SpriteText
            {
                Text = suppliedByDraft ? "SKIN" : "FALLBACK",
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Margin = new MarginPadding { Right = 8, Bottom = 7 },
                Font = FontUsage.Default.With(size: 7, weight: "Bold"),
                Colour = suppliedByDraft
                    ? Colour4.FromHex("#FFB7D5")
                    : Colour4.FromHex("#8F8290"),
            },
        ];
    }

    protected override bool OnClick(ClickEvent e)
    {
        togglePlayback();
        return true;
    }

    internal void ToggleAcceptance() => togglePlayback();

    private void togglePlayback()
    {
        if (sound.IsPlaying)
        {
            sound.Stop();
            icon.Text = "▶";
            reportPlayback(false);
        }
        else
        {
            sound.Play();
            icon.Text = "■";
            reportPlayback(true);
        }
    }

    public void Stop(bool notify = true)
    {
        if (!sound.IsPlaying)
            return;
        sound.Stop();
        icon.Text = "▶";
        if (notify)
            reportPlayback(false);
    }

    protected override bool OnHover(HoverEvent e)
    {
        background.FadeColour(Colour4.FromHex("#352B38"), 100);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        background.FadeColour(Colour4.FromHex("#211E28"), 100);
        base.OnHoverLost(e);
    }
}

internal static class StudioSkinCoverageCatalog
{
    private static IReadOnlyList<StudioSkinCoverageCategory> LegacyCategories { get; } =
    [
        images(
            "Hit objects",
            "Circles, overlays, approach timing, follow points, and slider endpoints.",
            ("Approach circle", "approachcircle"),
            ("Hit circle", "hitcircle"),
            ("Hit overlay", "hitcircleoverlay"),
            ("Slider start", "sliderstartcircle"),
            ("Start overlay", "sliderstartcircleoverlay"),
            ("Slider end", "sliderendcircle"),
            ("End overlay", "sliderendcircleoverlay"),
            ("Reverse arrow", "reversearrow"),
            ("Follow point", "followpoint"),
            ("Slider score point", "sliderscorepoint"),
            ("Legacy slider tick", "sliderpoint10"),
            ("Legacy slider repeat", "sliderpoint30"),
            ("Slider ball", "sliderb"),
            ("Slider ball frame zero", "sliderb0"),
            ("Slider ball normal map", "sliderb-nd"),
            ("Slider ball specular map", "sliderb-spec"),
            ("Follow circle", "sliderfollowcircle")),
        images(
            "Cursor and trail",
            "Interactive cursor components and both continuous and discrete trail resources.",
            ("Cursor", "cursor"),
            ("Cursor middle", "cursormiddle"),
            ("Cursor trail", "cursortrail"),
            ("Cursor ripple", "cursor-ripple"),
            ("Cursor particles", "star2"),
            ("Cursor smoke", "cursor-smoke")),
        images(
            "Gameplay HUD",
            "Health, scorebar, input overlay, skip, pause, fail, and section markers.",
            ("Scorebar background", "scorebar-bg"),
            ("Scorebar colour", "scorebar-colour"),
            ("Scorebar marker", "scorebar-marker"),
            ("Ki", "scorebar-ki"),
            ("Ki danger", "scorebar-kidanger"),
            ("Ki danger 2", "scorebar-kidanger2"),
            ("Input background", "inputoverlay-background"),
            ("Input key", "inputoverlay-key"),
            ("Skip", "play-skip"),
            ("Pause overlay", "pause-overlay"),
            ("Fail background", "fail-background"),
            ("Section pass", "section-pass"),
            ("Section fail", "section-fail")),
        images(
            "Judgements",
            "All legacy osu!standard result sprites, including geki and katu variants.",
            ("Miss", "hit0"),
            ("50", "hit50"),
            ("100", "hit100"),
            ("100 katu", "hit100k"),
            ("300", "hit300"),
            ("300 geki", "hit300g"),
            ("300 katu", "hit300k"),
            ("Slider end miss", "sliderendmiss"),
            ("Slider tick miss", "slidertickmiss"),
            ("50 particle", "particle50"),
            ("100 particle", "particle100"),
            ("300 particle", "particle300")),
        images(
            "Spinner",
            "Old-style and new-style spinner layers rendered with lazer skin fallback rules.",
            ("Background", "spinner-background"),
            ("Circle", "spinner-circle"),
            ("Metre", "spinner-metre"),
            ("Approach", "spinner-approachcircle"),
            ("Bottom", "spinner-bottom"),
            ("Glow", "spinner-glow"),
            ("Middle", "spinner-middle"),
            ("Middle 2", "spinner-middle2"),
            ("Top", "spinner-top"),
            ("Clear", "spinner-clear"),
            ("Spin", "spinner-spin"),
            ("RPM", "spinner-rpm")),
        images(
            "Countdown and prompts",
            "Ready/go prompts, countdown frames, warning arrows, and combo bursts.",
            ("Ready", "ready"),
            ("Count 3", "count3"),
            ("Count 2", "count2"),
            ("Count 1", "count1"),
            ("Go", "go"),
            ("Warning arrow", "arrow-warning"),
            ("Combo burst", "comboburst")),
        images(
            "Ranking",
            "Result screen panels, labels, grades, and perfect-combo resources.",
            ("Ranking panel", "ranking-panel"),
            ("Ranking graph", "ranking-graph"),
            ("Max combo", "ranking-maxcombo"),
            ("Accuracy", "ranking-accuracy"),
            ("Grade XH", "ranking-XH"),
            ("Grade X", "ranking-X"),
            ("Grade SH", "ranking-SH"),
            ("Grade S", "ranking-S"),
            ("Grade A", "ranking-A"),
            ("Grade B", "ranking-B"),
            ("Grade C", "ranking-C"),
            ("Grade D", "ranking-D"),
            ("Small grade XH", "ranking-XH-small"),
            ("Small grade X", "ranking-X-small"),
            ("Small grade SH", "ranking-SH-small"),
            ("Small grade S", "ranking-S-small"),
            ("Small grade A", "ranking-A-small"),
            ("Small grade B", "ranking-B-small"),
            ("Small grade C", "ranking-C-small"),
            ("Small grade D", "ranking-D-small"),
            ("Perfect", "ranking-perfect")),
        images(
            "Menus and selection",
            "Mode selection, pause actions, song-selection controls, and menu background.",
            ("Menu background", "menu-background"),
            ("Menu fountain star", "Menu/fountain-star"),
            ("Mode osu!", "mode-osu"),
            ("Selection mode", "selection-mode"),
            ("Selection mods", "selection-mods"),
            ("Selection random", "selection-random"),
            ("Selection options", "selection-options"),
            ("Pause back", "pause-back"),
            ("Pause continue", "pause-continue"),
            ("Pause retry", "pause-retry")),
        fontCategory(),
        new StudioSkinCoverageCategory(
            "Audio samples",
            "Click any sample to audition it through lazer's real skinnable audio pipeline.",
            true,
            [
                sample("Normal hit", "normal-hitnormal", () => new HitSampleInfo(HitSampleInfo.HIT_NORMAL)),
                sample("Whistle", "normal-hitwhistle", () => new HitSampleInfo(HitSampleInfo.HIT_WHISTLE)),
                sample("Finish", "normal-hitfinish", () => new HitSampleInfo(HitSampleInfo.HIT_FINISH)),
                sample("Clap", "normal-hitclap", () => new HitSampleInfo(HitSampleInfo.HIT_CLAP)),
                sample("Soft hit", "soft-hitnormal", () => new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_SOFT)),
                sample("Soft whistle", "soft-hitwhistle", () => new HitSampleInfo(HitSampleInfo.HIT_WHISTLE, HitSampleInfo.BANK_SOFT)),
                sample("Soft finish", "soft-hitfinish", () => new HitSampleInfo(HitSampleInfo.HIT_FINISH, HitSampleInfo.BANK_SOFT)),
                sample("Soft clap", "soft-hitclap", () => new HitSampleInfo(HitSampleInfo.HIT_CLAP, HitSampleInfo.BANK_SOFT)),
                sample("Drum hit", "drum-hitnormal", () => new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_DRUM)),
                sample("Drum whistle", "drum-hitwhistle", () => new HitSampleInfo(HitSampleInfo.HIT_WHISTLE, HitSampleInfo.BANK_DRUM)),
                sample("Drum finish", "drum-hitfinish", () => new HitSampleInfo(HitSampleInfo.HIT_FINISH, HitSampleInfo.BANK_DRUM)),
                sample("Drum clap", "drum-hitclap", () => new HitSampleInfo(HitSampleInfo.HIT_CLAP, HitSampleInfo.BANK_DRUM)),
                sample("Normal slider tick", "normal-slidertick", () => new HitSampleInfo("slidertick", HitSampleInfo.BANK_NORMAL)),
                sample("Soft slider tick", "soft-slidertick", () => new HitSampleInfo("slidertick", HitSampleInfo.BANK_SOFT)),
                sample("Drum slider tick", "drum-slidertick", () => new HitSampleInfo("slidertick", HitSampleInfo.BANK_DRUM)),
                sample("Combo break", "combobreak", () => new SampleInfo("Gameplay/combobreak")),
                sample("Fail", "failsound", () => new SampleInfo("Gameplay/failsound")),
                sample("Pause loop", "pause-loop", () => new SampleInfo("Gameplay/pause-loop")),
                sample("Spinner spin", "spinnerspin", () => new SampleInfo("Gameplay/spinnerspin")),
                sample("Spinner bonus", "spinnerbonus", () => new SampleInfo("Gameplay/spinnerbonus")),
                sample("Countdown 1", "count1s", () => new SampleInfo("Gameplay/count1s")),
                sample("Countdown 2", "count2s", () => new SampleInfo("Gameplay/count2s")),
                sample("Countdown 3", "count3s", () => new SampleInfo("Gameplay/count3s")),
                sample("Countdown ready", "readys", () => new SampleInfo("Gameplay/readys")),
                sample("Countdown go", "gos", () => new SampleInfo("Gameplay/gos")),
                sample("Section pass", "sectionpass", () => new SampleInfo("Gameplay/sectionpass")),
                sample("Section fail", "sectionfail", () => new SampleInfo("Gameplay/sectionfail")),
                sample("Nightcore kick", "nightcore-kick", () => new SampleInfo("Gameplay/nightcore-kick")),
                sample("Nightcore clap", "nightcore-clap", () => new SampleInfo("Gameplay/nightcore-clap")),
                sample("Nightcore hat", "nightcore-hat", () => new SampleInfo("Gameplay/nightcore-hat")),
                sample("Nightcore finish", "nightcore-finish", () => new SampleInfo("Gameplay/nightcore-finish")),
                sample("Applause", "applause", () => new SampleInfo("Results/applause")),
                sample("See ya", "seeya", () => new SampleInfo("Outro/seeya")),
                sample("Welcome", "welcome", () => new SampleInfo("Intro/Welcome/welcome")),
            ]),
    ];

    public static IReadOnlyList<StudioSkinCoverageCategory> Categories { get; } =
        SkinStudioElementCatalog.Categories.Select(category =>
            new StudioSkinCoverageCategory(
                category.Title,
                category.Description,
                category.IsAudio,
                category.Elements.Select(element =>
                    new StudioSkinCoverageElement(
                        element.Label,
                        element.ComponentName,
                        element.IsAudio
                            ? sampleFactory(element.ComponentName)
                            : null)).ToArray())).ToArray();

    private static Func<ISampleInfo>? sampleFactory(string component) =>
        component.ToLowerInvariant() switch
        {
            "normal-hitnormal" => () => new HitSampleInfo(HitSampleInfo.HIT_NORMAL),
            "normal-hitwhistle" => () => new HitSampleInfo(HitSampleInfo.HIT_WHISTLE),
            "normal-hitfinish" => () => new HitSampleInfo(HitSampleInfo.HIT_FINISH),
            "normal-hitclap" => () => new HitSampleInfo(HitSampleInfo.HIT_CLAP),
            "soft-hitnormal" => () => new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_SOFT),
            "soft-hitwhistle" => () => new HitSampleInfo(HitSampleInfo.HIT_WHISTLE, HitSampleInfo.BANK_SOFT),
            "soft-hitfinish" => () => new HitSampleInfo(HitSampleInfo.HIT_FINISH, HitSampleInfo.BANK_SOFT),
            "soft-hitclap" => () => new HitSampleInfo(HitSampleInfo.HIT_CLAP, HitSampleInfo.BANK_SOFT),
            "drum-hitnormal" => () => new HitSampleInfo(HitSampleInfo.HIT_NORMAL, HitSampleInfo.BANK_DRUM),
            "drum-hitwhistle" => () => new HitSampleInfo(HitSampleInfo.HIT_WHISTLE, HitSampleInfo.BANK_DRUM),
            "drum-hitfinish" => () => new HitSampleInfo(HitSampleInfo.HIT_FINISH, HitSampleInfo.BANK_DRUM),
            "drum-hitclap" => () => new HitSampleInfo(HitSampleInfo.HIT_CLAP, HitSampleInfo.BANK_DRUM),
            "normal-slidertick" => () => new HitSampleInfo("slidertick", HitSampleInfo.BANK_NORMAL),
            "normal-sliderslide" => () => new HitSampleInfo("sliderslide", HitSampleInfo.BANK_NORMAL),
            "normal-sliderwhistle" => () => new HitSampleInfo("sliderwhistle", HitSampleInfo.BANK_NORMAL),
            "soft-slidertick" => () => new HitSampleInfo("slidertick", HitSampleInfo.BANK_SOFT),
            "soft-sliderslide" => () => new HitSampleInfo("sliderslide", HitSampleInfo.BANK_SOFT),
            "soft-sliderwhistle" => () => new HitSampleInfo("sliderwhistle", HitSampleInfo.BANK_SOFT),
            "drum-slidertick" => () => new HitSampleInfo("slidertick", HitSampleInfo.BANK_DRUM),
            "drum-sliderslide" => () => new HitSampleInfo("sliderslide", HitSampleInfo.BANK_DRUM),
            "drum-sliderwhistle" => () => new HitSampleInfo("sliderwhistle", HitSampleInfo.BANK_DRUM),
            "combobreak" => () => new SampleInfo("Gameplay/combobreak"),
            "failsound" => () => new SampleInfo("Gameplay/failsound"),
            "pause-loop" => () => new SampleInfo("Gameplay/pause-loop"),
            "spinnerspin" => () => new SampleInfo("Gameplay/spinnerspin"),
            "spinnerbonus" => () => new SampleInfo("Gameplay/spinnerbonus"),
            "spinnerbonus-max" => () => new SampleInfo("Gameplay/spinnerbonus-max"),
            "count1s" => () => new SampleInfo("Gameplay/count1s"),
            "count2s" => () => new SampleInfo("Gameplay/count2s"),
            "count3s" => () => new SampleInfo("Gameplay/count3s"),
            "readys" => () => new SampleInfo("Gameplay/readys"),
            "gos" => () => new SampleInfo("Gameplay/gos"),
            "sectionpass" => () => new SampleInfo("Gameplay/sectionpass"),
            "sectionfail" => () => new SampleInfo("Gameplay/sectionfail"),
            "nightcore-kick" => () => new SampleInfo("Gameplay/nightcore-kick"),
            "nightcore-clap" => () => new SampleInfo("Gameplay/nightcore-clap"),
            "nightcore-hat" => () => new SampleInfo("Gameplay/nightcore-hat"),
            "nightcore-finish" => () => new SampleInfo("Gameplay/nightcore-finish"),
            "applause" => () => new SampleInfo("Results/applause"),
            "applause-XH" or "applause-X" or "applause-SH" or "applause-S" =>
                () => new SampleInfo("Results/applause-s"),
            "applause-A" => () => new SampleInfo("Results/applause-a"),
            "applause-B" => () => new SampleInfo("Results/applause-b"),
            "applause-C" => () => new SampleInfo("Results/applause-c"),
            "applause-D" => () => new SampleInfo("Results/applause-d"),
            "seeya" => () => new SampleInfo("Outro/seeya"),
            "welcome" => () => new SampleInfo("Intro/Welcome/welcome"),
            "menuhit" => () => new SampleInfo("UI/menuhit"),
            "menuback" => () => new SampleInfo("UI/menuback"),
            "menu-play-click" => () => new SampleInfo("UI/menu-play-click"),
            "menu-back-click" => () => new SampleInfo("UI/menu-back-click"),
            "key-confirm" => () => new SampleInfo("UI/key-confirm"),
            "key-delete" => () => new SampleInfo("UI/key-delete"),
            "key-movement" => () => new SampleInfo("UI/key-movement"),
            "rank-up" => () => new SampleInfo("Gameplay/rank-up"),
            "rank-down" => () => new SampleInfo("Gameplay/rank-down"),
            _ => () => new SampleInfo(component),
        };

    private static StudioSkinCoverageCategory images(
        string title,
        string description,
        params (string Label, string Component)[] elements) =>
        new(
            title,
            description,
            false,
            elements.Select(element =>
                new StudioSkinCoverageElement(element.Label, element.Component, null)).ToArray());

    private static (string Label, string Component)[] fontElements(
        string prefix,
        string label) =>
        Enumerable.Range(0, 10)
            .Select(number => ($"{label} {number}", $"{prefix}-{number}"))
            .Concat(
            [
                ($"{label} comma", $"{prefix}-comma"),
                ($"{label} dot", $"{prefix}-dot"),
                ($"{label} percent", $"{prefix}-percent"),
                ($"{label} x", $"{prefix}-x"),
            ])
            .ToArray();

    private static StudioSkinCoverageCategory fontCategory()
    {
        var elements = new[]
            {
                fontElements("default", "Hit"),
                fontElements("score", "Score"),
                fontElements("combo", "Combo"),
                fontElements("scoreentry", "Entry"),
            }
            .SelectMany(group => group)
            .ToArray();
        return images(
            "Number fonts",
            "Hit-circle, score, combo, and score-entry glyph sets with configured fallbacks.",
            elements);
    }

    private static StudioSkinCoverageElement sample(
        string label,
        string component,
        Func<ISampleInfo> factory) =>
        new(label, component, factory);
}

internal sealed record StudioSkinCoverageCategory(
    string Title,
    string Description,
    bool IsAudio,
    IReadOnlyList<StudioSkinCoverageElement> Elements);

internal sealed record StudioSkinCoverageElement(
    string Label,
    string ComponentName,
    Func<ISampleInfo>? SampleFactory);
