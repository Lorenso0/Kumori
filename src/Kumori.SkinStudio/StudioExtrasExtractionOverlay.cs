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

internal partial class StudioExtrasExtractionOverlay : CompositeDrawable
{
    private readonly string extrasRoot;
    private readonly Action refreshLibrary;
    private readonly Action<string> report;
    private readonly SkinExtrasExtractionService extraction = new();
    private readonly FillFlowContainer familiesFlow;
    private readonly SpriteText summary;
    private readonly OsuTextBox packName;
    private readonly StudioActionButton extractButton;
    private readonly HashSet<string> selectedIds =
        new(StringComparer.OrdinalIgnoreCase);
    private SkinExtractionSource? source;
    private IReadOnlyList<SkinExtractionFamily> families = [];
    private bool lazerUsedOnly;

    public StudioExtrasExtractionOverlay(
        string extrasRoot,
        Action refreshLibrary,
        Action<string> report)
    {
        this.extrasRoot = extrasRoot;
        this.refreshLibrary = refreshLibrary;
        this.report = report;
        RelativeSizeAxes = Axes.Both;
        Depth = -94;
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
                    Horizontal = 92,
                    Vertical = 54,
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
                            Height = 154,
                            Padding = new MarginPadding(24),
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 8),
                                Children =
                                [
                                    label("EXTRACT DRAFT TO EXTRAS", 21, true),
                                    summary = label("No draft loaded", 11, false),
                                    packName = new OsuTextBox
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        PlaceholderText = "Optional pack name override",
                                        LengthLimit = 160,
                                    },
                                ],
                            },
                        },
                        new OsuScrollContainer(Direction.Vertical)
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding
                            {
                                Top = 154,
                                Bottom = 206,
                                Horizontal = 24,
                            },
                            ScrollbarVisible = true,
                            Child = familiesFlow = new FillFlowContainer
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
                            Height = 206,
                            Anchor = Anchor.BottomLeft,
                            Origin = Anchor.BottomLeft,
                            Padding = new MarginPadding(24),
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 8),
                                Children =
                                [
                                    extractButton = new StudioActionButton(
                                        "Extract selected families",
                                        extractSelected,
                                        accent: true,
                                        enabled: false),
                                    new StudioActionButton("Select all / none", toggleAll),
                                    new StudioActionButton("Include all / lazer-used only", toggleLazerUsed),
                                    new StudioActionButton("Cancel", Hide),
                                ],
                            },
                        },
                    ],
                },
            },
        ];
        Hide();
    }

    public void Present(
        SkinExtractionSource source,
        IEnumerable<string>? preselectedIds = null)
    {
        this.source = source;
        families = extraction.Analyze(source);
        selectedIds.Clear();
        if (preselectedIds is null)
        {
            foreach (var family in families)
                selectedIds.Add(family.SelectionId);
        }
        else
        {
            var requested = preselectedIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            foreach (var family in families.Where(family =>
                         requested.Contains(family.SelectionId)))
            {
                selectedIds.Add(family.SelectionId);
            }
        }
        packName.Current.Value = "";
        rebuild();
        Show();
    }

    internal static IReadOnlyList<string> SelectionIdsForComponents(
        IEnumerable<SkinExtractionFamily> families,
        IEnumerable<string> components)
    {
        var requested = components
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
            return [];
        return families
            .Where(family => family.Files.Any(file =>
                requested.Contains(
                    StudioSkinWorkbench.ComponentName(file.Filename))))
            .Select(family => family.SelectionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void rebuild()
    {
        familiesFlow.Clear();
        summary.Text = source is null
            ? "No draft loaded"
            : $"{source.DisplayName} · {families.Count} extractable family/families · {selectedIds.Count} selected";
        foreach (var group in families.GroupBy(family => family.Definition.Area))
        {
            familiesFlow.Add(label(group.Key.ToUpperInvariant(), 14, true));
            foreach (var family in group)
            {
                var button = new StudioActionButton(
                    $"{family.DisplayName} · {family.Files.Count} file(s) · {family.IniPatch.Count} setting(s)",
                    () => toggle(family.SelectionId));
                button.SetSelected(selectedIds.Contains(family.SelectionId));
                familiesFlow.Add(button);
            }
        }
        if (families.Count == 0)
            familiesFlow.Add(label("This draft has no extractable Extras families.", 13, false));
        extractButton.SetEnabled(selectedIds.Count > 0);
    }

    private void toggle(string selectionId)
    {
        if (!selectedIds.Add(selectionId))
            selectedIds.Remove(selectionId);
        rebuild();
    }

    private void toggleAll()
    {
        if (selectedIds.Count == families.Count)
            selectedIds.Clear();
        else
        {
            selectedIds.Clear();
            foreach (var family in families)
                selectedIds.Add(family.SelectionId);
        }
        rebuild();
    }

    private void toggleLazerUsed()
    {
        lazerUsedOnly = !lazerUsedOnly;
        report(lazerUsedOnly
            ? "Extras extraction will retain only resources used by pinned lazer."
            : "Extras extraction will retain every compatible legacy resource.");
    }

    private void extractSelected()
    {
        if (source is null)
            return;
        var selected = families
            .Where(family => selectedIds.Contains(family.SelectionId))
            .ToArray();
        if (selected.Length == 0)
        {
            report("Select at least one Extras family to extract.");
            return;
        }
        try
        {
            var results = extraction.Extract(
                source,
                selected,
                extrasRoot,
                string.IsNullOrWhiteSpace(packName.Current.Value)
                    ? null
                    : packName.Current.Value.Trim(),
                lazerUsedOnly);
            refreshLibrary();
            var extracted = results.Count(result =>
                result.Status == SkinExtraExtractionStatus.Extracted);
            var duplicates = results.Count - extracted;
            report(
                $"Extras extraction completed: {extracted} pack(s) created or refreshed, {duplicates} exact duplicate(s) skipped.");
            Hide();
        }
        catch (Exception ex)
        {
            report($"Extras extraction failed: {ex.Message}");
        }
    }

    internal int AcceptanceFamilyCount => families.Count;

    internal int AcceptanceSelectedCount => selectedIds.Count;

    internal void ToggleAcceptanceLazerUsedOnly() => toggleLazerUsed();

    internal void ExtractAcceptanceSelection() => extractSelected();

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
