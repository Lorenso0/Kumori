using System.Windows;

namespace Kumori.App;

public static class KumoriDialog
{
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title = "Kumori",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = Create(owner, message, title, buttons, image, ResolveDefault(buttons, defaultResult));
        dialog.ShowDialog();
        return dialog.Result;
    }

    public static MessageBoxResult Show(
        string message,
        string title = "Kumori",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(ActiveOwner(), message, title, buttons, image, defaultResult);

    public static bool Confirm(Window? owner, string message, string title = "Kumori", MessageBoxImage image = MessageBoxImage.Question) =>
        Show(owner, message, title, MessageBoxButton.YesNo, image, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static string Input(Window? owner, string message, string title, string defaultValue = "")
    {
        var dialog = Create(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK, defaultValue);
        dialog.ShowDialog();
        return dialog.Result == MessageBoxResult.OK ? dialog.InputValue : "";
    }

    private static KumoriDialogWindow Create(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult,
        string? inputValue = null)
    {
        var dialog = new KumoriDialogWindow();
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }
        dialog.Configure(message, title, buttons, image, defaultResult, inputValue);
        return dialog;
    }

    private static MessageBoxResult ResolveDefault(MessageBoxButton buttons, MessageBoxResult requested)
    {
        if (requested != MessageBoxResult.None)
        {
            return requested;
        }

        return buttons switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            _ => MessageBoxResult.OK,
        };
    }

    private static Window? ActiveOwner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;
}
