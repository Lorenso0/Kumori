using System.Text;
using System.Windows;
using System.Windows.Interop;
using Kumori.Native;
using Kumori.Tracking;

namespace Kumori.App;

public partial class LazerStorageDiagnosticsWindow : Window
{
    public LazerStorageDiagnosticsWindow(LazerStorageDiagnostics status)
    {
        InitializeComponent();
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
        var text = new StringBuilder();
        text.AppendLine("osu!lazer direct-storage probe");
        text.AppendLine();
        text.AppendLine($"Default root:    {status.DefaultRoot}");
        text.AppendLine($"storage.ini root:{status.ConfiguredRoot ?? "(none)"}");
        text.AppendLine($"Resolved root:   {status.ResolvedRoot ?? "(not found)"}");
        text.AppendLine($"client.realm:    {(status.RealmExists ? "found" : "missing")}");
        text.AppendLine($"files directory: {(status.FilesDirectoryExists ? "found" : "missing")}");
        text.AppendLine($"Realm open:      {(status.RealmOpened ? "success" : "failed")}");
        if (!string.IsNullOrWhiteSpace(status.Error)) text.AppendLine($"Error:           {status.Error}");
        StatusText.Text = text.ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
