using System.Globalization;

namespace Trader.Scout;

/// <summary>Losses that stop new entries (scout.md §2.7), in % of equity. Starting values, to be calibrated on replay.</summary>
public sealed record RiskLimits(double DailyLossPercent = 3, double WeeklyLossPercent = 6, double PeakDrawdownPercent = 15);

public enum LossHalt { None, Daily, Weekly, Peak }

/// <summary>The equity reference points of one account, kept in Scout's memory so a restart does not forget a halt.</summary>
public sealed class EquityMarks
{
    public DateOnly Day { get; set; }
    public double DayStart { get; set; }
    public DateOnly Week { get; set; }
    public double WeekStart { get; set; }
    public double Peak { get; set; }
    public LossHalt Halt { get; set; }
    public string? HaltReason { get; set; }
    public DateTime? HaltSinceUtc { get; set; }
}

/// <summary>
/// The loss limits (scout.md §2.7). A halt stops new entries only: TM keeps managing and protecting every open position. A daily
/// halt ends with the UTC day; a weekly or peak halt stays until the owner resets it. A recovery of the equity does not end a halt.
/// </summary>
public static class EntryHalt
{
    private static DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)); // Monday

    /// <summary>Moves the marks to <paramref name="equity"/> and applies the limits.</summary>
    /// <returns>True when the marks changed in a way worth saving (a new day or week, a new peak, a halt started or ended).</returns>
    public static bool Update(EquityMarks m, double equity, DateTime nowUtc, RiskLimits limits)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        var changed = false;
        if (m.Peak <= 0)
        {
            (m.Day, m.DayStart, m.Week, m.WeekStart, m.Peak) = (today, equity, WeekOf(today), equity, equity);
            changed = true;
        }
        if (today != m.Day)
        {
            (m.Day, m.DayStart) = (today, equity);
            if (m.Halt == LossHalt.Daily)
                (m.Halt, m.HaltReason, m.HaltSinceUtc) = (LossHalt.None, null, null);
            changed = true;
        }
        if (WeekOf(today) != m.Week)
            (m.Week, m.WeekStart, changed) = (WeekOf(today), equity, true);
        if (equity > m.Peak)
            (m.Peak, changed) = (equity, true);

        if (m.Halt != LossHalt.None)
            return changed;
        static double Fall(double from, double to) => from > 0 ? (from - to) / from * 100 : 0;
        static string N(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        (LossHalt Kind, double Fell, double Limit, string What) hit =
            Fall(m.Peak, equity) >= limits.PeakDrawdownPercent ? (LossHalt.Peak, Fall(m.Peak, equity), limits.PeakDrawdownPercent, "from its peak")
            : Fall(m.WeekStart, equity) >= limits.WeeklyLossPercent ? (LossHalt.Weekly, Fall(m.WeekStart, equity), limits.WeeklyLossPercent, "this week")
            : Fall(m.DayStart, equity) >= limits.DailyLossPercent ? (LossHalt.Daily, Fall(m.DayStart, equity), limits.DailyLossPercent, "today")
            : (LossHalt.None, 0, 0, "");
        if (hit.Kind == LossHalt.None)
            return changed;
        m.Halt = hit.Kind;
        m.HaltSinceUtc = nowUtc;
        m.HaltReason = $"equity down {N(hit.Fell)} % {hit.What} (limit {N(hit.Limit)} %)";
        return true;
    }

    /// <summary>The owner's reset: the halt ends and the limits count from the equity now.</summary>
    public static void Reset(EquityMarks m, double equity, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        (m.Day, m.DayStart, m.Week, m.WeekStart, m.Peak) = (today, equity, WeekOf(today), equity, equity);
        (m.Halt, m.HaltReason, m.HaltSinceUtc) = (LossHalt.None, null, null);
    }
}
