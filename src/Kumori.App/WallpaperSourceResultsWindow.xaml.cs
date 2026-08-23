using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.Native;
using Microsoft.Win32;

namespace Kumori.App;

public partial class WallpaperSourceResultsWindow : Window
{
    private readonly WallpaperSourceSearchService searchService;
    private readonly string artworkSource;
    private readonly CancellationTokenSource cancellation = new();
    private int previewVersion;

    private readonly ObservableCollection<WallpaperResultItem> results = [];
    public System.Collections.IEnumerable Results => results;

    internal WallpaperSourceResultsWindow(
        WallpaperSourceSearchService searchService,
        string artworkSource)
    {
        this.searchService = searchService;
        this.artworkSource = artworkSource;
        DataContext = this;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await searchService.SearchAsync(artworkSource, cancellation.Token);
            foreach (var result in response.Results)
            {
                results.Add(new WallpaperResultItem(result));
            }

            SearchProgress.Visibility = Visibility.Collapsed;
            string warning = response.Warnings.Count > 0
                ? $" {string.Join(" ", response.Warnings)}"
                : "";
            StatusText.Text = results.Count == 0
                ? $"No matching pictures were found.{warning}"
                : $"{results.Count} matches found; highest resolution first.{warning}";

            if (results.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
                _ = LoadThumbnailsAsync(results.Take(20).ToArray(), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            SearchProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = "The wallpaper search timed out. Please try again.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SearchProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = exception is HttpRequestException
                ? "The wallpaper search could not connect. Check your connection and try again."
                : exception.Message;
        }
    }

    private async Task LoadThumbnailsAsync(
        IReadOnlyList<WallpaperResultItem> items,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                Uri target = item.Result.ThumbnailUri ?? item.Result.ImageUri;
                var image = await searchService.DownloadImageAsync(target, cancellationToken);
                item.Thumbnail = CreateBitmap(image.Bytes);
            }
            catch (Exception exception) when (exception is HttpRequestException
                                              or InvalidOperationException
                                              or NotSupportedException
                                              or IOException)
            {
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private async void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not WallpaperResultItem item)
        {
            ClearSelection();
            return;
        }

        int version = ++previewVersion;
        PreviewImage.Source = null;
        PreviewStatusText.Visibility = Visibility.Visible;
        PreviewStatusText.Text = "Loading picture…";
        SelectedTitleText.Text = item.Result.Title;
        SelectedDetailsText.Text = $"{item.Result.ResolutionText} · {item.Result.MatchText} · {item.Result.Provider}";
        SourceAddressText.Text = item.Result.SourceUri?.AbsoluteUri ?? item.Result.ImageUri.AbsoluteUri;
        CopySourceButton.IsEnabled = true;
        SaveButton.IsEnabled = false;

        try
        {
            var image = await searchService.DownloadImageAsync(item.Result.ImageUri, cancellation.Token);
            if (version != previewVersion)
            {
                return;
            }

            PreviewImage.Source = CreateBitmap(image.Bytes);
            PreviewStatusText.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or IOException)
        {
            if (version == previewVersion)
            {
                PreviewStatusText.Text = "This picture could not be loaded.";
            }
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not WallpaperResultItem item)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var image = await searchService.DownloadImageAsync(item.Result.ImageUri, cancellation.Token);
            string extension = ExtensionFor(image.ContentType, item.Result.ImageUri);
            var dialog = new SaveFileDialog
            {
                Title = "Save wallpaper",
                FileName = SafeFilename(item.Result.Title) + extension,
                DefaultExt = extension,
                Filter = FilterFor(extension),
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await File.WriteAllBytesAsync(dialog.FileName, image.Bytes, cancellation.Token);
            StatusText.Text = $"Saved {Path.GetFileName(dialog.FileName)} locally.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"The picture could not be saved: {exception.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = ResultsList.SelectedItem is WallpaperResultItem;
        }
    }

    private void CopySourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SourceAddressText.Text))
        {
            Clipboard.SetText(SourceAddressText.Text);
            StatusText.Text = "Source address copied.";
        }
    }

    private void ClearSelection()
    {
        previewVersion++;
        PreviewImage.Source = null;
        PreviewStatusText.Visibility = Visibility.Visible;
        PreviewStatusText.Text = "Select a result to preview it";
        SelectedTitleText.Text = "";
        SelectedDetailsText.Text = "";
        SourceAddressText.Text = "";
        CopySourceButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
    }

    private static BitmapSource CreateBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string ExtensionFor(string? contentType, Uri imageUri) =>
        contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/jpeg" or "image/jpg" => ".jpg",
            _ => ExtensionFromPath(imageUri),
        };

    private static string ExtensionFromPath(Uri imageUri)
    {
        string extension = Path.GetExtension(imageUri.AbsolutePath).ToLowerInvariant();
        return extension is ".png" or ".webp" or ".gif" or ".bmp" or ".jpg" or ".jpeg"
            ? extension
            : ".jpg";
    }

    private static string FilterFor(string extension) => extension switch
    {
        ".png" => "PNG image (*.png)|*.png|All files (*.*)|*.*",
        ".webp" => "WebP image (*.webp)|*.webp|All files (*.*)|*.*",
        ".gif" => "GIF image (*.gif)|*.gif|All files (*.*)|*.*",
        ".bmp" => "Bitmap image (*.bmp)|*.bmp|All files (*.*)|*.*",
        _ => "JPEG image (*.jpg;*.jpeg)|*.jpg;*.jpeg|All files (*.*)|*.*",
    };

    private static string SafeFilename(string title)
    {
        string cleaned = new(title
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character))
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "wallpaper" : cleaned.Trim();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}

internal sealed class WallpaperResultItem : INotifyPropertyChanged
{
    private ImageSource? thumbnail;

    public WallpaperSearchResult Result { get; }
    public string SummaryText => $"{Result.MatchText} · {Result.Domain}";
    public ImageSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            if (ReferenceEquals(thumbnail, value))
            {
                return;
            }
            thumbnail = value;
            OnPropertyChanged();
        }
    }

    public WallpaperResultItem(WallpaperSearchResult result)
    {
        Result = result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
