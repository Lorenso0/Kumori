using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioIdentityOverlay : CompositeDrawable
{
    private readonly SpriteText title;
    private readonly SpriteText validation;
    private readonly OsuTextBox name;
    private readonly OsuTextBox creator;
    private Func<string, string, bool>? commit;

    public StudioIdentityOverlay()
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -100;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.72f),
            },
            new Container
            {
                Width = 520,
                Height = 390,
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
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding(28),
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 12),
                        Children =
                        [
                            title = new SpriteText
                            {
                                Text = "SKIN IDENTITY",
                                Font = FontUsage.Default.With(size: 20, weight: "Bold"),
                                Colour = Colour4.FromHex("#FFB7D5"),
                            },
                            new SpriteText
                            {
                                Text = "Skin name",
                                Font = FontUsage.Default.With(size: 12, weight: "SemiBold"),
                                Colour = Colour4.FromHex("#C6A8BA"),
                            },
                            name = new OsuTextBox
                            {
                                RelativeSizeAxes = Axes.X,
                                PlaceholderText = "Skin name",
                                LengthLimit = 160,
                            },
                            new SpriteText
                            {
                                Text = "Author",
                                Font = FontUsage.Default.With(size: 12, weight: "SemiBold"),
                                Colour = Colour4.FromHex("#C6A8BA"),
                            },
                            creator = new OsuTextBox
                            {
                                RelativeSizeAxes = Axes.X,
                                PlaceholderText = "Author",
                                LengthLimit = 160,
                            },
                            validation = new SpriteText
                            {
                                Text = "",
                                Font = FontUsage.Default.With(size: 11),
                                Colour = Colour4.FromHex("#FF8EAF"),
                            },
                            new StudioActionButton("Save identity", save, accent: true),
                            new StudioActionButton("Cancel", Hide),
                        ],
                    },
                ],
            },
        ];
        Hide();
    }

    public void Present(
        string heading,
        string initialName,
        string initialCreator,
        Func<string, string, bool> commit)
    {
        title.Text = heading.ToUpperInvariant();
        name.Current.Value = initialName;
        creator.Current.Value = initialCreator;
        validation.Text = "";
        this.commit = commit;
        Show();
    }

    private void save()
    {
        var skinName = name.Current.Value.Trim();
        if (string.IsNullOrWhiteSpace(skinName))
        {
            validation.Text = "Enter a skin name.";
            return;
        }
        if (commit?.Invoke(skinName, creator.Current.Value.Trim()) == true)
            Hide();
    }
}
