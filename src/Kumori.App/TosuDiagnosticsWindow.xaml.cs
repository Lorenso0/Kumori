using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kumori.Core;
using Kumori.Core.State;
using Kumori.Native;

namespace Kumori.App;

/// <summary>Live view of the managed tosu process and its console-equivalent log.</summary>
public partial class TosuDiagnosticsWindow : Window
{
    private const int MaxLogCharacters = 250_000;
    private readonly AppStateStore _appState;
    private readonly DispatcherTimer _refreshTimer;
    private string _lastLog = string.Empty;

    public TosuDiagnosticsWindow(AppStateStore appState)
    {
        _appState = appState;
        InitializeComponent();
        Refresh();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(Tabs.SelectedIndex switch
        {
            0 => StatusText.Text,
            1 => TelemetryText.Text,
            _ => LogText.Text,
        });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        RefreshStatus();
        RefreshTelemetry();
        RefreshLog();
    }

    private void RefreshTelemetry()
    {
        var telemetry = _appState.Current.Tracking.LatestTelemetry;
        if (telemetry is null)
        {
            TelemetryText.Text = "Waiting for the first tosu packet. Values appear here as soon as tosu publishes telemetry.";
            return;
        }

        TelemetryText.Text =
            $"""
            Latest packet: {telemetry.ReceivedAt:O}

            Map
              {telemetry.Artist ?? "unknown"} — {telemetry.Title ?? "unknown"} [{telemetry.Difficulty ?? "unknown"}]
              Mapper: {telemetry.Mapper ?? "unknown"}
              Beatmap/set ID: {telemetry.BeatmapId?.ToString() ?? "n/a"}/{telemetry.BeatmapSetId?.ToString() ?? "n/a"}
              Checksum: {telemetry.Checksum ?? "n/a"}
              Mods: {telemetry.ModsKey}

            Play
              State: {telemetry.State} | playing={telemetry.IsPlaying} | results={telemetry.IsResults} | osu!standard={telemetry.IsStandardMode}
              Map time: {telemetry.LiveTimeMs?.ToString() ?? "n/a"} ms | progress: {telemetry.Progress:P2}
              Score: {telemetry.Score:N0} | grade: {telemetry.Grade ?? "n/a"}
              Accuracy: {telemetry.Accuracy:P4} | combo: {telemetry.Combo:0}/{telemetry.MaxCombo?.ToString() ?? "?"} | health: {telemetry.Health:P2}
              PP: {telemetry.Pp:0.##} | FC PP: {telemetry.FcPp:0.##} | Max PP: {telemetry.MaxPp:0.##} | UR: {telemetry.UnstableRate:0.##}

            Judgements
              300: {telemetry.Hit300:0} | 100: {telemetry.Hit100:0} | 50: {telemetry.Hit50:0} | miss: {telemetry.Miss:0}
              geki: {telemetry.Geki:0} | katu: {telemetry.Katu:0} | slider breaks: {telemetry.SliderBreaks:0}
              large ticks: {telemetry.LargeTickHits:0} hit / {telemetry.LargeTickMisses:0} miss
              small ticks: {telemetry.SmallTickHits:0} hit / {telemetry.SmallTickMisses:0} miss
              slider tails: {telemetry.SliderTailHits:0} hit / {telemetry.SliderTailMisses:0} miss
            """;
    }

    private void RefreshStatus()
    {
        var state = _appState.Current;
        var process = FindTosuProcess();
        var logPath = FindCurrentLogPath();
        var version = TryReadFile(AppPaths.TosuVersionFile, "unknown").Trim();
        StatusText.Text =
            $"""
            Managed tosu
              Connected to Kumori: {state.Tracking.TosuConnected}
              Tracking health: {state.Tracking.Health}
              Tracking detail: {state.Tracking.Detail ?? "none"}
              Installed: {File.Exists(AppPaths.TosuExecutable)}
              Version: {version}
              Executable: {AppPaths.TosuExecutable}
              Process: {(process is null ? "not running" : $"running (PID {process.Id})")}
              Started: {TryProcessStartTime(process) ?? "n/a"}
              Environment: {AppPaths.TosuEnvFile}
              Active log: {logPath ?? "not created yet"}
              Refreshed: {DateTimeOffset.Now:O}

            This is the contents tosu writes in place of a visible command window.
            """;
        process?.Dispose();
    }

    private void RefreshLog()
    {
        var log = ReadLogTail(FindCurrentLogPath());
        if (log == _lastLog)
        {
            return;
        }

        var shouldFollow = LogText.VerticalOffset >= LogText.ExtentHeight - LogText.ViewportHeight - 2;
        LogText.Text = log;
        _lastLog = log;
        if (shouldFollow)
        {
            LogText.ScrollToEnd();
        }
    }

    private static Process? FindTosuProcess() =>
        Process.GetProcessesByName("tosu")
            .OrderByDescending(process => TryProcessStartTime(process) ?? DateTime.MinValue.ToString("O"))
            .FirstOrDefault();

    private static string? TryProcessStartTime(Process? process)
    {
        try { return process?.StartTime.ToString("O"); }
        catch { return null; }
    }

    private static string? FindCurrentLogPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppPaths.TosuDir, "logs", "latest.log"),
            Path.Combine(AppPaths.TosuLogDir, "latest.log"),
        };

        return candidates
            .Where(File.Exists)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private static string ReadLogTail(string? path)
    {
        if (path is null)
        {
            return "tosu has not written a log yet. Launch tosu, then this pane will update automatically.";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - MaxLogCharacters * 2L);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return start > 0 ? $"… showing the latest {MaxLogCharacters:N0} characters …{Environment.NewLine}{text}" : text;
        }
        catch (IOException ex)
        {
            return $"Waiting for tosu log to become readable: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Cannot read the tosu log: {ex.Message}";
        }
    }

    private static string TryReadFile(string path, string fallback)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : fallback; }
        catch { return fallback; }
    }
}
