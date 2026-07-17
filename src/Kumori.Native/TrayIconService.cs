using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Kumori.Native;

/// <summary>
/// Tray icon via WinForms NotifyIcon interop (no extra NuGet dependency).
/// Owned by the WPF app; all callbacks are raised on the UI thread because
/// NotifyIcon uses the message pump of the thread that created it.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _dualModeToggleItem;
    private readonly ToolStripMenuItem _dualModeAutoSwitchItem;
    private readonly ToolStripSeparator _dualModeSeparator;
    private readonly ToolStripMenuItem _endSessionItem;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? LogsRequested;
    public event Action? EndSessionRequested;
    public event Action? ExitRequested;
    public event Action? RestoreDualModeRequested;
    public event Action? KeepDualModeRequested;
    public event Action? DualModeToggleRequested;
    public event Action? DualModeAutoSwitchToggleRequested;
    public event Action? UpdateRequested;

    public TrayIconService(string tooltip, string? iconPath = null)
    {
        _icon = new NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            Icon = LoadIcon(iconPath),
            Visible = true,
        };

        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Tracking starting...")
        {
            Enabled = false,
        };
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _dualModeToggleItem = new ToolStripMenuItem("Toggle LG dual mode") { Visible = false };
        _dualModeToggleItem.Click += (_, _) => DualModeToggleRequested?.Invoke();
        _menu.Items.Add(_dualModeToggleItem);
        _dualModeAutoSwitchItem = new ToolStripMenuItem("Switch LG dual mode automatically")
        {
            Visible = false,
        };
        _dualModeAutoSwitchItem.Click += (_, _) => DualModeAutoSwitchToggleRequested?.Invoke();
        _menu.Items.Add(_dualModeAutoSwitchItem);
        _dualModeSeparator = new ToolStripSeparator { Visible = false };
        _menu.Items.Add(_dualModeSeparator);
        _menu.Items.Add("Open Kumori", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add("Logs", null, (_, _) => LogsRequested?.Invoke());
        _endSessionItem = new ToolStripMenuItem("End Session") { Enabled = false };
        _endSessionItem.Click += (_, _) => EndSessionRequested?.Invoke();
        _menu.Items.Add(_endSessionItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        ToastNotificationManagerCompat.OnActivated += args => OnToastActivated(args.Argument);
    }

    public void ShowUpdateNotification(string version)
    {
        new ToastContentBuilder()
            .AddText("Kumori update available")
            .AddText($"Version {version} is ready on GitHub.")
            .AddButton("Open release", ToastActivationType.Foreground, "kumoriAction=openUpdate")
            .Show();
    }

    public void SetDualModeControls(bool compatibleMonitorConnected, bool autoSwitchEnabled)
    {
        _dualModeToggleItem.Visible = compatibleMonitorConnected;
        _dualModeToggleItem.Enabled = compatibleMonitorConnected;
        _dualModeAutoSwitchItem.Visible = compatibleMonitorConnected;
        _dualModeAutoSwitchItem.Enabled = compatibleMonitorConnected;
        _dualModeAutoSwitchItem.Checked = compatibleMonitorConnected && autoSwitchEnabled;
        _dualModeSeparator.Visible = compatibleMonitorConnected;
    }

    public void SetEndSessionEnabled(bool enabled) => _endSessionItem.Enabled = enabled;

    public void SetTheme(TrayMenuTheme theme)
    {
        _menu.Renderer = new TrayMenuRenderer(theme);
        _menu.BackColor = theme.Background;
        _menu.ForeColor = theme.Text;
        ApplyItemTheme(_menu.Items, theme);
        _menu.Invalidate();
    }

    /// <summary>Shows a Windows toast with actions embedded in the notification itself.</summary>
    public void ShowDualModeRestoreNotification()
    {
        new ToastContentBuilder()
            .AddText("osu! closed")
            .AddText("Restore the LG monitor's previous display mode?")
            .AddButton("Yes, restore", ToastActivationType.Foreground, "kumoriAction=restoreDualMode")
            .AddButton("No, keep dual mode", ToastActivationType.Foreground, "kumoriAction=keepDualMode")
            .Show();
    }

    public void UpdateStatus(string status)
    {
        var text = string.IsNullOrWhiteSpace(status) ? "Kumori" : status;
        _statusItem.Text = text.Length > 80 ? text[..77] + "..." : text;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static Icon LoadIcon(string? iconPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch
        {
            // fall through to default
        }
        return SystemIcons.Application;
    }

    private static void ApplyItemTheme(ToolStripItemCollection items, TrayMenuTheme theme)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = theme.Background;
            item.ForeColor = item.Enabled ? theme.Text : theme.DisabledText;
            if (item is ToolStripDropDownItem dropDown)
            {
                dropDown.DropDown.BackColor = theme.Background;
                dropDown.DropDown.ForeColor = theme.Text;
                dropDown.DropDown.Renderer = new TrayMenuRenderer(theme);
                ApplyItemTheme(dropDown.DropDownItems, theme);
            }
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void OnToastActivated(string arguments)
    {
        switch (arguments)
        {
            case "kumoriAction=restoreDualMode":
                RestoreDualModeRequested?.Invoke();
                break;
            case "kumoriAction=keepDualMode":
                KeepDualModeRequested?.Invoke();
                break;
            case "kumoriAction=openUpdate":
                UpdateRequested?.Invoke();
                break;
        }
    }
}

public sealed record TrayMenuTheme(
    Color Background,
    Color HoverBackground,
    Color Text,
    Color DisabledText,
    Color Accent,
    Color Separator);

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly TrayMenuTheme theme;

    public TrayMenuRenderer(TrayMenuTheme theme)
        : base(new TrayMenuColorTable(theme))
    {
        this.theme = theme;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? theme.Text : theme.DisabledText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        using var background = new SolidBrush(
            e.Item.Selected && e.Item.Enabled
                ? theme.HoverBackground
                : theme.Background);
        e.Graphics.FillRectangle(background, bounds);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        const int boxSize = 12;
        var box = new Rectangle(
            e.ImageRectangle.Left + ((e.ImageRectangle.Width - boxSize) / 2),
            e.ImageRectangle.Top + ((e.ImageRectangle.Height - boxSize) / 2),
            boxSize,
            boxSize);

        using var fill = new SolidBrush(theme.Accent);
        e.Graphics.FillRectangle(fill, box);

        var oldSmoothingMode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var check = new Pen(theme.Text, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(
            check,
            [
                new PointF(box.Left + 2, box.Top + 6),
                new PointF(box.Left + 5, box.Top + 9),
                new PointF(box.Left + 10, box.Top + 3),
            ]);
        e.Graphics.SmoothingMode = oldSmoothingMode;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(theme.Separator);
        var y = e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2);
        e.Graphics.DrawLine(
            pen,
            e.Item.ContentRectangle.Left + 4,
            y,
            e.Item.ContentRectangle.Right - 4,
            y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(theme.Separator);
        var bounds = new Rectangle(
            e.AffectedBounds.X,
            e.AffectedBounds.Y,
            Math.Max(0, e.AffectedBounds.Width - 1),
            Math.Max(0, e.AffectedBounds.Height - 1));
        e.Graphics.DrawRectangle(pen, bounds);
    }
}

internal sealed class TrayMenuColorTable(TrayMenuTheme theme) : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => theme.Background;
    public override Color ImageMarginGradientBegin => theme.Background;
    public override Color ImageMarginGradientMiddle => theme.Background;
    public override Color ImageMarginGradientEnd => theme.Background;
    public override Color MenuBorder => theme.Separator;
    public override Color MenuItemBorder => theme.HoverBackground;
    public override Color MenuItemSelected => theme.HoverBackground;
    public override Color MenuItemSelectedGradientBegin => theme.HoverBackground;
    public override Color MenuItemSelectedGradientEnd => theme.HoverBackground;
    public override Color MenuItemPressedGradientBegin => theme.HoverBackground;
    public override Color MenuItemPressedGradientMiddle => theme.HoverBackground;
    public override Color MenuItemPressedGradientEnd => theme.HoverBackground;
    public override Color CheckBackground => theme.Accent;
    public override Color CheckSelectedBackground => theme.Accent;
    public override Color CheckPressedBackground => theme.Accent;
    public override Color SeparatorDark => theme.Separator;
    public override Color SeparatorLight => theme.Separator;
}
