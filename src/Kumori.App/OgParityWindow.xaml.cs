using System.IO;
using System.Windows;
using Kumori.Native;

namespace Kumori.App;

public partial class OgParityWindow : Window
{
    private static readonly string Report = """
        OG Python parity audit

        Implemented in live build
          - tosu-based tracking, managed vanilla tosu download/install/launch
          - score, judgement, PP, UR, mods, mod settings, sessions, PBs
          - osu!lazer replay-frame movement storage via memory reader
          - OpenTabletDriver detection, optional auto-launch, owned-process cleanup
          - replay inspector contracts, skin selection, hit timing, map pressure, replay media
          - analytics/history/search/filter/group repeats/CSV/JSON export
          - diagnostics zip, lazer frame debug, health dashboard, logs, tray actions
          - LG dual-mode command plus DDC/CI fallback
          - cleanup/backfill maintenance tooling

        Partially implemented
          - first-run setup: live wizard covers core setup and diagnostics; OG-style guided test flow is compact
          - skin library: imports .osk/folders, activates/deletes; no visual preview thumbnails yet
          - auto update: release check foundation is present; installer replacement still remains manual

        Intentionally omitted
          - legacy key-only tracker: only provides rough play/session guesses without beatmap, mods, PP, score, UR, or replay context

        Remaining nice-to-haves
          - visual skin previews
          - signed Kumori self-update installer
          - deeper replay section bookmarks inside the replay viewer
        """;

    public OgParityWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        ReportText.Text = Report;
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "OG_PARITY_AUDIT.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Report.Replace("  - ", "- "));
        KumoriDialog.Show(this, $"Wrote {path}", "Kumori", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
