using System.Drawing;
using System.Windows.Forms;

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

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? LogsRequested;
    public event Action? EndSessionRequested;
    public event Action? ExitRequested;

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
        menu.Items.Add("Open Kumori", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("Logs", null, (_, _) => LogsRequested?.Invoke());
        menu.Items.Add("End Session", null, (_, _) => EndSessionRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void ShowNotification(string title, string message)
        => _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.None);

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
}
