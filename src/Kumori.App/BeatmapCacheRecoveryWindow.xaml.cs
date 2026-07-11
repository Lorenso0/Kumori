using System.Windows;
using System.Windows.Interop;
using Kumori.Native;
using Kumori.Tracking;

namespace Kumori.App;

public partial class BeatmapCacheRecoveryWindow : Window
{
    private readonly int _total;

    public BeatmapCacheRecoveryWindow(int total)
    {
        InitializeComponent();
        _total = total;
        DetailText.Text = "Checking your historical maps in osu!lazer and downloading anything that is missing.";
        CountText.Text = $"0 of {_total} maps prepared";
        SourceInitialized += (_, _) => DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    public void Report(HistoricalMapRecoveryProgress progress)
    {
        Progress.Maximum = progress.Total;
        Progress.Value = progress.Current;
        DetailText.Text = progress.Succeeded
            ? $"Prepared: {progress.MapName}"
            : $"Could not prepare: {progress.MapName}";
        CountText.Text = $"{progress.Current} of {progress.Total} maps prepared";
    }
}
