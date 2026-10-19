namespace Trader.Core.MarketData;

/// <summary>One candle (trendbar) in prices; <see cref="OpenUtc"/> is the start of its period.</summary>
public sealed record Candle(DateTime OpenUtc, double Open, double High, double Low, double Close)
{
    /// <summary>The API sends the low as an integer (1/100,000) and the others as offsets from it.</summary>
    public static Candle From(ProtoOATrendbar t)
    {
        double P(ulong delta) => SymbolInfo.PriceFromRaw((ulong)t.Low + delta);
        return new Candle(DateTime.UnixEpoch.AddMinutes(t.UtcTimestampInMinutes), P(t.DeltaOpen), P(t.DeltaHigh), P(0), P(t.DeltaClose));
    }

    /// <summary>
    /// Average true range of the last <paramref name="count"/> candles of <paramref name="candles"/> (oldest first):
    /// each candle's high-to-low range, stretched to the previous close when the market gapped. A candle with no
    /// previous one counts its own range only. Null when there are no candles.
    /// </summary>
    public static double? AverageTrueRange(IReadOnlyList<Candle> candles, int count)
    {
        var first = Math.Max(0, candles.Count - count);
        if (candles.Count == 0 || count < 1)
            return null;
        var sum = 0.0;
        for (var i = first; i < candles.Count; i++)
        {
            var c = candles[i];
            sum += i == 0 ? c.High - c.Low : Math.Max(c.High, candles[i - 1].Close) - Math.Min(c.Low, candles[i - 1].Close);
        }
        return sum / (candles.Count - first);
    }
}
