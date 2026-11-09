using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>"daily" in scout.json (scout.md §2.10). On by default (owner, 09-Nov-2026); the dashboard's switch wins over Enabled.</summary>
public sealed record DailySettings
{
    public bool Enabled { get; init; } = true;
    public int ZoneSwing { get; init; } = 3;           // a swing is the highest or lowest candle of this many on either side
    public int ZoneCandles { get; init; } = 250;       // daily candles looked at (about a year)
    public int ZoneTouches { get; init; } = 2;         // swings that make a zone (a level that held twice)
    public double ZoneWidthAtr { get; init; } = 0.25;  // swings within this × daily ATR of each other are one zone
    public double ZoneNearAtr { get; init; } = 0.5;    // "near": inside the zone or within this × daily ATR of it
}

/// <summary>A daily support or resistance zone: which of the two it is depends on which side of it the order is (the polarity flip, Scout spec §4.7).</summary>
public sealed record DailyZone(double Low, double High, int Touches);

/// <summary>The daily-direction rule (Scout spec §4.7 D1, scout.md §2.10). A calculation only, on closed daily candles.</summary>
public static class DailyRule
{
    public const string AgainstDaily = "against daily";
    public const string NearResistance = "near daily resistance";
    public const string NearSupport = "near daily support";

    /// <summary>
    /// The zones in the last closed daily candles. A swing needs its later candles to be closed, so the newest candles cannot make one:
    /// nothing here looks ahead. Highs and lows count together, as a level can be support and later resistance.
    /// </summary>
    public static IReadOnlyList<DailyZone> Zones(IReadOnlyList<Candle> closed, double? atr, DailySettings s)
    {
        if (atr is not > 0)
            return [];
        var from = Math.Max(0, closed.Count - s.ZoneCandles);
        var swings = new List<double>();
        for (var i = from + s.ZoneSwing; i < closed.Count - s.ZoneSwing; i++)
        {
            bool isHigh = true, isLow = true;
            for (var j = i - s.ZoneSwing; j <= i + s.ZoneSwing && (isHigh || isLow); j++)
            {
                if (j == i)
                    continue;
                // Strict on the left, so that a run of equal candles gives one swing.
                isHigh &= j < i ? closed[j].High < closed[i].High : closed[j].High <= closed[i].High;
                isLow &= j < i ? closed[j].Low > closed[i].Low : closed[j].Low >= closed[i].Low;
            }
            if (isHigh)
                swings.Add(closed[i].High);
            if (isLow)
                swings.Add(closed[i].Low);
        }
        swings.Sort();

        var zones = new List<DailyZone>();
        var width = s.ZoneWidthAtr * atr.Value;
        for (var i = 0; i < swings.Count;)
        {
            var j = i;
            while (j + 1 < swings.Count && swings[j + 1] - swings[i] <= width)
                j++;
            if (j - i + 1 >= s.ZoneTouches)
                zones.Add(new DailyZone(swings[i], swings[j], j - i + 1));
            i = j + 1;
        }
        return zones;
    }

    /// <summary>
    /// The zone in the order's way, in words, or null. For a buy any zone whose top is above the level is resistance, for a sell any zone whose
    /// bottom is below it is support; a level beyond the zone has cleared it, which is the owner's entry just past a daily level.
    /// </summary>
    public static string? ZoneInTheWay(int dir, double level, IReadOnlyList<DailyZone> zones, double? atr, DailySettings s)
    {
        if (atr is not > 0)
            return null;
        var near = s.ZoneNearAtr * atr.Value;
        return dir > 0
            ? zones.Any(z => level < z.High && z.Low - level <= near) ? NearResistance : null
            : zones.Any(z => level > z.Low && level - z.High <= near) ? NearSupport : null;
    }

    /// <summary>Why the order must not be placed, or null. Direction first; only when the direction is unknown do the zones decide (owner, 09-Nov-2026).</summary>
    public static string? Hold(int dir, double level, int dailyTrend, IReadOnlyList<DailyZone> zones, double? atr, DailySettings s)
    {
        if (dailyTrend != 0)
            return dailyTrend == dir ? null : AgainstDaily;
        return ZoneInTheWay(dir, level, zones, atr, s);
    }
}
