using System.Windows;

namespace Kumori.App;

public partial class ShutdownStatusWindow : Window
{
    private const double WorkAreaMargin = 16;

    public ShutdownStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionNearNotificationArea();
    }

    public void UpdateStatus(string status) => StatusMessage.Text = status;

    private void PositionNearNotificationArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - WorkAreaMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - WorkAreaMargin);
    }
}
