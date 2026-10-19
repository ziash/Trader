using Trader.Core.MarketData;

namespace Trader.Core.Tests;

public sealed class CandleTests
{
    private static Candle C(int day, double high, double low, double close) =>
        new(new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc), low, high, low, close);

    [Fact]
    public void Average_true_range_counts_gaps_from_the_previous_close()
    {
        Candle[] days = [C(14, 1.10, 1.09, 1.095), C(15, 1.11, 1.10, 1.105), C(16, 1.12, 1.11, 1.115), C(19, 1.13, 1.125, 1.128)];
        // Last 3: 15th 1.11-1.095 (gap from 1.095) = 0.015; 16th 1.12-1.105 = 0.015; 19th 1.13-1.115 = 0.015
        Assert.Equal(0.015, Candle.AverageTrueRange(days, 3)!.Value, 9);
        Assert.Equal(0.010, Candle.AverageTrueRange([days[0]], 3)!.Value, 9); // only its own range
        Assert.Null(Candle.AverageTrueRange([], 3));
    }

    [Fact]
    public void Trendbar_prices_are_low_plus_offsets()
    {
        var c = Candle.From(new ProtoOATrendbar { Low = 116_000, DeltaOpen = 20, DeltaHigh = 100, DeltaClose = 50, UtcTimestampInMinutes = 29_000_000 });
        Assert.Equal((1.16, 1.1602, 1.161, 1.1605), (c.Low, c.Open, c.High, c.Close));
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(29_000_000), c.OpenUtc);
    }
}
