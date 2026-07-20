namespace Kumori.App.ViewModels;

/// <summary>
/// Pure display mapping from an osu! mod acronym to a category-tinted badge and the
/// matching lazer mod icon asset.
/// </summary>
internal static class ModBadgeInfo
{
    // Exact osu!lazer OsuColour.ForModType() values.
    private const string ColorReduction = "#B2FF66";   // Lime1
    private const string ColorIncrease = "#FF6666";    // Red1
    private const string ColorAutomation = "#66CCFF";  // Blue1
    private const string ColorConversion = "#8C66FF";  // Purple1
    private const string ColorFun = "#FF66AB";         // Pink1
    private const string ColorUnknown = "#FFCC22";     // Yellow/System

    public static string Background(string acronym) => acronym.ToUpperInvariant() switch
    {
        "EZ" or "NF" or "HT" or "DC" => ColorReduction,
        "HR" or "SD" or "PF" or "DT" or "NC" or "HD" or "FL" or "FI" or "BL" or "AC" => ColorIncrease,
        "RX" or "AP" or "SO" or "AT" or "CN" => ColorAutomation,
        "DA" or "CL" or "TP" or "RD" or "MR" or "AL" => ColorConversion,
        "WU" or "WD" or "MU" or "NS" or "MG" or "RP" or "AS" or "FR" or "DP" or "BM" or "BR" or "SI" or "TC" or "GR" or "WG" or "DF" or "BPM" => ColorFun,
        _ => ColorUnknown,
    };

    public static string Foreground(string acronym) => acronym.ToUpperInvariant() switch
    {
        "EZ" or "NF" or "HT" or "DC" => "#121A0A",
        "HR" or "SD" or "PF" or "DT" or "NC" or "HD" or "FL" or "FI" or "BL" or "AC" => "#1A0A0A",
        "RX" or "AP" or "SO" or "AT" or "CN" => "#0A141A",
        "DA" or "CL" or "TP" or "RD" or "MR" or "AL" => "#0E0A1A",
        "WU" or "WD" or "MU" or "NS" or "MG" or "RP" or "AS" or "FR" or "DP" or "BM" or "BR" or "SI" or "TC" or "GR" or "WG" or "DF" or "BPM" => "#1A0A11",
        _ => "#1A1403",
    };

    public static string? IconFileName(string acronym) => acronym.ToUpperInvariant() switch
    {
        "NM" => "mod-no-mod.png",
        "AC" => "mod-accuracy-challenge.png",
        "AS" => "mod-adaptive-speed.png",
        "AL" => "mod-alternate.png",
        "AD" => "mod-approach-different.png",
        "AP" => "mod-autopilot.png",
        "AT" => "mod-autoplay.png",
        "BR" => "mod-barrel-roll.png",
        "BL" => "mod-blinds.png",
        "BM" => "mod-bloom.png",
        // The local BPM Adjust mod deliberately has no borrowed lazer icon. Its
        // acronym is the logo, so score badges render "BPM" plus the target.
        "BPM" => null,
        "BU" => "mod-bubbles.png",
        "CN" => "mod-cinema.png",
        "CL" => "mod-classic.png",
        "CS" => "mod-constant-speed.png",
        "CO" => "mod-cover.png",
        "DC" => "mod-daycore.png",
        "DF" => "mod-deflate.png",
        "DP" => "mod-depth.png",
        "DA" => "mod-difficulty-adjust.png",
        "DT" => "mod-double-time.png",
        "DS" => "mod-dual-stages.png",
        "EZ" => "mod-easy.png",
        "8K" => "mod-eight-keys.png",
        "FI" => "mod-fade-in.png",
        "5K" => "mod-five-keys.png",
        "FL" => "mod-flashlight.png",
        "FF" => "mod-floating-fruits.png",
        "4K" => "mod-four-keys.png",
        "FR" => "mod-freeze-frame.png",
        "GR" => "mod-grow.png",
        "HT" => "mod-half-time.png",
        "HR" => "mod-hard-rock.png",
        "HD" => "mod-hidden.png",
        "HO" => "mod-hold-off.png",
        "IN" => "mod-invert.png",
        "MG" => "mod-magnetised.png",
        "MR" => "mod-mirror.png",
        "MF" => "mod-moving-fast.png",
        "MU" => "mod-muted.png",
        "NC" => "mod-nightcore.png",
        "9K" => "mod-nine-keys.png",
        "NF" => "mod-no-fail.png",
        "NR" => "mod-no-release.png",
        "NS" => "mod-no-scope.png",
        "1K" => "mod-one-key.png",
        "PF" => "mod-perfect.png",
        "RD" => "mod-random.png",
        "RX" => "mod-relax.png",
        "RP" => "mod-repel.png",
        "SV2" => "mod-score-v2.png",
        "7K" => "mod-seven-keys.png",
        "SR" => "mod-simplified-rhythm.png",
        "SG" => "mod-single-tap.png",
        "6K" => "mod-six-keys.png",
        "SI" => "mod-spin-in.png",
        "SO" => "mod-spun-out.png",
        "ST" => "mod-strict-tracking.png",
        "SD" => "mod-sudden-death.png",
        "SW" => "mod-swap.png",
        "SY" => "mod-synesthesia.png",
        "TP" => "mod-target-practice.png",
        "10K" => "mod-ten-keys.png",
        "3K" => "mod-three-keys.png",
        "TD" => "mod-touch-device.png",
        "TC" => "mod-traceable.png",
        "TR" => "mod-transform.png",
        "2K" => "mod-two-keys.png",
        "WG" => "mod-wiggle.png",
        "WD" => "mod-wind-down.png",
        "WU" => "mod-wind-up.png",
        _ => null,
    };

