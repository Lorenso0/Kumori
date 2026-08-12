using Kumori.Skins;
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

internal sealed partial class StudioOpeningSkinOverlay : CompositeDrawable
{
    internal const float HeaderHeight = 184;
    internal const float FooterHeight = 82;

    private readonly Action<Guid> openDraft;
    private readonly Action<Guid> openInstalled;
    private readonly Action importPackage;
    private readonly Action createBlank;
    private readonly Action createFromExtras;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer choices;
    private readonly StudioActionButton cancelButton;
    private IReadOnlyList<SkinDraftManifest> drafts = [];
    private IReadOnlyList<LazerSkinInfo> installed = [];
    private bool installedLoading;
    private string? installedError;

    public StudioOpeningSkinOverlay(
        Action<Guid> openDraft,
        Action<Guid> openInstalled,
        Action importPackage,
        Action createBlank,
        Action createFromExtras)
    {
        this.openDraft = openDraft;
        this.openInstalled = openInstalled;
        this.importPackage = importPackage;
        this.createBlank = createBlank;
        this.createFromExtras = createFromExtras;
        RelativeSizeAxes = Axes.Both;
        Depth = -110;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.84f),
            },
            new Container
            {
                Width = 960,
                RelativeSizeAxes = Axes.Y,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Padding = new MarginPadding { Vertical = 54 },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 14,
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
                            Height = HeaderHeight,
                            Padding = new MarginPadding(26),
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 9),
                                Children =
                                [
                                    label("WHICH SKIN DO YOU WANT TO EDIT?", 23, true),
                                    label(
                                        "Kumori creates an isolated draft. Your installed and source skins remain untouched.",
                                        12,
                                        false),
                                    search = new OsuTextBox
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 38,
                                        PlaceholderText = "Search recent drafts or installed lazer skins",
                                    },
                                ],
                            },
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding
                            {
                                Top = HeaderHeight,
                                Bottom = FooterHeight,
                                Horizontal = 26,
                            },
                            Child = new OsuScrollContainer(Direction.Vertical)
                            {
                                RelativeSizeAxes = Axes.Both,
                                ScrollbarVisible = true,
                                Child = choices = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new Vector2(0, 8),
                                },
                            },
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = FooterHeight,
                            Anchor = Anchor.BottomLeft,
                            Origin = Anchor.BottomLeft,
                            Padding = new MarginPadding
                            {
                                Horizontal = 26,
                                Vertical = 15,
                            },
                            Child = cancelButton =
                                new StudioActionButton("Keep current skin", Hide),
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
        IReadOnlyList<LazerSkinInfo>? installed,
        bool installedLoading,
        string? installedError,
        bool required)
    {
        this.drafts = drafts;
        this.installed = installed ?? [];
        this.installedLoading = installedLoading;
        this.installedError = installedError;
        cancelButton.Alpha = required ? 0 : 1;
        cancelButton.SetEnabled(!required);
        search.Current.Value = "";
        rebuild();
        Show();
    }

    public void UpdateInstalled(
        IReadOnlyList<LazerSkinInfo> skins,
        string? error = null)
    {
        installed = skins;
        installedLoading = false;
        installedError = error;
        rebuild();
    }

    internal static (
        IReadOnlyList<SkinDraftManifest> Drafts,
        IReadOnlyList<LazerSkinInfo> Installed) Filter(
        IEnumerable<SkinDraftManifest> drafts,
        IEnumerable<LazerSkinInfo> installed,
        string? query)
    {
        var term = query?.Trim();
        var filteredDrafts = StudioDraftBrowserOverlay.FilterDrafts(
            drafts,
            term);
        var filteredInstalled = StudioInstalledSkinBrowserOverlay.Filter(
            installed,
            term);
        return (filteredDrafts, filteredInstalled);
    }

    private void rebuild()
    {
        choices.Clear();
        choices.Add(label("START", 12, true));
        choices.Add(new StudioActionButton(
            "Import an .osk or .zip skin…",
            () =>
            {
                Hide();
                importPackage();
            },
            accent: true));
        choices.Add(new StudioActionButton(
            "Create a blank skin",
            () =>
            {
                Hide();
                createBlank();
            }));
        choices.Add(new StudioActionButton(
            "Create a skin from Extras",
            () =>
            {
                Hide();
                createFromExtras();
            }));

        var filtered = Filter(drafts, installed, search.Current.Value);
        choices.Add(label("RECENT KUMORI DRAFTS", 12, true));
        foreach (var draft in filtered.Drafts)
        {
            choices.Add(new StudioActionButton(
                $"{draft.Name}  ·  {draft.Creator}  ·  {draft.UpdatedAt.LocalDateTime:g}",
                () =>
                {
                    Hide();
                    openDraft(draft.DraftId);
                }));
        }
        if (filtered.Drafts.Count == 0)
            choices.Add(label("No matching drafts.", 11, false));

        choices.Add(label("INSTALLED OSU!LAZER SKINS", 12, true));
        if (installedLoading)
        {
            choices.Add(label("Reading the lazer skin catalog…", 11, false));
        }
        else if (!string.IsNullOrWhiteSpace(installedError))
        {
            choices.Add(label(installedError, 11, false));
        }
        else
        {
            foreach (var skin in filtered.Installed)
            {
                choices.Add(new StudioActionButton(
                    $"{skin.DisplayName}  ·  {skin.Files.Count} file(s)",
                    () =>
                    {
                        Hide();
                        openInstalled(skin.Id);
                    }));
            }
            if (filtered.Installed.Count == 0)
                choices.Add(label("No matching installed lazer skins.", 11, false));
        }
    }

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Margin = new MarginPadding { Top = bold ? 9 : 0, Left = 2 },
        Font = FontUsage.Default.With(
            size: size,
            weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.FromHex("#FFB7D5") : Colour4.FromHex("#C6A8BA"),
    };
}
