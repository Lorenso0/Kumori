using System.Drawing;
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
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _dualModeToggleItem;
    private readonly ToolStripMenuItem _endSessionItem;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? LogsRequested;
    public event Action? EndSessionRequested;
    public event Action? ExitRequested;
    public event Action? RestoreDualModeRequested;
    public event Action? KeepDualModeRequested;
    public event Action? DualModeToggleRequested;
    public event Action? UpdateRequested;

    public TrayIconService(string tooltip, string? iconPath = null)
    {
        _icon = new NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            Icon = LoadIcon(iconPath),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Tracking starting...")
        {
            Enabled = false,
        };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        _dualModeToggleItem = new ToolStripMenuItem("Toggle LG dual mode") { Enabled = false };
        _dualModeToggleItem.Click += (_, _) => DualModeToggleRequested?.Invoke();
        menu.Items.Add(_dualModeToggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Kumori", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("Logs", null, (_, _) => LogsRequested?.Invoke());
        _endSessionItem = new ToolStripMenuItem("End Session") { Enabled = false };
        _endSessionItem.Click += (_, _) => EndSessionRequested?.Invoke();
        menu.Items.Add(_endSessionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        ToastNotificationManagerCompat.OnActivated += args => OnToastActivated(args.Argument);
    }

    public void ShowNotification(string title, string message)
        => _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.None);

    public void ShowUpdateNotification(string version)
    {
        new ToastContentBuilder()
            .AddText("Kumori update available")
            .AddText($"Version {version} is ready on GitHub.")
            .AddButton("Open release", ToastActivationType.Foreground, "kumoriAction=openUpdate")
            .Show();
    }

    public void SetDualModeToggleEnabled(bool enabled) => _dualModeToggleItem.Enabled = enabled;

    public void SetEndSessionEnabled(bool enabled) => _endSessionItem.Enabled = enabled;

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
