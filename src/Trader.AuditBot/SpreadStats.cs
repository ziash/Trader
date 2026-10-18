namespace Trader.AuditBot;

/// <summary>One price update as the recorder keeps it.</summary>
public readonly record struct Tick(DateTime TimeUtc, double Bid, double Ask)
{
    public double Spread => Ask - Bid;
}

/// <summary>Spread statistics of one symbol over one interval (prices, not yet pips or USD).</summary>
public sealed record SpreadSummary(
    int Ticks,
    double? Bid,
    double? Ask,
    double? Min,
    double? Avg,
    double? Median,
    double? P95,
    double? Max,
    double MaxGapSeconds);

public static class SpreadStats
{
    /// <summary>
    /// Summarises the ticks of [start, end). Ticks before start are counted from start (late arrivals);
    /// the longest gap includes the stretch from start to the first tick and from the last tick to end,
    /// so an interval with no ticks has a gap equal to its length.
    /// </summary>
    public static SpreadSummary Summarize(IReadOnlyList<Tick> ticks, DateTime start, DateTime end)
    {
        var maxGap = 0.0;
        var previous = start;
        foreach (var t in ticks.OrderBy(t => t.TimeUtc))
        {
            var time = t.TimeUtc < start ? start : t.TimeUtc;
            maxGap = Math.Max(maxGap, (time - previous).TotalSeconds);
            previous = time;
        }
        maxGap = Math.Max(maxGap, (end - previous).TotalSeconds);

        if (ticks.Count == 0)
            return new SpreadSummary(0, null, null, null, null, null, null, null, maxGap);

        var spreads = ticks.Select(t => t.Spread).Order().ToArray();
        var last = ticks.MaxBy(t => t.TimeUtc);
        return new SpreadSummary(
            ticks.Count, last.Bid, last.Ask,
            spreads[0], spreads.Average(), Median(spreads), Percentile(spreads, 0.95), spreads[^1],
            maxGap);
    }

    /// <summary>Middle value of sorted data (average of the two middle values when the count is even).</summary>
    public static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>Nearest-rank percentile of sorted data: the smallest value with at least p of the data at or below it.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
