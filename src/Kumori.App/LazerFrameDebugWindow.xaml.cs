using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Kumori.Core.Settings;
using Kumori.Native;

namespace Kumori.App;

public partial class LazerFrameDebugWindow : Window
{
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _refreshTimer;

    public LazerFrameDebugWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Refresh();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(StatusText.Text);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        var statusPath = LazerReplayFrameDiagnostics.StatusPath;
        var statusJson = TryReadStatusJson(statusPath);
        var status = LazerReplayFrameDiagnostics.Load();

        StatusText.Text =
            $"""
            How to test
              1. Start Kumori and vanilla tosu.
              2. Start osu!lazer, then play any osu!standard map.
              3. Open this window while playing or after finishing the attempt.

            What good looks like
              State: lazer_memory_frame means Kumori is reading usable osu!lazer replay frames from memory.
              State: lazer_memory_waiting means osu!lazer is running, but replay frames are not available yet.
              State: osu_lazer_not_running means osu!lazer was not detected.
              Frames emitted: increases when the memory reader finds replay frames.
              Frames buffered for attempt: increases during an active Kumori attempt.
              Frames stored: increases after the attempt finalizes.
              Stored movement source should become: lazer_memory.

            Current summary
              Capture source: lazer_memory
              Status file: {statusPath}
              Updated: {status.UpdatedAt:O}
              State: {status.State}
              Detail: {status.Detail ?? "none"}
              Replay-frame reader state: {status.State}
              Frames emitted: {status.FramesEmitted}
              Active attempt: {status.ActiveAttemptId?.ToString() ?? "none"}
              Buffered/stored: {status.FramesBufferedForAttempt}/{status.FramesStored}
              Last frame: t={status.LastFrameMapTimeMs?.ToString("0.##") ?? "n/a"} x={status.LastFrameX?.ToString("0.##") ?? "n/a"} y={status.LastFrameY?.ToString("0.##") ?? "n/a"} L={status.LastFrameLeftPressed} R={status.LastFrameRightPressed}
              Last error: {status.LastError ?? "none"}
              osu! process: {status.ProcessName ?? "none"} pid={status.ProcessId?.ToString() ?? "n/a"}
              osu! path: {status.ProcessPath ?? "n/a"}

            Raw status
            {statusJson}
            """;
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static string TryReadStatusJson(string statusPath)
    {
        try
        {
            return File.Exists(statusPath)
                ? PrettyJson(File.ReadAllText(statusPath))
                : "lazer_replay_frame_status.json was not found.";
        }
        catch (IOException ex)
        {
            return $"Could not read status file yet: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Could not read status file yet: {ex.Message}";
        }
    }
}