    public static string DisplayName(string acronym)
    {
        var normalized = acronym.ToUpperInvariant();
        var name = normalized switch
        {
            "NM" => "No Mod",
            "AC" => "Accuracy Challenge",
            "AS" => "Adaptive Speed",
            "AL" => "Alternate",
            "AD" => "Approach Different",
            "AP" => "Autopilot",
            "AT" => "Autoplay",
            "BR" => "Barrel Roll",
            "BL" => "Blinds",
            "BM" => "Bloom",
            "BPM" => "BPM Adjust",
            "BU" => "Bubbles",
            "CN" => "Cinema",
            "CL" => "Classic",
            "CS" => "Constant Speed",
            "CO" => "Cover",
            "DC" => "Daycore",
            "DF" => "Deflate",
            "DP" => "Depth",
            "DA" => "Difficulty Adjust",
            "DT" => "Double Time",
            "DS" => "Dual Stages",
            "EZ" => "Easy",
            "8K" => "Eight Keys",
            "FI" => "Fade In",
            "5K" => "Five Keys",
            "FL" => "Flashlight",
            "FF" => "Floating Fruits",
            "4K" => "Four Keys",
            "FR" => "Freeze Frame",
            "GR" => "Grow",
            "HT" => "Half Time",
            "HR" => "Hard Rock",
            "HD" => "Hidden",
            "HO" => "Hold Off",
            "IN" => "Invert",
            "MG" => "Magnetised",
            "MR" => "Mirror",
            "MF" => "Moving Fast",
            "MU" => "Muted",
            "NC" => "Nightcore",
            "9K" => "Nine Keys",
            "NF" => "No Fail",
            "NR" => "No Release",
            "NS" => "No Scope",
            "1K" => "One Key",
            "PF" => "Perfect",
            "RD" => "Random",
            "RX" => "Relax",
            "RP" => "Repel",
            "SV2" => "Score V2",
            "7K" => "Seven Keys",
            "SR" => "Simplified Rhythm",
            "SG" => "Single Tap",
            "6K" => "Six Keys",
            "SI" => "Spin In",
            "SO" => "Spun Out",
            "ST" => "Strict Tracking",
            "SD" => "Sudden Death",
            "SW" => "Swap",
            "SY" => "Synesthesia",
            "TP" => "Target Practice",
            "10K" => "Ten Keys",
            "3K" => "Three Keys",
            "TD" => "Touch Device",
            "TC" => "Traceable",
            "TR" => "Transform",
            "2K" => "Two Keys",
            "WG" => "Wiggle",
            "WD" => "Wind Down",
            "WU" => "Wind Up",
            _ => normalized,
        };

        return $"{name} ({normalized})";
    }
}
