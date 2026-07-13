using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Input.Events;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Input;

namespace Kumori.ReplayViewer;

/// <summary>
/// A second native lazer replay-settings overlay dedicated to comparison.
/// It retains lazer's built-in collapse handle and expansion behaviour.
/// </summary>
internal partial class KumoriComparisonOverlay : CompositeDrawable
{
    private const float sidebar_width = ReplaySettingsOverlay.EXPANDED_WIDTH;
    private const float bottom_clearance = KumoriSeekBar.ReservedBottomHeight;

    private readonly KumoriViewerConfig config;
    private readonly IReadOnlyList<ComparisonContract> options;
    private readonly long? selectedAttemptId;
    private readonly Action enter;
    private readonly Action exit;
    private readonly Action<ComparisonContract> select;
    private readonly Action chooseOsr;
    private readonly IBindable<string> importStatus;
    private readonly Action stop;
    private readonly Func<KumoriReplayPlayer?> player;
    private StickyReplaySettingsOverlay settings = null!;
    private bool isOpen;

    public KumoriComparisonOverlay(
        KumoriViewerConfig config,
        IReadOnlyList<ComparisonContract> options,
        long? selectedAttemptId,
        Action enter,
        Action exit,
        Action<ComparisonContract> select,
        Action chooseOsr,
        IBindable<string> importStatus,
        Action stop,
        Func<KumoriReplayPlayer?> player)
    {
        this.config = config;
        this.options = options;
        this.selectedAttemptId = selectedAttemptId;
        this.enter = enter;
        this.exit = exit;
        this.select = select;
        this.chooseOsr = chooseOsr;
        this.importStatus = importStatus;
        this.stop = stop;
        this.player = player;
        RelativeSizeAxes = Axes.Both;
        Alpha = 0;
        Depth = -100;
    }

    public override bool HandlePositionalInput => isOpen;
    public override bool HandleNonPositionalInput => isOpen;

    [BackgroundDependencyLoader]
    private void load()
    {
        settings = new StickyReplaySettingsOverlay
        {
            RelativeSizeAxes = Axes.Y,
        };
        settings.RemoveAll(drawable => drawable is PlayerSettingsGroup, true);
        settings.Add(new KumoriComparisonPlaybackSettings(player));
        settings.Add(new KumoriComparisonPanel(
            config,
            options,
            selectedAttemptId,
            option =>
            {
                settings.ReleaseExternalOpenHold();
                settings.Expanded.Value = false;
                select(option);
            },
            chooseOsr,
            importStatus,
            () =>
            {
                settings.ReleaseExternalOpenHold();
                settings.Expanded.Value = false;
                stop();
            },
            collapseSettings));
        settings.Add(new KumoriAudioSettings(config));

        InternalChild = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Bottom = bottom_clearance },
            Child = new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = settings,
            },
        };
    }

    public void Open()
    {
        if (!isOpen)
        {
            isOpen = true;
            enter();
            this.FadeIn(160, Easing.OutQuint);
        }

        settings.ExpandFromExternalAction();
    }

    /// <summary>Restores the native comparison handle after a comparison reload.</summary>
    public void ActivateCollapsed()
    {
        if (isOpen)
            return;

        isOpen = true;
        enter();
        settings.ReleaseExternalOpenHold();
        settings.Expanded.Value = false;
        this.FadeIn(160, Easing.OutQuint);
    }

    public void Close()
    {
        if (!isOpen)
            return;

        settings.ReleaseExternalOpenHold();
        isOpen = false;
        exit();
        this.FadeOut(140, Easing.OutQuint);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Key != Key.Escape)
            return base.OnKeyDown(e);

        if (selectedAttemptId is not null)
            collapseSettings();
        else
            Close();
        return true;
    }

    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
        => isOpen
           && Alpha > 0.01f
           && screenSpacePos.Y < ToScreenSpace(new Vector2(0, DrawHeight - bottom_clearance)).Y
           && screenSpacePos.X > ToScreenSpace(new Vector2(DrawWidth - sidebar_width, 0)).X
           && base.ReceivePositionalInputAt(screenSpacePos);

    protected override bool OnClick(ClickEvent e) => isOpen;

    private void collapseSettings()
    {
        settings.ReleaseExternalOpenHold();
        settings.Expanded.Value = false;
    }

    /// <summary>
    /// lazer normally closes a replay sidebar as soon as the pointer is not
    /// over its own handle. Comparison is opened by a button in a different
    /// overlay, so retain the first expansion until this sidebar is hovered.
    /// </summary>
    private partial class StickyReplaySettingsOverlay : ReplaySettingsOverlay
    {
        private bool holdExternalOpen;
        private double releaseAfter;

        public void ExpandFromExternalAction()
        {
            holdExternalOpen = true;
            releaseAfter = Time.Current + 150;
            Expanded.Value = true;
        }

        public void ReleaseExternalOpenHold() => holdExternalOpen = false;

        protected override void Update()
        {
            base.Update();
            if (!holdExternalOpen)
                return;

            Expanded.Value = true;
            if (Time.Current >= releaseAfter && IsHovered)
                holdExternalOpen = false;
        }
    }
}
