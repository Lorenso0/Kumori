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

internal partial class StudioExtrasAudioBrowserOverlay : CompositeDrawable
{
    private readonly Action<SkinExtraPackDescriptor, string> open;
    private readonly OsuTextBox search;
    private readonly SpriteText summary;
    private readonly FillFlowContainer tracks;
    private SkinExtraPackDescriptor? pack;
    private IReadOnlyList<SkinExtraManifestFile> audioFiles = [];

    public StudioExtrasAudioBrowserOverlay(
        Action<SkinExtraPackDescriptor, string> open)
    {
        this.open = open;
        RelativeSizeAxes = Axes.Both;
        Depth = -98;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.78f),
            },
            new Container
            {
                Width = 820,
                Height = 700,
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
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 142,
                        Padding = new MarginPadding(26),
                        Child = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 8),
                            Children =
                            [
                                label("EXTRAS AUDIO BROWSER", 21, true),
                                summary = label("No Extras pack selected.", 12, false),
                                search = new OsuTextBox
                                {
                                    RelativeSizeAxes = Axes.X,
                                    PlaceholderText =
                                        "Search long tracks, hitsounds, or filenames",
                                },
                            ],
                        },
                    },
                    new OsuScrollContainer(Direction.Vertical)
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding
                        {
                            Top = 142,
                            Bottom = 74,
                            Horizontal = 26,
                        },
                        ScrollbarVisible = true,
                        Child = tracks = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 9),
                        },
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 74,
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        Padding = new MarginPadding(18),
                        Child = new StudioActionButton(
                            "Close audio browser",
                            Hide),
                    },
                ],
            },
        ];
        search.Current.BindValueChanged(_ => rebuild());
        Hide();
    }

    public void Present(SkinExtraPackDescriptor selectedPack)
    {
        ArgumentNullException.ThrowIfNull(selectedPack);
        pack = selectedPack;
        audioFiles = selectedPack.Manifest.Files
            .Where(file => SkinMediaTypes.IsAudio(file.TargetFilename))
            .OrderBy(
                file => file.TargetFilename,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        search.Current.Value = string.Empty;
        summary.Text =
            $"{selectedPack.Manifest.DisplayName} \u00b7 "
            + $"{audioFiles.Count} audio file(s) \u00b7 "
            + "select any entry for lazer's real track transport";
        rebuild();
        Show();
    }

    private void rebuild()
    {
        tracks.Clear();
        if (pack is null)
            return;
        var term = search.Current.Value.Trim();
        var visible = audioFiles.Where(file =>
            term.Length == 0
            || file.TargetFilename.Contains(
                term,
                StringComparison.OrdinalIgnoreCase)
            || file.LogicalSlot.Contains(
                term,
                StringComparison.OrdinalIgnoreCase)
            || pack.Manifest.FamilyName.Contains(
                term,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var file in visible)
        {
            tracks.Add(new StudioActionButton(
                $"{file.TargetFilename} \u00b7 {file.LogicalSlot}",
                () => open(pack, file.TargetFilename),
                accent: SkinExtraFamilyRegistry.ForFile(
                    file.TargetFilename)?.Id is "audio.applause"
                    or "audio.failsound"
                    or "audio.welcome"));
        }
        if (visible.Length == 0)
        {
            tracks.Add(label(
                audioFiles.Count == 0
                    ? "This Extras pack contains no audio files."
                    : "No audio files match this search.",
                13,
                false));
        }
    }

    internal int AcceptanceAudioFileCount => audioFiles.Count;

    internal int AcceptanceVisibleCount => tracks.Count;

    internal void OpenFirstAcceptanceTrack()
    {
        if (pack is null || audioFiles.Count == 0)
            throw new InvalidOperationException(
                "No Extras audio track is available.");
        open(pack, audioFiles[0].TargetFilename);
    }

    internal void SetAcceptanceSearch(string value) =>
        search.Current.Value = value;

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(
            size: size,
            weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
