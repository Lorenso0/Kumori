using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.Core.Models;
using static System.FormattableString;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;

namespace Kumori.App;

internal static class DailyStatsCardRenderer
{
    internal const int Width = 1120;
    internal const int Height = 638;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Semibold = new(
        new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Bold = new(
        new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static Task RenderAsync(
        DailyProgressReport report,
        bool isTest,
        string? avatarPath,
        string? countryFlagPath,
        string? bannerPath,
        string? bestArtworkPath,
        string? mostPlayedArtworkPath,
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
                    report,
                    isTest,
                    avatarPath,
                    countryFlagPath,
                    bannerPath,
                    bestArtworkPath,
                    mostPlayedArtworkPath,
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
            Name = "Kumori daily card renderer",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Render(
        DailyProgressReport report,
        bool isTest,
        string? avatarPath,
        string? countryFlagPath,
        string? bannerPath,
        string? bestArtworkPath,
        string? mostPlayedArtworkPath,
        string destination)
    {
        DailyAttemptTrend summary = report.Summary;
        DailyAccountProgress? account = report.Account;
        string playerName = First(account?.PlayerName, report.PlayerName, "Kumori user");
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brush("#100A0E"), null, new Rect(0, 0, Width, Height));
            DrawHeader(drawing, playerName, summary.Day, isTest, avatarPath, bannerPath);
            DrawDailyTotals(drawing, summary, account);
            DrawAccountProgress(drawing, summary, account, countryFlagPath);
            DrawHighlights(drawing, report, bestArtworkPath, mostPlayedArtworkPath);
            DrawFooter(drawing, report);
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

    private static void DrawHeader(
        DrawingContext drawing,
        string playerName,
        string day,
        bool isTest,
        string? avatarPath,
        string? bannerPath)
    {
        var rect = new Rect(8, 8, Width - 16, 128);
        var gradient = new LinearGradientBrush(
            MediaColor.FromRgb(55, 21, 42),
            MediaColor.FromRgb(24, 14, 22),
            new WpfPoint(0, 0),
            new WpfPoint(1, 1));
        gradient.Freeze();
        drawing.DrawRoundedRectangle(gradient, null, rect, 8, 8);
        if (TryDrawImageFill(drawing, bannerPath, rect, 8))
        {
            var coverShade = new LinearGradientBrush(
                MediaColor.FromArgb(235, 20, 9, 16),
                MediaColor.FromArgb(150, 24, 12, 20),
                new WpfPoint(0, 0),
                new WpfPoint(1, 0));
            coverShade.Freeze();
            drawing.DrawRoundedRectangle(coverShade, null, rect, 8, 8);
        }
        drawing.DrawRoundedRectangle(null, Pen("#824363", 1.5), rect, 8, 8);
        DrawAvatar(drawing, avatarPath, 70, 72);
        DrawText(drawing, "KUMORI  ·  DAILY OSU! RECAP", 134, 31, 11, "#FF7AAF", Bold, 500);
        DrawText(drawing, $"{playerName}'s day in review", 134, 54, 28, "#FFFFFF", Bold, 730);
        DrawText(drawing, DisplayDay(day), 136, 96, 14, "#D8C5CF", Semibold, 430);
        DrawRightText(
            drawing,
            isTest ? "TEST PREVIEW" : "PLAYTIME IS IN-MAP TIME ONLY",
            Width - 24,
            101,
            11,
            isTest ? "#FF7AAF" : "#A9929E",
            Bold,
            360);
    }

    private static void DrawAvatar(DrawingContext drawing, string? avatarPath, double x, double y)
    {
        var center = new WpfPoint(x, y);
        drawing.DrawEllipse(Brush("#21151C"), Pen("#FF6CA8", 3), center, 44, 44);
        if (string.IsNullOrWhiteSpace(avatarPath) || !File.Exists(avatarPath))
        {
            DrawCenteredText(drawing, "K", x, y - 20, 31, "#FF7AAF", Bold, 70);
            return;
        }

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
            drawing.PushClip(new EllipseGeometry(center, 40, 40));
            drawing.DrawEllipse(brush, null, center, 40, 40);
            drawing.Pop();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            DrawCenteredText(drawing, "K", x, y - 20, 31, "#FF7AAF", Bold, 70);
        }
    }

    private static bool TryDrawImage(DrawingContext drawing, string? path, Rect bounds)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            drawing.DrawImage(image, bounds);
            return true;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDrawImageFill(
        DrawingContext drawing,
        string? path,
        Rect bounds,
        double cornerRadius)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            brush.Freeze();
            drawing.PushClip(new RectangleGeometry(bounds, cornerRadius, cornerRadius));
            drawing.DrawRectangle(brush, null, bounds);
            drawing.Pop();
            return true;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void DrawDailyTotals(
        DrawingContext drawing,
        DailyAttemptTrend summary,
        DailyAccountProgress? account)
    {
        double completion = summary.Attempts == 0 ? 0 : summary.Completed * 100d / summary.Attempts;
        string official = account?.OldPlayCount is { } oldPlays && account.NewPlayCount is { } newPlays
            ? Invariant($"{Signed(newPlays - oldPlays)} official")
            : "Official unavailable";
        (string Label, string Value, string Detail, string Accent)[] stats =
        [
            ("PLAYS", Invariant($"{summary.Attempts:N0}"), official, "#F45B9B"),
            ("COMPLETED", Invariant($"{summary.Completed:N0}"), Invariant($"{completion:0}% completion"), "#8ADA7A"),
            ("AVERAGE ACCURACY", Invariant($"{summary.AverageAccuracy:0.00}%"), "Across local plays", "#FFFFFF"),
            ("PLAYTIME", FormatPlaytime(summary.TotalDurationSeconds), "In-map time", "#A379FF"),
            ("DISTINCT MAPS", Invariant($"{summary.DistinctMaps:N0}"), "Unique beatmaps", "#F45B9B"),
        ];

        var panel = new Rect(8, 144, Width - 16, 96);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), panel, 8, 8);
        double width = panel.Width / stats.Length;
        for (int index = 0; index < stats.Length; index++)
        {
            double left = panel.Left + index * width;
            if (index > 0)
                drawing.DrawLine(Pen("#422635", 1), new WpfPoint(left, panel.Top), new WpfPoint(left, panel.Bottom));
            DrawText(drawing, stats[index].Label, left + 14, panel.Top + 13, 9.5, "#A98D9C", Semibold, width - 28);
            DrawText(drawing, stats[index].Value, left + 14, panel.Top + 36, 23, stats[index].Accent, Bold, width - 28);
            DrawText(drawing, stats[index].Detail, left + 14, panel.Top + 72, 9, "#A9929E", Regular, width - 28);
        }
    }

