using System.Windows;

namespace Kumori.App;

public partial class ShutdownStatusWindow : Window
{
    public ShutdownStatusWindow()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string status) => StatusMessage.Text = status;
}
