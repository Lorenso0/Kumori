using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Kumori.Native;

namespace Kumori.App;

public partial class StableFrameDebugWindow : Window
{
    private readonly DispatcherTimer refreshTimer;

    public StableFrameDebugWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Refresh();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        refreshTimer.Stop();
        base.OnClosed(e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(StatusText.Text);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ArmSnapshot_Click(object sender, RoutedEventArgs e)
    {
        string requestPath = SnapshotRequestPath();
        Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
        File.WriteAllText(requestPath, DateTimeOffset.UtcNow.ToString("O"));
        try { File.Delete(SnapshotPath() + ".new"); } catch { }
        Refresh();
    }

    private void Refresh()
    {
        var path = StableReplayFrameDiagnostics.StatusPath;
        var status = StableReplayFrameDiagnostics.Load();
        StatusText.Text =
            $"""
            How to test
              1. Enable replay-frame capture in Settings.
              2. Start osu!stable and play an osu!standard map to a result screen.
              3. Keep Kumori open through the attempt finalization.
              4. Open the saved play; its movement source should be Stable Memory or Stable Replay.

            What good looks like
              State: armed              stable play detected
              State: searching          attempt finalized; local replay scan started
              State: checking_candidate candidate found and being checksum-validated
              State: stored             exact replay frames decoded and persisted

            Common failure states
              paths_unavailable  tosu did not report usable stable/beatmap paths
              replay_not_found   stable did not save a matching local replay
              existing_capture_preserved another movement source was retained
              error              decoder or filesystem failure; see Last error

            Current summary
              Enabled: {status.Enabled}
              Status file: {path}
              Updated: {status.UpdatedAt:O}
              State: {status.State}
              Detail: {status.Detail ?? "none"}
              Active attempt: {status.ActiveAttemptId?.ToString() ?? "none"}
              Game folder: {status.GameFolder ?? "n/a"}
              Beatmap path: {status.BeatmapPath ?? "n/a"}
              Expected checksum: {status.ExpectedChecksum ?? "n/a"}
              Candidate replay: {status.CandidateReplayPath ?? "n/a"}
              Candidates checked: {status.CandidatesChecked}
              Frames decoded: {status.FramesDecoded}
              Frames stored (lifetime): {status.FramesStored}
              Comparison report: {status.ComparisonReportPath ?? "not created"}
              Comparison summary: {status.ComparisonSummary ?? "n/a"}
              Last error: {status.LastError ?? "none"}

            Live capture (works for quit/retry/failed plays)
              State: {status.LiveState}
              Detail: {status.LiveDetail ?? "none"}
              Snapshot armed: {File.Exists(SnapshotRequestPath())}
              Failure snapshot: {status.LiveSnapshotPath ?? "not captured"}
              Frames received: {status.LiveFramesReceived}
              Frames buffered: {status.LiveFramesBuffered}
              Frames stored: {status.LiveFramesStored}

            Raw status
            {ReadPretty(path)}
            """;
    }

    private static string SnapshotPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kumori", "runtime", "debug", "stable-memory-latest.bin");

    private static string SnapshotRequestPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kumori", "runtime", "debug", "stable-memory-snapshot.request");

    private static string ReadPretty(string path)
    {
        try
        {
            if (!File.Exists(path)) return "stable_replay_frame_status.json was not found.";
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Could not read status file yet: {ex.Message}";
        }
    }
}
