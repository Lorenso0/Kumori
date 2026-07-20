using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kumori.App.Controls;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;
using static System.FormattableString;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App.ViewModels;

public partial class AttemptDetailsViewModel
{
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [RelayCommand]
    private Task RetryLoadAsync() => LoadAsync(_requestedAttemptId);

    private async Task LoadOriginalStarsAsync(AttemptDetails? details)
    {
        if (details is null || details.CapturedDifficulty.TryGetValue("stars", out var stars) && stars.Original is not null)
        {
            return;
        }

        var path = !string.IsNullOrWhiteSpace(details.LocalBeatmapPath) && File.Exists(details.LocalBeatmapPath)
            ? details.LocalBeatmapPath
            : BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!_originalStarCache.TryGetValue(path, out var original))
            {
                original = await Task.Run(() => BeatmapStarRatingCalculator.CalculateOriginal(path));
                _originalStarCache[path] = original;
                TrimCache(_originalStarCache, StarCacheLimit);
            }

            if (ReferenceEquals(Details, details))
            {
                _calculatedOriginalStars = original;
                OnPropertyChanged(nameof(StarsDisplay));
                OnPropertyChanged(nameof(StarsNumberDisplay));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Original star calculation failed for attempt {AttemptId}", details.Summary.Id);
        }
    }

    private async Task LoadPressureCurveAsync(AttemptDetails? details)
    {
        if (details is null)
        {
            return;
        }
        var id = details.Summary.Id;
        if (_curveCache.TryGetValue(id, out var cached))
        {
            if (ReferenceEquals(Details, details))
            {
                PressureCurve = cached;
            }
            return;
        }
        var path = !string.IsNullOrWhiteSpace(details.LocalBeatmapPath) && File.Exists(details.LocalBeatmapPath)
            ? details.LocalBeatmapPath
            : BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            var curve = await Task.Run(() => MapPressureGraph.BuildDifficultyCurve(path, details.Mods));
            _curveCache[id] = curve;
            TrimCache(_curveCache, DetailCacheLimit);
            if (ReferenceEquals(Details, details))
            {
                PressureCurve = curve;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Map pressure curve build failed for attempt {AttemptId}", id);
        }
    }

    private static IReadOnlyList<UrPoint> BuildUrSamples(AttemptDetails? details)
    {
        if (details is null)
        {
            return Array.Empty<UrPoint>();
        }
        var list = new List<UrPoint>();
        foreach (var e in details.Events)
        {
            if (e.EventType != "checkpoint" || e.MapTimeMs is null)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(e.DataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("unstable_rate", out var urElement)
                    && TryNumber(urElement) is { } ur && ur > 0)
                {
                    list.Add(new UrPoint((int)Math.Max(0, e.MapTimeMs.Value), ur));
                }
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "Skipping malformed checkpoint payload for attempt {AttemptId}", details.Summary.Id);
            }
        }
        list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return list;
    }

    [RelayCommand(CanExecute = nameof(CanOpenReplayInspector))]
    private async Task OpenReplayInspector()
    {
        LastReplayInspectorProcess = null;
        if (Details is not { } details || _replayViewer is null)
        {
            return;
        }

        try
        {
            string? stableBeatmap = !string.IsNullOrWhiteSpace(details.LocalBeatmapPath) && File.Exists(details.LocalBeatmapPath)
                ? details.LocalBeatmapPath
                : null;
            var lazer = stableBeatmap is null
                ? LazerStorage.ResolveBeatmapAssets(details.Summary.OsuBeatmapId, details.Summary.BeatmapSetId, details.Summary.Difficulty)
                : null;
            var beatmapPath = stableBeatmap ?? lazer?.BeatmapPath ?? BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
            if (string.IsNullOrWhiteSpace(beatmapPath))
            {
                LoadError = "Replay Analyzer needs cached beatmap media for this play";
                return;
            }

            var mediaDirectory = stableBeatmap is not null && Directory.Exists(details.LocalMediaDirectory)
                ? details.LocalMediaDirectory
                : BeatmapArtworkResolver.ResolveMediaDirectory(details.Summary);
            var mediaPaths = details.LocalMediaPaths.Count > 0
                ? details.LocalMediaPaths
                : lazer?.Files;
            var contract = await Task.Run(() => details.IsImported
                ? _replayViewer.WriteExternalContract(
                    details,
                    _movementLoader(details.Summary.Id),
                    beatmapPath,
                    mediaDirectory,
                    mediaPaths)
                : _replayViewer.WriteContract(
                    details.Summary.Id,
                    beatmapPath,
                    mediaDirectory,
                    mediaPaths));
            try
            {
                await _replayViewer.PrepareAnalysisAsync(contract);
            }
            catch (Exception ex)
            {
                // Exact judgement simulation enriches the analyzer but must
                // never prevent the movement/replay viewer itself from opening.
                Log.Warning(ex, "Exact replay analysis preparation failed for attempt {AttemptId}; opening fallback analyzer", details.Summary.Id);
            }
            LastReplayInspectorProcess = _replayViewer.LaunchViewer(contract);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Replay Analyzer launch failed for attempt {AttemptId}", details.Summary.Id);
            LoadError = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanValidateOsr))]
    private async Task ValidateOsr()
    {
        if (Details is not { } details)
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = "Select the .osr replay for this play",
            Filter = "osu! replay (*.osr)|*.osr",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var beatmapPath = !string.IsNullOrWhiteSpace(details.LocalBeatmapPath) && File.Exists(details.LocalBeatmapPath)
                ? details.LocalBeatmapPath
                : BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
            if (string.IsNullOrWhiteSpace(beatmapPath))
            {
                LoadError = "Replay validation needs the cached .osu beatmap file for this play.";
                return;
            }
            var capturedSamples = _movementLoader(details.Summary.Id);
            var result = await Task.Run(() => OsrValidationService.Validate(picker.FileName, details, beatmapPath, capturedSamples));
            MainWindow.TryOpenWorkspace(new OsrValidationWindow(result), "Replay validation");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            Log.Warning(ex, "Could not validate .osr for attempt {AttemptId}", details.Summary.Id);
            KumoriDialog.Show(
                Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                $"Kumori could not read that .osr file.\n\n{ex.Message}",
                ".osr validation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task LoadAsync(long? attemptId, CancellationToken cancellationToken = default)
    {
        _requestedAttemptId = attemptId;
        var previousLoad = _loadCts;
        _loadCts = null;
        previousLoad?.Cancel();
        previousLoad?.Dispose();
        if (attemptId is null)
        {
            Details = null;
            IsLoading = false;
            LoadError = null;
            return;
        }
        if (_cache.TryGetValue(attemptId.Value, out var cached))
        {
            if (_requestedAttemptId != attemptId)
            {
                return;
            }
            Details = cached;
            IsLoading = false;
            LoadError = null;
            return;
        }

        // Do not keep the last play visible while the next one is loading.
        // Apart from being misleading, this made a newly highlighted history row
        // appear to have the previous row's title and score in the inspector.
        Details = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCts = cts;
        IsLoading = true;
        LoadError = null;
        try
        {
            var loaded = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var result = _detailsLoader(attemptId.Value);
                cts.Token.ThrowIfCancellationRequested();
                return result;
            }, cts.Token);
            if (cts.IsCancellationRequested || _requestedAttemptId != attemptId)
            {
                return;
            }
            if (loaded is not null)
            {
                _cache[attemptId.Value] = loaded;
                TrimCache(_cache, DetailCacheLimit);
            }
            Details = loaded;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Detail load failed for attempt {AttemptId}", attemptId);
            if (!cts.IsCancellationRequested)
            {
                LoadError = "Could not load details (see logs)";
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                IsLoading = false;
            }
            cts.Dispose();
        }
    }

    private static void TrimCache<TKey, TValue>(Dictionary<TKey, TValue> cache, int limit) where TKey : notnull
    {
        while (cache.Count > limit) cache.Remove(cache.Keys.First());
    }

    /// <summary>Removes deleted attempts from the inspector and its detail cache.</summary>
    public void ForgetAttempt(long attemptId)
    {
        _cache.Remove(attemptId);
        _curveCache.Remove(attemptId);
        if (Details?.Summary.Id == attemptId || _requestedAttemptId == attemptId)
        {
            _requestedAttemptId = null;
            var load = _loadCts;
            _loadCts = null;
            load?.Cancel();
            load?.Dispose();
            Details = null;
            IsLoading = false;
            LoadError = null;
        }
    }

}
