using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using Kumori.App.ViewModels;
using Kumori.Core.Settings;
using Kumori.Native;

namespace Kumori.App;

public partial class MainWindow : Window
{
    private const double DefaultWindowWidth = 1180;
    private const double DefaultWindowHeight = 820;
    private const double MinimumRestoredWindowWidth = 1080;
    private const double MinimumRestoredWindowHeight = 760;
    private const double MaximumRestoredWindowWidth = 1300;
    private const double MaximumRestoredWindowHeight = 920;

    private readonly SettingsService _settings;

    /// <summary>Set by App before Shutdown so the tray Exit actually closes the window.</summary>
    public bool ForceClose { get; set; }

    public MainWindow(MainViewModel viewModel, SettingsService settings)
    {
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();
        ApplyInitialBounds();
        Closing += (_, e) =>
        {
            SaveBounds();
            // Tray app: closing the window hides it; Exit lives in the tray menu.
            if (!ForceClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar before first render — part of the no-flicker plan.
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Session separators are not selectable — revert any non-attempt selection
        // back to the previously selected attempt (or clear it).
        if (sender is ListBox lb && lb.SelectedItem is not null and not AttemptRowViewModel)
        {
            lb.SelectedItem = e.RemovedItems.OfType<AttemptRowViewModel>().FirstOrDefault();
        }
    }

    private void DayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DayRowViewModel row }
            && DataContext is MainViewModel vm)
        {
            vm.ToggleDay(row);
        }
    }

    private void SessionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SessionRowViewModel row }
            && DataContext is MainViewModel vm)
        {
            vm.ToggleSession(row);
        }
    }

    private void ScrollingTitle_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement viewport)
        {
            return;
        }
        var title = FindVisualChild<TextBlock>(viewport);
        if (title is null)
        {
            return;
        }

        var transform = EnsureTranslateTransform(title);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        title.Width = double.NaN;

        var fullWidth = MeasureTextWidth(title);
        var overflow = fullWidth - viewport.ActualWidth;
        if (overflow <= 1)
        {
            ResetTitleScroll(title, TimeSpan.Zero);
            return;
        }

        title.Width = fullWidth;
        var distance = overflow + 12;
        var seconds = Math.Clamp(distance / 48.0, 3.0, 9.0);
        var animation = new DoubleAnimation(0, -distance, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(350),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ScrollingTitle_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement viewport &&
            FindVisualChild<TextBlock>(viewport) is { } title)
        {
            ResetTitleScroll(title, TimeSpan.FromMilliseconds(120));
        }
    }

    private static void ResetTitleScroll(TextBlock title, TimeSpan duration)
    {
        var transform = EnsureTranslateTransform(title);
        var animation = new DoubleAnimation(0, duration);
        if (duration > TimeSpan.Zero)
        {
            animation.Completed += (_, _) => title.Width = double.NaN;
        }
        else
        {
            title.Width = double.NaN;
        }
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static double MeasureTextWidth(TextBlock title)
    {
        var dpi = VisualTreeHelper.GetDpi(title);
        var formatted = new FormattedText(
            title.Text ?? "",
            CultureInfo.CurrentCulture,
            title.FlowDirection,
            new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch),
            title.FontSize,
            title.Foreground,
            dpi.PixelsPerDip)
        {
            MaxLineCount = 1,
        };
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static TranslateTransform EnsureTranslateTransform(TextBlock title)
    {
        if (title.RenderTransform is TranslateTransform { IsFrozen: false } transform)
        {
            return transform;
        }
        transform = new TranslateTransform();
        title.RenderTransform = transform;
        return transform;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private void CardOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AttemptRowViewModel row } button
            || DataContext is not MainViewModel vm)
        {
            return;
        }
        var menu = new ContextMenu();
        var replay = new MenuItem { Header = "Open Replay Analyzer", IsEnabled = row.CanOpenReplayInspector };
        replay.Click += (_, _) => vm.OpenReplayInspector(row);
        var showAll = new MenuItem { Header = "Show all plays for this map" };
        showAll.Click += (_, _) => vm.ShowAllPlaysForMap(row);
        var delete = new MenuItem { Header = "Delete this attempt" };
        delete.Click += (_, _) => vm.DeleteAttempt(row);
        menu.Items.Add(replay);
        menu.Items.Add(new Separator());
        menu.Items.Add(showAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void SessionOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionRowViewModel row } button
            || DataContext is not MainViewModel vm)
        {
            return;
        }
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "Delete this session" };
        delete.Click += (_, _) => vm.DeleteSession(row.Model.Id);
        menu.Items.Add(delete);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void History_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        // "At bottom" once the last pixels are in view, or when the whole list
        // already fits the viewport. Gates the Load older button.
        const double threshold = 24;
        vm.IsScrolledToBottom = e.ViewportHeight <= 0
            || e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - threshold;
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Monitor-relative default size, centered on the work area. Restores the
    /// saved size/position only if it is still (mostly) on screen; otherwise
    /// recenters — handles monitor layout changes and offscreen windows.
    /// </summary>
    private void ApplyInitialBounds()
    {
        var work = SystemParameters.WorkArea;
        var saved = _settings.Current.Window;

        var useSavedSize = saved.Width is >= MinimumRestoredWindowWidth and <= MaximumRestoredWindowWidth
            && saved.Height is >= MinimumRestoredWindowHeight and <= MaximumRestoredWindowHeight;
        double width = useSavedSize
            ? saved.Width!.Value
            : Math.Min(Math.Max(DefaultWindowWidth, MinWidth), work.Width);
        double height = useSavedSize && saved.Height is { } savedHeight
            ? savedHeight
            : Math.Min(Math.Max(DefaultWindowHeight, MinHeight), work.Height);
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        double left, top;
        if (useSavedSize && saved.Left is { } l && saved.Top is { } t && IsMostlyOnScreen(l, t, width, height))
        {
            left = l;
            top = t;
        }
        else
        {
            left = work.Left + (work.Width - width) / 2;
            top = work.Top + (work.Height - height) / 2;
        }

        Width = width;
        Height = height;
        Left = left;
        Top = top;
        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsMostlyOnScreen(double left, double top, double width, double height)
    {
        // VirtualScreen covers all monitors; require the title bar region visible.
        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsTop = SystemParameters.VirtualScreenTop;
        var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        return left + width - 100 > vsLeft
            && left + 100 < vsRight
            && top + 40 > vsTop
            && top + 40 < vsBottom;
    }

    private void SaveBounds()
    {
        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _settings.Update(s =>
        {
            s.Window.Left = bounds.Left;
            s.Window.Top = bounds.Top;
            s.Window.Width = bounds.Width;
            s.Window.Height = bounds.Height;
            s.Window.Maximized = maximized;
        });
    }
}
