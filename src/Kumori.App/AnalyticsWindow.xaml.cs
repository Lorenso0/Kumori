using System.Windows;
using Kumori.Native;
using Kumori.Storage;

namespace Kumori.App;

public partial class AnalyticsWindow : Window
{
    private readonly AnalyticsRepository _repository;

    public AnalyticsWindow(AnalyticsRepository repository)
    {
        _repository = repository;
        InitializeComponent();
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (Content is FrameworkElement content)
        {
            content.Loaded += async (_, _) => await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var summary = await Task.Run(() => _repository.GetSummary());
            SummaryText.Text =
                $"Attempts: {summary.Attempts:N0}   Completed: {summary.Completed:N0}   Failed: {summary.Failed:N0}\n" +
                $"Average completed accuracy: {summary.AverageAccuracy:0.00}%   Best PP: {summary.BestPp:0.##}   Total score: {summary.TotalScore:N0}";
            DailyList.ItemsSource = summary.Daily;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load analytics: {ex.Message}";
        }
    }
}
