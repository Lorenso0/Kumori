namespace Kumori.Tracking;

/// <summary>Calculates the standard osu! rank when live telemetry omits it.</summary>
internal static class OsuGradeCalculator
{
    public static string? Calculate(AttemptSnapshot snapshot, string outcome)
    {
        if (string.Equals(outcome, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return "F";
        }

        // A rank only represents a completed score. Do not invent one for a
        // quit, retry, or abandoned play.
        if (!string.Equals(outcome, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var total = snapshot.N300 + snapshot.N100 + snapshot.N50 + snapshot.Misses;
        if (total <= 0)
        {
            return null;
        }

        var ratio300 = snapshot.N300 / total;
        var ratio50 = snapshot.N50 / total;
        var noMisses = snapshot.Misses <= 0;

        if (snapshot.N300 >= total && noMisses)
        {
            return "X";
        }
        if (ratio300 > 0.9 && ratio50 < 0.01 && noMisses)
        {
            return "S";
        }
        if ((ratio300 > 0.8 && noMisses) || ratio300 > 0.9)
        {
            return "A";
        }
        if (ratio300 > 0.7)
        {
            return "B";
        }
        return ratio300 > 0.6 ? "C" : "D";
    }
}
