using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.Core.Models;
using Kumori.Storage;
using static System.FormattableString;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;

namespace Kumori.App;

internal static class PbScoreCardRenderer
{
    internal const int Width = 1120;
    internal const int Height = 620;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Semibold = new(
        new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Bold = new(
        new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static Task RenderAsync(
        AttemptDetails attempt,
        string playerName,
        int? beatmapRank,
        long scoreId,
        ScoreAlertProfileChange? profileChange,
        bool replayAttached,
        bool isTest,
        string? artworkPath,
        string? avatarPath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Render(
                    attempt,
                    playerName,
                    beatmapRank,
                    scoreId,
                    profileChange,
                    replayAttached,
                    isTest,
                    artworkPath,
                    avatarPath,
                    destination);
                completion.SetResult();
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Kumori top-play card renderer",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Render(
        AttemptDetails attempt,
        string playerName,
        int? beatmapRank,
        long scoreId,
        ScoreAlertProfileChange? profileChange,
        bool replayAttached,
        bool isTest,
        string? artworkPath,
        string? avatarPath,
        string destination)
    {
        AttemptSummary summary = attempt.Summary;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            var full = new Rect(0, 0, Width, Height);
            drawing.DrawRectangle(Brush("#100A0E"), null, full);
            var hero = new Rect(8, 8, Width - 16, 158);
            DrawHeroArtwork(drawing, artworkPath, hero);
            drawing.DrawRoundedRectangle(
                null,
                Pen("#78415D", 1.5),
                new Rect(hero.X + 0.5, hero.Y + 0.5, hero.Width - 1, hero.Height - 1),
                8,
                8);

            string heading = beatmapRank is > 0
                ? Invariant($"NEW #{beatmapRank:N0} BEST PLAY")
                : isTest ? "TOP-PLAY ALERT PREVIEW" : "NEW TOP PLAY";
            var titlePlate = new Rect(20, 20, 430, 100);
            drawing.DrawRoundedRectangle(Brush("#D90F090D"), Pen("#5D3448", 1), titlePlate, 7, 7);
            DrawPlayerAvatar(drawing, avatarPath, playerName, new Rect(32, 43, 56, 56));
            DrawText(drawing, heading, 100, 29, 11, "#FF7AAF", Bold, 330);
            DrawText(drawing, $"{summary.Artist} — {summary.Title}", 100, 50, 21, "#FFFFFF", Bold, 330);
            DrawText(
                drawing,
                $"[{Fallback(summary.Difficulty, "Unknown")}] · played by {Fallback(playerName, "Kumori user")}",
                100,
                80,
                13,
                "#E2D4DC",
                Regular,
                330);

            DrawText(drawing, $"mapped by {Fallback(attempt.Mapper, summary.Mapper, "Unknown")}", 20, 137, 12, "#FFFFFF", Semibold, 360);
            string outcome = string.Equals(summary.Outcome, "completed", StringComparison.OrdinalIgnoreCase)
                ? "COMPLETED"
                : Invariant($"{summary.Progress:P0}");
            DrawCenteredText(drawing, outcome, Width / 2, 137, 12, "#FFFFFF", Bold, 280);
            DrawRightText(drawing, DateText(summary.EndedAt ?? summary.StartedAt), Width - 20, 137, 12, "#FFFFFF", Semibold, 390);

            DrawMapStatStrip(drawing, attempt);
            DrawScoreDetailsTable(drawing, attempt);
            DrawProgression(drawing, profileChange);
            DrawFooterStrip(drawing, attempt, replayAttached);
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }

    private static void DrawHeroArtwork(DrawingContext drawing, string? artworkPath, Rect bounds)
    {
        drawing.DrawRoundedRectangle(Brush("#25131D"), null, bounds, 12, 12);
        if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.GetFullPath(artworkPath), UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                var artwork = new ImageBrush(image)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                    Opacity = 0.66,
                };
                artwork.Freeze();
                drawing.PushClip(new RectangleGeometry(bounds, 12, 12));
                drawing.DrawRectangle(artwork, null, bounds);
                drawing.Pop();
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException)
            {
                // The card remains useful with the themed fallback background.
            }
        }

