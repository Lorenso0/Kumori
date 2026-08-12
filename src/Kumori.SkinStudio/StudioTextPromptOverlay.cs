using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioTextPromptOverlay : CompositeDrawable
{
    private readonly SpriteText title;
    private readonly SpriteText prompt;
    private readonly SpriteText validation;
    private readonly OsuTextBox value;
    private Func<string, bool>? commit;

    public StudioTextPromptOverlay()
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -20;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.78f),
            },
            new Container
            {
                Width = 520,
                Height = 310,
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
                            title = label("", 20, true),
                            prompt = label("", 12, false),
                            value = new OsuTextBox
                            {
                                RelativeSizeAxes = Axes.X,
                                LengthLimit = 1024,
                            },
                            validation = new SpriteText
                            {
                                Text = "",
                                Font = FontUsage.Default.With(size: 11),
                                Colour = Colour4.FromHex("#FF8EAF"),
                            },
                            new StudioActionButton("Apply", save, accent: true),
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
        string instruction,
        string initialValue,
        Func<string, bool> commit)
    {
        title.Text = heading.ToUpperInvariant();
        prompt.Text = instruction;
        value.Current.Value = initialValue;
        validation.Text = "";
        this.commit = commit;
        Show();
    }

    private void save()
    {
        var entered = value.Current.Value.Trim();
        if (string.IsNullOrWhiteSpace(entered))
        {
            validation.Text = "Enter a value.";
            return;
        }
        try
        {
            validation.Text = "";
            if (commit?.Invoke(entered) == true)
                Hide();
        }
        catch (Exception ex)
        {
            validation.Text = ex.Message;
        }
    }

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.FromHex("#FFB7D5") : Colour4.FromHex("#C6A8BA"),
    };
}
