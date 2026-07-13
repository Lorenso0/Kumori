using System.Windows;
using System.Windows.Interop;
using Kumori.Native;

namespace Kumori.App;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
        try
        {
            ReleaseList.ItemsSource = ChangelogService.LoadBundled();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"The bundled changelog could not be opened.\n\n{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }
}