        var shade = new LinearGradientBrush(
            MediaColor.FromArgb(245, 19, 12, 17),
            MediaColor.FromArgb(80, 19, 12, 17),
            new WpfPoint(0, 0.5),
            new WpfPoint(1, 0.5));
        shade.Freeze();
        drawing.DrawRoundedRectangle(shade, null, bounds, 12, 12);
    }

    private static void DrawRank(
        DrawingContext drawing,
        string grade,
        double progress,
        double x,
        double y)
    {
        string normalized = grade.Trim().ToUpperInvariant();
        string letter = normalized switch
        {
            "X" or "XH" or "SS" => "SS",
            "SH" => "S",
            "" => "—",
            _ => normalized,
        };
        double filled = Math.Clamp(progress, 0, 1);
        var center = new WpfPoint(x, y);
        const double radius = 23;
        drawing.DrawEllipse(Brush("#160D12"), null, center, radius - 1.4, radius - 1.4);
        drawing.DrawEllipse(null, Pen("#593044", 4.4), center, radius - 2.2, radius - 2.2);
        if (filled > 0.002)
            drawing.DrawGeometry(null, Pen("#F45B9B", 4.4), RankArc(center, radius - 2.2, 0, filled));

        double innerRadius = radius - 9.1;
        drawing.DrawEllipse(null, Pen("#160D12", 4.4), center, innerRadius, innerRadius);
        (double Start, double End, double Opacity)[] segments =
        [
            (0.00, 0.70, 0.40), (0.70, 0.80, 0.52), (0.80, 0.90, 0.64),
            (0.90, 0.95, 0.76), (0.95, 0.99, 0.88), (0.99, 1.00, 1.00),
        ];
        foreach ((double start, double end, double opacity) in segments)
        {
            const double spacing = 2d / 360d;
            double segmentStart = Math.Min(1, start + spacing * 0.5);
            double segmentEnd = Math.Min(filled, end - spacing * 0.5);
            if (segmentEnd <= segmentStart)
                continue;
            var accent = Brush("#F45B9B").Clone();
            accent.Opacity = opacity;
            accent.Freeze();
            var pen = new MediaPen(accent, 2.6);
            pen.Freeze();
            drawing.DrawGeometry(null, pen, RankArc(center, innerRadius, segmentStart, segmentEnd));
        }
        drawing.DrawEllipse(null, Pen("#593044", 1.2), center, innerRadius - 3.1, innerRadius - 3.1);
        DrawCenteredText(drawing, letter, x, y - (letter.Length > 1 ? 6 : 8), letter.Length > 1 ? 9.5 : 13, "#FFFFFF", Semibold, 34);
    }

    private static Geometry RankArc(WpfPoint center, double radius, double startProgress, double endProgress)
    {
        if (endProgress - startProgress >= 0.999)
            return new EllipseGeometry(center, radius, radius);
        WpfPoint Point(double progress)
        {
            double angle = (-90 + progress * 360) * Math.PI / 180;
            return new WpfPoint(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        }
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(Point(startProgress), false, false);
            context.ArcTo(
                Point(endProgress),
                new System.Windows.Size(radius, radius),
                0,
                endProgress - startProgress > 0.5,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawPlayerAvatar(
        DrawingContext drawing,
        string? avatarPath,
        string playerName,
        Rect bounds)
    {
        var center = new WpfPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        double radius = Math.Min(bounds.Width, bounds.Height) / 2;
        drawing.DrawEllipse(Brush("#24141D"), Pen("#F45B9B", 2), center, radius, radius);
        if (!string.IsNullOrWhiteSpace(avatarPath) && File.Exists(avatarPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.GetFullPath(avatarPath), UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                drawing.PushClip(new EllipseGeometry(center, radius - 2, radius - 2));
                drawing.DrawRectangle(brush, null, bounds);
                drawing.Pop();
                return;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException)
            {
            }
        }

        string initial = Fallback(playerName, "K")[..1].ToUpperInvariant();
        DrawCenteredText(drawing, initial, center.X, center.Y - 15, 24, "#F45B9B", Bold, bounds.Width - 12);
    }

    private static double DrawMods(DrawingContext drawing, AttemptDetails attempt, double x, double y)
    {
        IEnumerable<string> mods = (attempt.Mods.Count > 0 ? attempt.Mods : attempt.Summary.Mods)
            .Select(mod => mod.Acronym);
        return ScoreCardModRenderer.Draw(
            drawing,
            mods,
            EffectiveBpm(attempt),
            x,
            y,
            485);
    }

    private static void DrawMapStatStrip(DrawingContext drawing, AttemptDetails attempt)
    {
        AttemptSummary summary = attempt.Summary;
        double? adjustedStars = summary.AdjustedStars ?? summary.Stars ?? attempt.AdjustedStars;
        double? baseStars = attempt.BaseStars ?? summary.Stars;
        (string Label, string Value, string Accent)[] stats =
        [
            ("CS", DifficultyNumber(attempt, "cs", attempt.BeatmapCs), "#D8A0BE"),
            ("AR", DifficultyNumber(attempt, "ar", attempt.BeatmapAr), "#D8A0BE"),
            ("OD", DifficultyNumber(attempt, "od", attempt.BeatmapOd), "#D8A0BE"),
            ("BPM", BpmNumber(attempt), "#D8A0BE"),
            ("LENGTH", DurationText(attempt.DurationSeconds), "#D8A0BE"),
            ("DIFFICULTY", StarNumber(adjustedStars, baseStars), "#B08BFF"),
        ];

        var rect = new Rect(8, 174, Width - 16, 56);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), rect, 8, 8);
        double width = rect.Width / stats.Length;
        for (int index = 0; index < stats.Length; index++)
        {
            double left = rect.Left + index * width;
            if (index > 0)
                drawing.DrawLine(Pen("#422635", 1), new WpfPoint(left, rect.Top), new WpfPoint(left, rect.Bottom));
            DrawCenteredText(drawing, stats[index].Label, left + width / 2, rect.Top + 8, 10, stats[index].Accent, Semibold, width - 16);
            DrawCenteredText(drawing, stats[index].Value, left + width / 2, rect.Top + 27, 15, stats[index].Accent == "#B08BFF" ? "#B08BFF" : "#FFFFFF", Semibold, width - 16);
        }
    }

    private static void DrawScoreDetailsTable(DrawingContext drawing, AttemptDetails attempt)
    {
        AttemptSummary summary = attempt.Summary;
        var panel = new Rect(8, 238, Width - 16, 216);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), panel, 8, 8);
        double cellWidth = panel.Width / 4;
        const double performanceBottom = 314;
        const double judgementsBottom = 376;
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(panel.Left, performanceBottom), new WpfPoint(panel.Right, performanceBottom));
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(panel.Left, judgementsBottom), new WpfPoint(panel.Right, judgementsBottom));
        for (int index = 1; index < 4; index++)
        {
            double left = panel.Left + index * cellWidth;
            drawing.DrawLine(Pen("#422635", 1), new WpfPoint(left, panel.Top), new WpfPoint(left, performanceBottom));
            if (index < 3)
                drawing.DrawLine(Pen("#422635", 1), new WpfPoint(left, judgementsBottom), new WpfPoint(left, panel.Bottom));
        }

        double gradeCenter = panel.Left + cellWidth / 2;
        DrawRank(drawing, summary.Grade ?? "—", summary.Accuracy / 100d, gradeCenter, panel.Top + 31);
        DrawCenteredText(drawing, "GRADE", gradeCenter, panel.Top + 59, 10, "#A98D9C", Semibold, cellWidth - 24);

        int maxCombo = summary.BeatmapMaxCombo > 0
            ? summary.BeatmapMaxCombo
            : attempt.BeatmapMaxCombo;
        string combo = maxCombo > 0
            ? Invariant($"{summary.Combo:N0} / {maxCombo:N0}x")
            : Invariant($"{summary.Combo:N0}x");
        (string Label, string Value)[] primary =
        [
            ("ACCURACY", Invariant($"{summary.Accuracy:0.00}%")),
            ("SCORE", Invariant($"{summary.Score:N0}")),
            ("COMBO", combo),
        ];
        for (int index = 0; index < primary.Length; index++)
        {
            double center = panel.Left + (index + 1.5) * cellWidth;
            DrawCenteredText(drawing, primary[index].Label, center, panel.Top + 13, 10, "#A98D9C", Semibold, cellWidth - 24);
            DrawCenteredText(drawing, primary[index].Value, center, panel.Top + 38, 19, "#FFFFFF", Semibold, cellWidth - 24);
        }

        DrawJudgements(drawing, attempt, new Rect(panel.Left, performanceBottom, panel.Width, judgementsBottom - performanceBottom));
        DrawScoreDataRow(drawing, attempt, new Rect(panel.Left, judgementsBottom, panel.Width, panel.Bottom - judgementsBottom));
    }

    private static void DrawScoreDataRow(DrawingContext drawing, AttemptDetails attempt, Rect rect)
    {
        double width = rect.Width / 4;
        DrawText(drawing, "MODS", rect.Left + 14, rect.Top + 9, 10, "#A98D9C", Semibold, width - 28);
        IEnumerable<string> mods = (attempt.Mods.Count > 0 ? attempt.Mods : attempt.Summary.Mods)
            .Select(mod => mod.Acronym);
        _ = ScoreCardModRenderer.Draw(drawing, mods, EffectiveBpm(attempt), rect.Left + 14, rect.Top + 31, width - 28, compact: true);

        double performanceLeft = rect.Left + width;
        DrawCenteredText(drawing, "PP", performanceLeft + width / 2, rect.Top + 15, 10, "#A98D9C", Semibold, width - 28);
        DrawCenteredText(drawing, Invariant($"{attempt.Summary.Pp:0.##}pp"), performanceLeft + width / 2, rect.Top + 38, 17, "#FFFFFF", Semibold, width - 28);

        int key1 = attempt.Input?.Key1Presses ?? attempt.Key1Count;
        int key2 = attempt.Input?.Key2Presses ?? attempt.Key2Count;
        DrawCenteredText(
            drawing,
            "KEY PRESSES",
            rect.Left + width * 3,
            rect.Top + 12,
            10,
            "#A98D9C",
            Semibold,
            width * 2 - 32);
        DrawCenteredText(
            drawing,
            Invariant($"K1 {key1:N0}  ·  K2 {key2:N0}"),
            rect.Left + width * 3,
            rect.Top + 36,
            17,
            "#FFFFFF",
            Semibold,
            width * 2 - 32);
    }

    private static void DrawProgression(DrawingContext drawing, ScoreAlertProfileChange? change)
    {
        var panel = new Rect(8, 462, Width - 16, 70);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), panel, 8, 8);
        double width = panel.Width / 2;
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(panel.Left + width, panel.Top), new WpfPoint(panel.Left + width, panel.Bottom));
        DrawProgressCell(
            drawing,
            new Rect(panel.Left, panel.Top, width, panel.Height),
            "GLOBAL RANK",
            RankTransition(change),
            RankDelta(change),
            "#8D7BFF");
        DrawProgressCell(
            drawing,
            new Rect(panel.Left + width, panel.Top, width, panel.Height),
            "TOTAL PERFORMANCE",
            PpTransition(change),
            PpDelta(change),
            "#F45B9B");
    }

    private static void DrawProgressCell(
        DrawingContext drawing,
        Rect rect,
        string label,
        string transition,
        string delta,
        string accent)
    {
        DrawText(drawing, label, rect.X + 18, rect.Y + 12, 10, accent, Bold, rect.Width - 36);
        DrawText(drawing, transition, rect.X + 18, rect.Y + 32, 19, "#FFFFFF", Bold, rect.Width - 190);
        DrawRightText(drawing, delta, rect.Right - 18, rect.Y + 35, 13, accent, Semibold, 170);
    }

    private static string RankTransition(ScoreAlertProfileChange? change)
        => change?.OldGlobalRank is { } oldRank && change.NewGlobalRank is { } newRank
            ? Invariant($"#{oldRank:N0}  →  #{newRank:N0}")
            : "Not captured";

    private static string RankDelta(ScoreAlertProfileChange? change)
        => change?.RanksGained switch
        {
            > 0 and var gained => Invariant($"▲ {gained:N0} ranks"),
            < 0 and var lost => Invariant($"▼ {Math.Abs(lost):N0} ranks"),
            0 => "No change",
            _ => "Profile telemetry",
        };

    private static string PpTransition(ScoreAlertProfileChange? change)
        => change?.OldTotalPp is { } oldPp && change.NewTotalPp is { } newPp
            ? Invariant($"{oldPp:N2}  →  {newPp:N2}pp")
            : "Not captured";

    private static string PpDelta(ScoreAlertProfileChange? change)
        => change?.PpGained switch
        {
            > 0 and var gained => Invariant($"+{gained:0.00}pp"),
            < 0 and var lost => Invariant($"{lost:0.00}pp"),
            0 => "No change",
            _ => "Profile telemetry",
        };

    private static void DrawJudgements(DrawingContext drawing, AttemptDetails attempt, Rect rect)
    {
        bool stable = attempt.ClientKind.Equals("stable", StringComparison.OrdinalIgnoreCase);
        string unavailable = "—";
        (string Label, string Value, string Accent)[] values =
        [
            ("300", Invariant($"{attempt.N300:N0}"), "#55D7E3"),
            ("100", Invariant($"{attempt.N100:N0}"), "#76E57B"),
            ("50", Invariant($"{attempt.N50:N0}"), "#F0C45C"),
            ("MISS", Invariant($"{attempt.Summary.Misses:N0}"), "#FF6879"),
            ("SLIDER BREAKS", stable ? unavailable : Invariant($"{attempt.SliderBreaks:N0}"), "#FF5C9E"),
            ("LARGE TICK", stable ? unavailable : HitTotal(attempt.LargeTickHits, attempt.LargeTickMisses), "#B08BFF"),
            ("SLIDER TAIL", stable ? unavailable : HitTotal(attempt.SliderTailHits, attempt.SliderTailMisses), "#76E57B"),
        ];

        double cellWidth = rect.Width / values.Length;
        for (int index = 0; index < values.Length; index++)
        {
            double left = rect.Left + index * cellWidth;
            double center = left + cellWidth / 2;
            if (index > 0)
            {
                drawing.DrawLine(
                    Pen("#422635", 1),
                    new WpfPoint(left, rect.Top + 1),
                    new WpfPoint(left, rect.Bottom - 1));
            }
            DrawCenteredText(drawing, values[index].Label, center, rect.Top + 10, 9.5, values[index].Accent, Bold, cellWidth - 16);
            DrawCenteredText(drawing, values[index].Value, center, rect.Top + 32, 17, "#FFFFFF", Semibold, cellWidth - 16);
        }
    }

    private static string HitTotal(int hits, int misses)
        => Invariant($"{hits:N0}/{hits + misses:N0}");

    private static void DrawFooterStrip(DrawingContext drawing, AttemptDetails attempt, bool replayAttached)
    {
        var rect = new Rect(8, 540, Width - 16, 60);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), rect, 8, 8);
        double width = rect.Width / 3;
        string timing = attempt.UnstableRate > 0
            ? attempt.Timing is { } summary
                ? Invariant($"UR {attempt.UnstableRate:0.0} · {TimingBias(summary.Mean)}")
                : Invariant($"UR {attempt.UnstableRate:0.0}")
            : "Unavailable";
        int totalPresses = (attempt.Input?.Key1Presses ?? attempt.Key1Count)
            + (attempt.Input?.Key2Presses ?? attempt.Key2Count);
        string input = attempt.Input?.PeakKps is > 0 and var peak
            ? Invariant($"{totalPresses:N0} total · {peak:N0} peak KPS")
            : Invariant($"{totalPresses:N0} total presses");
        (string Label, string Value, string Accent)[] cells =
        [
            ("HIT TIMING", timing, "#A379FF"),
            ("TOTAL INPUT", input, "#55D7E3"),
            ("REPLAY", replayAttached ? "Attached below" : "Attachment unavailable", replayAttached ? "#76E57B" : "#FF6879"),
        ];

        for (int index = 0; index < cells.Length; index++)
        {
            double left = rect.Left + index * width;
            if (index > 0)
                drawing.DrawLine(Pen("#422635", 1), new WpfPoint(left, rect.Top), new WpfPoint(left, rect.Bottom));
            DrawText(drawing, cells[index].Label, left + 16, rect.Top + 9, 10, cells[index].Accent, Bold, width - 32);
            DrawText(drawing, cells[index].Value, left + 16, rect.Top + 28, 15, "#FFFFFF", Semibold, width - 32);
        }
    }

    internal static string MapSettings(AttemptDetails attempt)
    {
        string[] values =
        [
            DifficultyValue("AR", attempt, "ar", attempt.BeatmapAr),
            DifficultyValue("OD", attempt, "od", attempt.BeatmapOd),
            DifficultyValue("CS", attempt, "cs", attempt.BeatmapCs),
            BpmValue(attempt),
        ];
        string available = string.Join(" · ", values.Where(value => value.Length > 0));
        return available.Length > 0 ? available : "Unavailable";
    }

    private static string DifficultyValue(
        string label,
        AttemptDetails attempt,
        string key,
        double? fallback)
    {
        if (!attempt.CapturedDifficulty.TryGetValue(key, out DifficultyPair captured))
            return AdjustedValue(label, fallback, fallback);

        double? original = captured.Original ?? fallback;
        double? converted = captured.Converted ?? original;
        return AdjustedValue(label, converted, original);
    }

    private static string DifficultyNumber(
        AttemptDetails attempt,
        string key,
        double? fallback)
    {
        if (!attempt.CapturedDifficulty.TryGetValue(key, out DifficultyPair captured))
            return fallback is { } value ? Invariant($"{value:0.#}") : "—";
        return AdjustedNumber(captured.Converted ?? captured.Original ?? fallback, captured.Original ?? fallback);
    }

    private static string BpmValue(AttemptDetails attempt)
    {
        double? original = attempt.CapturedDifficulty.TryGetValue("bpm", out DifficultyPair captured)
            ? captured.Original ?? attempt.Bpm
            : attempt.Bpm;
        return AdjustedValue("BPM", EffectiveBpm(attempt), original);
    }

    private static string BpmNumber(AttemptDetails attempt)
    {
        double? original = attempt.CapturedDifficulty.TryGetValue("bpm", out DifficultyPair captured)
            ? captured.Original ?? attempt.Bpm
            : attempt.Bpm;
        return AdjustedNumber(EffectiveBpm(attempt), original);
    }

    private static string AdjustedNumber(double? current, double? original)
    {
        if (current is not { } value)
            return "—";
        return original is { } previous && Math.Abs(value - previous) > 0.01
            ? Invariant($"{value:0.#} ({previous:0.#})")
            : Invariant($"{value:0.#}");
    }

    private static string StarNumber(double? current, double? original)
    {
        if (current is not { } value)
            return "—";
        return original is { } previous && Math.Abs(value - previous) > 0.005
            ? Invariant($"{value:0.00} ({previous:0.00})★")
            : Invariant($"{value:0.00}★");
    }

    private static string DurationText(double seconds)
    {
        if (seconds <= 0 || !double.IsFinite(seconds))
            return "—";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? Invariant($"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : Invariant($"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}");
    }

    private static string AdjustedValue(string label, double? current, double? original)
    {
        if (current is not { } value)
            return "";
        return original is { } previous && Math.Abs(value - previous) > 0.01
            ? Invariant($"{label}{value:0.#} ({previous:0.#})")
            : Invariant($"{label}{value:0.#}");
    }

    private static string InputText(AttemptDetails attempt)
    {
        int key1 = attempt.Input?.Key1Presses ?? attempt.Key1Count;
        int key2 = attempt.Input?.Key2Presses ?? attempt.Key2Count;
        string presses = key1 > 0 || key2 > 0
            ? Invariant($"{attempt.Key1Binding} {key1:N0} · {attempt.Key2Binding} {key2:N0}")
            : "Unavailable";
        return attempt.Input?.PeakKps is > 0 and var peak
            ? Invariant($"{presses} · {peak:N0} peak KPS")
            : presses;
    }

    private static double? EffectiveBpm(AttemptDetails attempt)
    {
        if (attempt.CapturedDifficulty.TryGetValue("bpm", out DifficultyPair captured)
            && captured.Converted is > 0)
            return captured.Converted;
        foreach (ModEntry mod in attempt.Mods.Concat(attempt.Summary.Mods))
        {
            if (!mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(mod.SettingsJson);
                if (document.RootElement.TryGetProperty("target_bpm", out JsonElement value)
                    && value.TryGetDouble(out double bpm)
                    && bpm > 0)
                    return bpm;
            }
            catch (JsonException)
            {
            }
        }
        if (attempt.Bpm is not > 0)
            return null;
        string[] acronyms = attempt.Mods.Concat(attempt.Summary.Mods)
            .Select(mod => mod.Acronym.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (acronyms.Any(mod => mod is "DT" or "NC"))
            return attempt.Bpm * 1.5;
        if (acronyms.Any(mod => mod is "HT" or "DC"))
            return attempt.Bpm * 0.75;
        return attempt.Bpm;
    }

    private static string TimingBias(double mean)
        => Math.Abs(mean) < 0.05
            ? "centered"
            : Invariant($"{Math.Abs(mean):0.0}ms {(mean < 0 ? "early" : "late")}");

    private static string DateText(string? timestamp)
        => DateTimeOffset.TryParse(timestamp, Culture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? $"Played {value.ToLocalTime():d MMM yyyy HH:mm}"
            : "Played recently";

    private static string Join(params string?[] values)
        => string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string Fallback(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static void DrawText(
        DrawingContext drawing,
        string text,
        double x,
        double y,
        double size,
        string color,
        Typeface typeface,
        double maxWidth)
    {
        FormattedText formatted = Format(text, size, color, typeface, maxWidth);
        drawing.DrawText(formatted, new WpfPoint(x, y));
    }

    private static void DrawRightText(
        DrawingContext drawing,
        string text,
        double right,
        double y,
        double size,
        string color,
        Typeface typeface,
        double maxWidth)
    {
        FormattedText formatted = Format(text, size, color, typeface, maxWidth);
        drawing.DrawText(formatted, new WpfPoint(right - Math.Min(maxWidth, formatted.Width), y));
    }

    private static void DrawCenteredText(
        DrawingContext drawing,
        string text,
        double center,
        double y,
        double size,
        string color,
        Typeface typeface,
        double maxWidth)
    {
        FormattedText formatted = Format(text, size, color, typeface, maxWidth);
        drawing.DrawText(formatted, new WpfPoint(center - formatted.Width / 2, y));
    }

    private static FormattedText Format(
        string text,
        double size,
        string color,
        Typeface typeface,
        double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            Culture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brush(color),
            1)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return formatted;
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static MediaPen Pen(string value, double thickness)
    {
        var pen = new MediaPen(Brush(value), thickness);
        pen.Freeze();
        return pen;
    }
}
