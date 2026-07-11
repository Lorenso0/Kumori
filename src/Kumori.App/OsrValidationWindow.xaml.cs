using System.Windows;
using System.Windows.Interop;
using Kumori.Native;

namespace Kumori.App;

public partial class OsrValidationWindow : Window
{
    public OsrValidationWindow(OsrValidationResult result)
    {
        InitializeComponent();
        DataContext = result;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
