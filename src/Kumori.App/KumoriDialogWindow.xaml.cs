using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Kumori.Native;

namespace Kumori.App;

public partial class KumoriDialogWindow : Window
{
    private readonly List<Button> _buttons = new();

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
    public string InputValue => InputTextBox.Text;

    public KumoriDialogWindow()
    {
        InitializeComponent();
    }

    public void Configure(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult,
        string? inputValue = null)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfigureIcon(image);

        if (inputValue is not null)
        {
            InputTextBox.Visibility = Visibility.Visible;
            InputTextBox.Text = inputValue;
            InputTextBox.SelectAll();
        }

        ButtonHost.Children.Clear();
        _buttons.Clear();
        var cancelResult = CancelResult(buttons);
        foreach (var spec in ButtonSpecs(buttons))
        {
            var button = new Button
            {
                Content = spec.Label,
                MinWidth = 78,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = spec.Result == defaultResult,
                IsCancel = spec.Result == cancelResult,
                Style = (Style)FindResource(ButtonStyleKey(spec.Result, image)),
            };
            button.Click += (_, _) => CloseWith(spec.Result);
            ButtonHost.Children.Add(button);
            _buttons.Add(button);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (InputTextBox.Visibility == Visibility.Visible)
        {
            InputTextBox.Focus();
            return;
        }

        _buttons.FirstOrDefault(b => b.IsDefault)?.Focus();
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        var (text, brushKey) = image switch
        {
            MessageBoxImage.Error => ("!", "Brush.Danger"),
            MessageBoxImage.Warning => ("!", "Brush.Warning"),
            MessageBoxImage.Question => ("?", "Brush.Cyan"),
            MessageBoxImage.Information => ("i", "Brush.AccentPurple"),
            _ => ("i", "Brush.TextMuted"),
        };
        var brush = (System.Windows.Media.Brush)FindResource(brushKey);
        IconText.Text = text;
        IconText.Foreground = brush;
        IconBadge.BorderBrush = brush;
    }

    private static IEnumerable<(string Label, MessageBoxResult Result)> ButtonSpecs(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) },
            MessageBoxButton.YesNoCancel => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };

    private static string ButtonStyleKey(MessageBoxResult result, MessageBoxImage image)
    {
        if (result is MessageBoxResult.Yes or MessageBoxResult.OK)
        {
            return image is MessageBoxImage.Warning or MessageBoxImage.Error
                ? "Button.Danger"
                : "Button.Primary";
        }

        return "Button.Chrome";
    }

    private void CloseWith(MessageBoxResult result)
    {
        Result = result;
        DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseWith(CurrentCancelResult());

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWith(CurrentCancelResult());
        }
    }

    private MessageBoxResult CurrentCancelResult() =>
        _buttons.Any(b => string.Equals(b.Content?.ToString(), "Cancel", StringComparison.Ordinal))
            ? MessageBoxResult.Cancel
            : _buttons.Any(b => string.Equals(b.Content?.ToString(), "No", StringComparison.Ordinal))
                ? MessageBoxResult.No
                : MessageBoxResult.None;

    private static MessageBoxResult CancelResult(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.None,
        };

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
