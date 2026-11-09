namespace Trader.Scout;

/// <summary>One order a setup wants on the account right now (scout.md §2.3). <see cref="SinceUtc"/>: the open of the candle that gave its level.</summary>
internal sealed record OrderIntent(string Key, DateTime PauseStartUtc, bool Reversal, int Dir, double Level, double? FarSide, DateTime SinceUtc);

/// <summary>
/// Scout's order rules, in one place for the live bot and the replay (review P-09, F-28): which orders a chart's detector wants, where the stop-loss goes, and when an
/// order expires. A calculation only. What happens next (the broker's answer, margin, limits) is decided by whoever executes the intents.
/// </summary>
internal static class OrderPlanner
{
    /// <summary>The current pause's main order and reversal order, those that have a level.</summary>
    public static IEnumerable<OrderIntent> Wanted(Detector d, string symbol, string timeframe)
    {
        if (d.PauseStartUtc is not { } start)
            yield break;
        if (d.MainLevel is { } main)
            yield return new OrderIntent(ScoutState.Key(symbol, timeframe, start, false), start, false, d.Trend, main, d.MainFarSide, d.MainSinceUtc ?? start);
        if (d.ReversalLevel is { } reversal)
            yield return new OrderIntent(ScoutState.Key(symbol, timeframe, start, true), start, true, -d.Trend, reversal, d.ReversalFarSide, d.ReversalSinceUtc ?? start);
    }

    /// <summary>Why the daily rule holds this order back (scout.md §2.10), or null: also null when the rule is off or does not apply to the chart.</summary>
    public static string? DailyHold(ChartTracker chart, OrderIntent i, DailySettings s) =>
        !s.Enabled || chart.Daily is not { } d ? null : DailyRule.Hold(i.Dir, i.Level, d.Trend, chart.DailyZones(s), d.Atr, s);

    /// <summary>The far side of the pause, when it is on the losing side of the order's price; otherwise no valid stop-loss.</summary>
    public static double? StopLoss(int dir, double price, double? farSide) => farSide is { } far && (far - price) * dir < 0 ? far : null;

    /// <summary>The server expiry: this many candles after the candle that gave the level, however late the order is placed.</summary>
    public static DateTime Expiry(DateTime sinceUtc, TimeSpan candle, int expiryCandles) => sinceUtc + candle + expiryCandles * candle;
}
