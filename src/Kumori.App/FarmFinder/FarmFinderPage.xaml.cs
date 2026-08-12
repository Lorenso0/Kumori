using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Kumori.App.ViewModels;
using Kumori.FarmFinder;

namespace Kumori.App.FarmFinder;

public partial class FarmFinderPage : UserControl
{
    private readonly FarmFinderViewModel viewModel;

    public FarmFinderPage(FarmFinderViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ConfirmUpdateAsync = ConfirmUpdateAsync;
        viewModel.ConfirmMetadataRepairAsync = ConfirmMetadataRepairAsync;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Unloaded += (_, _) =>
        {
            viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        };
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        viewModel.ClientSecret = SecretBox.Password;

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
    }

    private void ApiSetupMenuItem_Click(object sender, RoutedEventArgs e) =>
        ApiSetupPopup.IsOpen = true;

    private void ResultsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = (sender as TextBox)?.Text.Trim();
        var resultsView = CollectionViewSource.GetDefaultView(viewModel.Results);
        if (string.IsNullOrWhiteSpace(query))
        {
            resultsView.Filter = null;
            return;
        }

        resultsView.Filter = item =>
            item is FarmMapResult result &&
            (Contains(result.Beatmap.Title, query) ||
             Contains(result.Beatmap.Artist, query) ||
             Contains(result.Beatmap.Mapper, query) ||
             Contains(result.Beatmap.Difficulty, query) ||
             Contains(result.NormalizedMods, query));
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private Task<bool> ConfirmUpdateAsync(int minimumRank, int maximumRank)
    {
        var playerCount = maximumRank - minimumRank + 1;
        var usesCountryScan = maximumRank > 10_000;
        var estimatedMinutes = Math.Max(1, (int)Math.Ceiling(playerCount / 8d / 60d));
        var discovery = usesCountryScan
            ? "\n\nRanks beyond 10,000 are discovered by merging official country " +
              "leaderboards. Countries whose own top-10,000 limit creates a gap " +
              "will be identified in the coverage warning."
            : "";
        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"Build the local Farm Finder index for #{minimumRank:N0}–#{maximumRank:N0} " +
            $"({playerCount:N0} possible ranks)?\n\n" +
            "Missing or stale top scores will come from Hinamizawa at a safe, paced rate. " +
            $"A completely empty cache is estimated at about {estimatedMinutes:N0} minutes; " +
            "cached players are skipped." +
            discovery + "\n\n" +
            "Progress is shown live. You can cancel and resume without losing completed players.",
            "Build Farm Finder index",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    private Task<bool> ConfirmMetadataRepairAsync(
        FarmScoreMetadataRepairStatus status)
    {
        var estimatedMinutes = Math.Max(
            1,
            (int)Math.Ceiling(status.PendingPlayers / 500d));
        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"Refresh top scores for {status.PendingPlayers:N0} cached players?\n\n" +
            "This fills the new stable/lazer origin, Classic, legacy score ID, " +
            "score-total, and client-build fields. Existing rows are replaced only " +
            "after each player's response is downloaded successfully.\n\n" +
            $"At the current provider limit this may take about {estimatedMinutes:N0} minutes. " +
            "You can cancel at any time and run this action again to resume.",
            "Repair Farm Finder score data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FarmFinderViewModel.ClientSecret) &&
            string.IsNullOrEmpty(viewModel.ClientSecret) &&
            SecretBox.Password.Length != 0)
            SecretBox.Clear();
    }
}