    private static void DrawAccountProgress(
        DrawingContext drawing,
        DailyAttemptTrend summary,
        DailyAccountProgress? account,
        string? countryFlagPath)
    {
        string rank = account?.OldGlobalRank is { } oldRank && account.NewGlobalRank is { } newRank
            ? Invariant($"#{oldRank:N0}  →  #{newRank:N0}")
            : summary.RankChange is { } rankChange ? Signed(rankChange) : "Not captured";
        string rankDelta = account?.OldGlobalRank is { } oldGlobal && account.NewGlobalRank is { } newGlobal
            ? RankDeltaText(oldGlobal - newGlobal)
            : summary.RankChange is { } fallbackRank ? RankDeltaText(fallbackRank) : "Daily change";
        long? rankDirection = account?.OldGlobalRank is { } oldGlobalDirection
                              && account.NewGlobalRank is { } newGlobalDirection
            ? oldGlobalDirection - newGlobalDirection
            : summary.RankChange;
        string country = account?.OldCountryRank is { } oldCountry && account.NewCountryRank is { } newCountry
            ? Invariant($"#{oldCountry:N0}  →  #{newCountry:N0}")
            : "Not captured";
        string countryDelta = account?.OldCountryRank is { } oldCr && account.NewCountryRank is { } newCr
            ? RankDeltaText(oldCr - newCr)
            : "Country rank";
        long? countryDirection = account?.OldCountryRank is { } oldCountryDirection
                                 && account.NewCountryRank is { } newCountryDirection
            ? oldCountryDirection - newCountryDirection
            : null;
        string pp = account?.OldTotalPp is { } oldPp && account.NewTotalPp is { } newPp
            ? Invariant($"{oldPp:N1}  →  {newPp:N1}pp")
            : summary.PpChange is { } ppChange ? Invariant($"{Signed(ppChange, 1)}pp") : "Not captured";
        string ppDelta = account?.OldTotalPp is { } priorPp && account.NewTotalPp is { } currentPp
            ? PpDeltaText(currentPp - priorPp)
            : summary.PpChange is { } fallbackPp ? PpDeltaText(fallbackPp) : "Daily change";
        double? ppDirection = account?.OldTotalPp is { } oldPpDirection
                              && account.NewTotalPp is { } newPpDirection
            ? newPpDirection - oldPpDirection
            : summary.PpChange;
        var panel = new Rect(8, 248, Width - 16, 100);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), panel, 8, 8);
        double width = panel.Width / 3;
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(panel.Left + width, panel.Top), new WpfPoint(panel.Left + width, panel.Bottom));
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(panel.Left + width * 2, panel.Top), new WpfPoint(panel.Left + width * 2, panel.Bottom));
        DrawProgressCard(drawing, panel.Left, panel.Top, width, panel.Height, "GLOBAL RANK", rank, rankDelta, ChangeColor(rankDirection), "#8D7BFF");
        DrawProgressCard(
            drawing,
            panel.Left + width,
            panel.Top,
            width,
            panel.Height,
            "COUNTRY RANK",
            country,
            countryDelta,
            ChangeColor(countryDirection),
            "#55D7E3",
            countryFlagPath);
        DrawProgressCard(drawing, panel.Left + width * 2, panel.Top, width, panel.Height, "TOTAL PERFORMANCE", pp, ppDelta, ChangeColor(ppDirection), "#F45B9B");
    }

    private static void DrawProgressCard(
        DrawingContext drawing,
        double x,
        double y,
        double width,
        double height,
        string label,
        string value,
        string delta,
        string deltaColor,
        string accent,
        string? flagPath = null)
    {
        var rect = new Rect(x, y, width, height);
        drawing.DrawRoundedRectangle(Brush(accent), null, new Rect(x, rect.Y, 5, rect.Height), 3, 3);
        double labelX = x + 22;
        if (TryDrawImage(drawing, flagPath, new Rect(labelX, y + 17, 24, 16)))
            labelX += 34;
        DrawText(drawing, label, labelX, y + 17, 10, accent, Bold, width - 44 - (labelX - x - 22));
        DrawText(drawing, value, x + 22, y + 43, 20, "#FFFFFF", Bold, width - 44);
        DrawText(drawing, delta, x + 22, y + 73, 12, deltaColor, Bold, width - 44);
    }

    private static void DrawHighlights(
        DrawingContext drawing,
        DailyProgressReport report,
        string? bestArtworkPath,
        string? mostPlayedArtworkPath)
    {
        var panel = new Rect(8, 356, Width - 16, 174);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), panel, 8, 8);
        double bestWidth = panel.Width * 2 / 3;
        var bestRect = new Rect(panel.Left, panel.Top, bestWidth, panel.Height);
        var mostRect = new Rect(panel.Left + bestWidth, panel.Top, panel.Width - bestWidth, panel.Height);
        drawing.PushClip(new RectangleGeometry(panel, 8, 8));
        DrawHighlightArtwork(drawing, bestRect, bestArtworkPath);
        DrawHighlightArtwork(drawing, mostRect, mostPlayedArtworkPath);
        drawing.Pop();
        drawing.DrawRoundedRectangle(null, Pen("#4D2B3B", 1), panel, 8, 8);
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(mostRect.Left, panel.Top), new WpfPoint(mostRect.Left, panel.Bottom));
        DrawHighlightShell(drawing, bestRect, "HIGHEST ACHIEVED PP PLAY", "#F45B9B");
        if (report.BestPlay is { } best)
        {
            DrawFittedText(drawing, MapName(best.Artist, best.Title), bestRect.Left + 22, bestRect.Top + 39, 21, 8, "#FFFFFF", Bold, bestRect.Width - 44);
            DrawText(drawing, Difficulty(best.Difficulty), bestRect.Left + 22, bestRect.Top + 69, 14, "#D8C5CF", Semibold, bestRect.Width - 44);
            DrawFittedText(
                drawing,
                BestPlayMapStats(best),
                bestRect.Left + 22,
                bestRect.Top + 92,
                11,
                7,
                "#D8A0BE",
                Semibold,
                bestRect.Width - 44);
            IEnumerable<string> bestMods = ParseModAcronyms(best.ModsKey);
            if (!best.UsedBpmAdjust)
            {
                bestMods = bestMods.Where(mod =>
                    !mod.Equals("BPM", StringComparison.OrdinalIgnoreCase));
            }
            ScoreCardModRenderer.Draw(
                drawing,
                bestMods,
                best.UsedBpmAdjust ? best.Bpm : null,
                bestRect.Left + 22,
                bestRect.Top + 120,
                178,
                compact: true);
            double metricsLeft = bestRect.Left + 220;
            double metricAreaWidth = bestRect.Right - metricsLeft - 20;
            (string Label, string Value, string Color, double Weight)[] metrics =
            [
                ("PP", Invariant($"{best.Pp:0.0}pp"), "#FF82B5", 0.13),
                ("ACCURACY", Invariant($"{best.Accuracy:0.00}%"), "#FFFFFF", 0.15),
                ("COMBO", best.MaxCombo > 0 ? Invariant($"{best.Combo:N0}/{best.MaxCombo:N0}x") : Invariant($"{best.Combo:N0}x"), "#55D7E3", 0.23),
                ("100", Invariant($"{best.N100:N0}"), "#72E58B", 0.10),
                ("50", Invariant($"{best.N50:N0}"), "#FFD45C", 0.10),
                ("MISS", Invariant($"{best.Misses:N0}"), best.Misses == 0 ? "#72E58B" : "#FF6879", 0.11),
                ("SLIDER BREAKS", Invariant($"{best.SliderBreaks:N0}"), best.SliderBreaks == 0 ? "#72E58B" : "#FF6879", 0.18),
            ];
            double metricX = metricsLeft;
            for (int index = 0; index < metrics.Length; index++)
            {
                double width = index == metrics.Length - 1
                    ? bestRect.Right - 20 - metricX
                    : metricAreaWidth * metrics[index].Weight;
                DrawHighlightMetric(
                    drawing,
                    new Rect(metricX, bestRect.Top + 118, width, 46),
                    metrics[index].Label,
                    metrics[index].Value,
                    metrics[index].Color);
                metricX += width;
            }
        }
        else
        {
            DrawText(drawing, "No PP play captured", bestRect.Left + 22, bestRect.Top + 55, 20, "#B9A3AE", Semibold, bestRect.Width - 44);
        }

        DrawHighlightShell(drawing, mostRect, "MOST PLAYED MAP", "#A379FF");
        if (report.MostPlayedMap is { } most)
        {
            DrawFittedText(drawing, MapName(most.Artist, most.Title), mostRect.Left + 22, mostRect.Top + 39, 19, 8, "#FFFFFF", Bold, mostRect.Width - 44);
            DrawText(drawing, Difficulty(most.Difficulty), mostRect.Left + 22, mostRect.Top + 69, 14, "#D8C5CF", Semibold, mostRect.Width - 44);
            DrawFittedText(
                drawing,
                MapStats(most),
                mostRect.Left + 22,
                mostRect.Top + 92,
                10.5,
                7,
                "#C6A7B7",
                Semibold,
                mostRect.Width - 44);
            DrawHighlightMetric(
                drawing,
                new Rect(mostRect.Left + 22, mostRect.Top + 118, 142, 46),
                "PLAYS",
                Invariant($"{most.Plays:N0}"),
                "#B79AFF");
        }
        else
        {
            DrawText(drawing, "No map highlight", mostRect.Left + 22, mostRect.Top + 55, 19, "#B9A3AE", Semibold, mostRect.Width - 44);
        }
    }

    private static void DrawHighlightArtwork(DrawingContext drawing, Rect rect, string? artworkPath)
    {
        if (!TryDrawImageFill(drawing, artworkPath, rect, 0))
            return;

        var shade = new LinearGradientBrush(
            MediaColor.FromArgb(238, 15, 8, 13),
            MediaColor.FromArgb(174, 19, 9, 16),
            new WpfPoint(0, 0),
            new WpfPoint(1, 0));
        shade.Freeze();
        drawing.DrawRectangle(shade, null, rect);
        drawing.DrawRectangle(Brush("#4210080D"), null, rect);
    }

    private static void DrawHighlightMetric(
        DrawingContext drawing,
        Rect rect,
        string label,
        string value,
        string accent)
    {
        drawing.DrawRectangle(Brush("#A8140D12"), Pen("#664D2B3B", 1), rect);
        DrawFittedText(drawing, label, rect.Left + 7, rect.Top + 6, 8.5, 6.5, "#A98D9C", Bold, rect.Width - 14);
        DrawFittedText(drawing, value, rect.Left + 7, rect.Top + 21, 15, 8, accent, Bold, rect.Width - 14);
    }

    private static void DrawHighlightShell(DrawingContext drawing, Rect rect, string label, string accent)
    {
        drawing.DrawRoundedRectangle(Brush(accent), null, new Rect(rect.X, rect.Y, 5, rect.Height), 3, 3);
        DrawText(drawing, label, rect.X + 22, rect.Y + 14, 10, accent, Bold, rect.Width - 44);
    }

    private static void DrawFooter(DrawingContext drawing, DailyProgressReport report)
    {
        DailyAttemptTrend summary = report.Summary;
        var panel = new Rect(8, 538, Width - 16, 92);
        drawing.DrawRoundedRectangle(Brush("#181015"), Pen("#4D2B3B", 1), panel, 8, 8);
        double inputWidth = panel.Width / 3;
        double modsLeft = panel.Left + inputWidth;
        drawing.DrawLine(Pen("#422635", 1), new WpfPoint(modsLeft, panel.Top), new WpfPoint(modsLeft, panel.Bottom));
        drawing.DrawRoundedRectangle(Brush("#55D7E3"), null, new Rect(panel.X, panel.Y, 5, panel.Height), 3, 3);
        DrawText(drawing, "DAILY INPUT", panel.Left + 22, panel.Top + 14, 10, "#55D7E3", Bold, inputWidth - 44);
        DrawCenteredText(drawing, Invariant($"K1 {summary.ZTotal:N0}  ·  K2 {summary.XTotal:N0}"), panel.Left + inputWidth / 2, panel.Top + 39, 17, "#FFFFFF", Bold, inputWidth - 44);
        DrawCenteredText(drawing, $"{summary.ZTotal + summary.XTotal:N0} presses  ·  {KeyBalance(summary.ZTotal, summary.XTotal)} balance", panel.Left + inputWidth / 2, panel.Top + 68, 9.5, "#A9929E", Semibold, inputWidth - 44);

        drawing.DrawRoundedRectangle(Brush("#A379FF"), null, new Rect(modsLeft, panel.Y, 5, panel.Height), 3, 3);
        DrawText(drawing, "MOST-USED MOD COMBINATIONS", modsLeft + 22, panel.Top + 10, 10, "#B79AFF", Bold, panel.Right - modsLeft - 44);
        DrawModCombinations(drawing, report.MostUsedModCombinations, modsLeft + 22, panel.Top + 31, panel.Right - modsLeft - 44);
    }

    private static void DrawModCombinations(
        DrawingContext drawing,
        IReadOnlyList<DailyModCombinationUsage> combinations,
        double x,
        double y,
        double width)
    {
        if (combinations.Count == 0)
        {
            DrawText(drawing, "No mod usage captured", x, y + 17, 15, "#B9A3AE", Semibold, width);
            return;
        }

        int count = Math.Min(3, combinations.Count);
        double slotWidth = width / count;
        for (int index = 0; index < count; index++)
        {
            DailyModCombinationUsage combination = combinations[index];
            double slotLeft = x + index * slotWidth;
            ScoreCardModRenderer.Draw(
                drawing,
                ParseModAcronyms(combination.ModsKey),
                null,
                slotLeft,
                y,
                slotWidth - 10,
                compact: true);
            DrawText(
                drawing,
                Invariant($"{combination.Plays:N0} play{(combination.Plays == 1 ? "" : "s")}"),
                slotLeft,
                y + 47,
                9.5,
                "#B79AFF",
                Semibold,
                slotWidth - 10);
        }
    }

    private static string KeyBalance(long key1, long key2)
    {
        long total = key1 + key2;
        return total > 0
            ? Invariant($"{key1 * 100d / total:0}% / {key2 * 100d / total:0}%")
            : "No input captured";
    }

    private static string RankDeltaText(long value) => value switch
    {
        > 0 => Invariant($"▲ {value:N0} ranks gained"),
        < 0 => Invariant($"▼ {Math.Abs(value):N0} ranks lost"),
        _ => "No rank change",
    };

    private static string PpDeltaText(double value) => value switch
    {
        > 0 => Invariant($"▲ +{value:0.0}pp gained"),
        < 0 => Invariant($"▼ {value:0.0}pp lost"),
        _ => "No PP change",
    };

    private static string ChangeColor(double? value) => value switch
    {
        > 0 => "#72E58B",
        < 0 => "#FF6879",
        _ => "#A9929E",
    };

    private static string First(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string MapName(string artist, string title)
        => $"{First(artist, "Unknown artist")} — {First(title, "Unknown title")}";

    private static string BestPlayMapStats(DailyPlayHighlight play)
    {
        var values = new List<string>();
        AddAdjustedStat(values, "★ ", play.AdjustedStars, play.BaseStars);
        AddAdjustedStat(values, "AR", play.AdjustedAr, play.BaseAr);
        AddAdjustedStat(values, "OD", play.AdjustedOd, play.BaseOd);
        AddAdjustedStat(values, "CS", play.AdjustedCs, play.BaseCs);
        AddAdjustedStat(values, "BPM", play.Bpm, play.BaseBpm, 0.05);
        return values.Count > 0 ? string.Join("  ·  ", values) : "Map settings unavailable";
    }

    private static string MapStats(DailyMapHighlight map)
    {
        var values = new List<string>();
        AddStat(values, "★ ", map.Stars);
        AddStat(values, "AR", map.Ar);
        AddStat(values, "OD", map.Od);
        AddStat(values, "CS", map.Cs);
        AddStat(values, "BPM", map.Bpm);
        return values.Count > 0 ? string.Join("  ·  ", values) : "Map settings unavailable";
    }

    private static void AddAdjustedStat(
        ICollection<string> values,
        string label,
        double? adjusted,
        double? original,
        double threshold = 0.005)
    {
        double? displayed = adjusted ?? original;
        if (displayed is not { } value || !double.IsFinite(value))
            return;
        string current = Invariant($"{value:0.##}");
        values.Add(original is { } baseValue
                   && double.IsFinite(baseValue)
                   && Math.Abs(value - baseValue) > threshold
            ? Invariant($"{label}{current} ({baseValue:0.##})")
            : $"{label}{current}");
    }

    private static void AddStat(ICollection<string> values, string label, double? stat)
    {
        if (stat is { } value && double.IsFinite(value))
            values.Add(Invariant($"{label}{value:0.##}"));
    }

    private static string Difficulty(string difficulty)
        => string.IsNullOrWhiteSpace(difficulty) ? "Unknown difficulty" : $"[{difficulty.Trim()}]";

    private static IEnumerable<string> ParseModAcronyms(string modsKey)
    {
        string normalized = (modsKey ?? "")
            .Trim()
            .TrimStart('+')
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
        if (normalized is "" or "NM")
            return ["NM"];
        if (normalized.Contains(','))
            return normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<string>();
        for (int index = 0; index < normalized.Length;)
        {
            if (normalized.AsSpan(index).StartsWith("BPM", StringComparison.Ordinal))
            {
                result.Add("BPM");
                index += 3;
                continue;
            }
            if (normalized.AsSpan(index).StartsWith("SV2", StringComparison.Ordinal))
            {
                result.Add("SV2");
                index += 3;
                continue;
            }
            int length = index + 3 <= normalized.Length
                         && char.IsDigit(normalized[index])
                         && char.IsDigit(normalized[index + 1])
                         && normalized[index + 2] == 'K'
                ? 3
                : Math.Min(2, normalized.Length - index);
            result.Add(normalized.Substring(index, length));
            index += length;
        }
        return result;
    }

    private static string MissText(long misses) => misses == 1 ? "miss" : "misses";

    private static string Signed(long value) => Invariant($"{value:+#,0;-#,0;0}");

    private static string Signed(double value, int decimals) => decimals == 1
        ? Invariant($"{value:+0.0;-0.0;0.0}")
        : Invariant($"{value:+0;-0;0}");

    private static string FormatPlaytime(double seconds)
    {
        long minutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        return minutes >= 60
            ? Invariant($"{minutes / 60}h {minutes % 60:00}m")
            : Invariant($"{minutes}m");
    }

    private static string DisplayDay(string day)
        => DateOnly.TryParseExact(day, "yyyy-MM-dd", Culture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed.ToString("dddd, d MMMM yyyy", Culture)
            : day;

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
        drawing.DrawText(Format(text, size, color, typeface, maxWidth), new WpfPoint(x, y));
    }

    private static void DrawFittedText(
        DrawingContext drawing,
        string text,
        double x,
        double y,
        double preferredSize,
        double minimumSize,
        string color,
        Typeface typeface,
        double maxWidth)
    {
        double size = preferredSize;
        FormattedText measured = Measure(text, size, color, typeface);
        if (measured.Width > maxWidth && measured.Width > 0)
            size = Math.Max(minimumSize, size * maxWidth / measured.Width * 0.96);

        drawing.DrawText(Format(text, size, color, typeface, maxWidth), new WpfPoint(x, y));
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
        => new(
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

    private static FormattedText Measure(
        string text,
        double size,
        string color,
        Typeface typeface)
        => new(
            text,
            Culture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brush(color),
            1);

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
