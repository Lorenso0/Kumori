using System.Windows;

namespace Kumori.App;

internal static class PlayerNamePrompt
{
    public static string? Show(Window? owner, string defaultValue = "")
    {
        string value = KumoriDialog.Input(
            owner,
            "Kumori did not capture a player name for this older play.\n\n" +
            "Enter the name to display as “Shared by” in the exported file.",
            "Player name",
            defaultValue).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length <= 80)
            return value;
        KumoriDialog.Show(
            owner,
            "Player names can contain at most 80 characters.",
            "Player name",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return null;
    }
}
