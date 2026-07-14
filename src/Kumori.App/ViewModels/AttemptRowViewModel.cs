using CommunityToolkit.Mvvm.ComponentModel;
using Kumori.Core;
using Kumori.Core.Models;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

/// <summary>Read-only row wrapper with display helpers for the dense history list.</summary>
public partial class AttemptRowViewModel : HistoryRowViewModel
{
    public AttemptSummary Model { get; }

    public AttemptRowViewModel(AttemptSummary model) => Model = model;

    public long Id => Model.Id;
    public string Artist => Model.Artist;
    public string Title => Model.Title;
    public string Difficulty => string.IsNullOrWhiteSpace(Model.Difficulty) ? "Unknown difficulty" : Model.Difficulty;
    public string MapLine => $"{Model.Artist} — {Model.Title}";
    public string DifficultyLine
    {
        get
        {
            var difficulty = string.IsNullOrEmpty(Model.Difficulty) ? "Unknown" : Model.Difficulty;
            var mapper = string.IsNullOrEmpty(Model.Mapper) ? "Unknown" : Model.Mapper;
            return $"[{difficulty}]  ·  {StarsText}  ·  mapped by {mapper}";
        }
    }
    public string Grade => Model.Grade ?? "-";
    public double GradeProgress => Math.Clamp(Model.Accuracy / 100d, 0d, 1d);
    public string AccuracyText => Invariant($"{Model.Accuracy:0.00}%");
    public string ScoreText => Model.Score.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string ProgressText => Invariant($"{Math.Clamp(Model.Progress, 0, 1) * 100:0}%");
    public string MissesText => Model.Misses.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string PpText => Model.Pp > 0 ? Invariant($"{Model.Pp:0.0}pp") : "";
    public string ComboText => Model.Combo > 0
        ? Model.BeatmapMaxCombo > 0
            ? $"{Model.Combo}/{Model.BeatmapMaxCombo}x"
            : $"{Model.Combo}x"
        : "";
    public string ModsText => ModDisplayText.FromKey(Model.ModsKey);
    public IReadOnlyList<string> ModAcronyms => ModDisplayText.AcronymsFromKey(Model.ModsKey);
    public string Outcome => Model.Outcome.ToUpperInvariant();
    public string OutcomeWithProgress => string.Equals(Model.Outcome, "completed", StringComparison.OrdinalIgnoreCase)
        ? Outcome
        : Invariant($"{Outcome} ({Math.Clamp(Model.Progress, 0, 1) * 100:0}%)");
    public string StarsText
    {
        get
        {
            var stars = Model.AdjustedStars ?? Model.Stars;
            return stars is { } s ? Invariant($"{s:0.00}★") : "—★";
        }
    }
    public string WhenShort => LocalTimeDisplay.Time(Model.StartedAt);
    public string WhenLong => LocalTimeDisplay.TimeWithSeconds(Model.StartedAt, WhenShort);
    public string WhenText => LocalTimeDisplay.DateTime(Model.StartedAt, DisplayDateTime.UnknownDate);
    public string WhenRelative => LocalTimeDisplay.Relative(Model.StartedAt, fallback: WhenText);
    public string WhenExact => LocalTimeDisplay.DateTimeWithSeconds(Model.StartedAt, DisplayDateTime.UnknownDate);
    public string ImprovementLine => Model.IsPersonalBest ? "NEW BEST" : "";
    public string RowStatusLine
    {
        get
        {
            if (Model.IsPersonalBest)
            {
                return "NEW BEST";
            }

            var outcome = string.IsNullOrWhiteSpace(Model.Outcome) ? "" : Model.Outcome.ToUpperInvariant();
            if (outcome == "COMPLETED")
            {
                outcome = "COMPLETE";
            }
            var progress = Model.Progress > 0 && Model.Progress < 0.999
                ? Invariant($" {Model.Progress * 100:0.0}%")
                : "";
            var misses = Model.Misses > 0 ? Invariant($"  {Model.Misses} miss") : "";
            return $"{outcome}{progress}{misses}".Trim();
        }
    }
    public bool CanOpenReplayInspector => Model.HasMovement && BeatmapArtworkResolver.ResolveBeatmapFile(Model) is not null;
    public string QualityBadges
    {
        get
        {
            var badges = new List<string>();
            if (Model.HasMovement)
            {
                badges.Add("movement");
            }
            if (BeatmapArtworkResolver.ResolveBeatmapFile(Model) is not null)
            {
                badges.Add("media");
            }
            if (Model.Misses > 0)
            {
                badges.Add($"{Model.Misses} miss");
            }
            return badges.Count == 0 ? "" : string.Join("  ", badges);
        }
    }
    public string? ArtworkSource => BeatmapArtworkResolver.Resolve(Model);
    public string ArtworkBrush => (Id % 6) switch
    {
        0 => "#5B193F",
        1 => "#6E5A09",
        2 => "#173D57",
        3 => "#432B63",
        4 => "#2E4932",
        _ => "#4B2930",
    };
    public string GradeColor => Grade switch
    {
        "X" or "XH" or "SS" => "#DE31AE",
        "S" or "SH" => "#02B5C3",
        "A" => "#88DA20",
        "B" => "#E3B130",
        "C" => "#FF8E5D",
        "D" => "#FF5A5A",
        "F" => "#3F3F3F",
        _ => "#3F3F3F",
    };

    public string GradeTextColor => "#FFFFFF";

    public string OutcomeColor => Model.Outcome switch
    {
        "completed" => "#33F078",
        "failed" => "#FF4F7B",
        "retried" => "#FFD43B",
        "abandoned" or "quit" => "#A8A3C7",
        _ => "#F04EA3",
    };
}
