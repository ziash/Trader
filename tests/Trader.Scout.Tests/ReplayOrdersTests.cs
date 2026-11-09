using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>The orders Scout would place on past candles (scout-lens.md §8 step 1), with Trade mode's rules (scout.md §2.3).</summary>
public sealed class ReplayOrdersTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>16 quiet candles (ATR ≈ 10), a rally of three 20-point candles, then the given pause candles, one hour apart.</summary>
    private static List<Candle> Chart(params (double O, double H, double L, double C)[] pause)
    {
        var bars = new List<(double O, double H, double L, double C)>();
        for (var i = 0; i < 16; i++)
            bars.Add((100, 105, 95, 100));
        bars.AddRange([(100, 121, 99, 120), (120, 141, 119, 140), (140, 161, 139, 160)]);
        bars.AddRange(pause);
        return bars.Select((b, i) => new Candle(T0.AddHours(i), b.O, b.H, b.L, b.C)).ToList();
    }

    private static List<ReplayOrder> Run(List<Candle> candles) => ReplayOrders.Run("EURUSD", "H1", candles, [], new DetectorSettings(), 16);

    [Theory]
    [InlineData(1, 100.0, 95.0, 95.0)]    // a buy: the stop-loss is below the price
    [InlineData(-1, 100.0, 105.0, 105.0)] // a sell: above it
    [InlineData(1, 100.0, 105.0, null)]   // on the winning side: no valid stop-loss
    [InlineData(-1, 100.0, 95.0, null)]
    [InlineData(1, 100.0, 100.0, null)]   // at the price
    [InlineData(1, 100.0, null, null)]    // no far side yet
    public void The_stop_loss_must_be_on_the_losing_side(int dir, double price, double? far, double? expected)
    {
        Assert.Equal(expected, OrderPlanner.StopLoss(dir, price, far));
    }

    [Fact]
    public void Main_stop_is_placed_once_when_the_pause_gets_its_level_with_the_far_side_as_stop_loss()
    {
        var orders = Run(Chart((160, 163, 155, 158), (158, 162, 154, 157), (157, 162, 155, 158)));

        var place = Assert.Single(orders);
        Assert.Equal(("place", 1, 163.0, 154.0), (place.Action, place.Dir, place.Price, place.StopLoss!.Value));
        Assert.Equal("EURUSD|H1|202610011900|M", place.Key);
        Assert.Equal(T0.AddHours(21), place.TimeUtc);                 // when the 2nd pause candle closed
        Assert.Equal(T0.AddHours(21 + 16), place.ExpiresUtc);         // 16 candles later
        Assert.Equal(2, place.Pause!.Candles);
    }

    [Fact]
    public void Rolling_over_pause_adds_the_reversal_and_a_fill_ends_both()
    {
        var orders = Run(Chart((160, 163, 155, 158), (158, 162, 154, 157), (157, 161, 153, 156), (156, 158, 152, 154), (154, 165, 153, 164)));

        var rev = Assert.Single(orders, o => o is { Action: "place", Reversal: true });
        Assert.Equal((-1, 152.0, 163.0, T0.AddHours(23)), (rev.Dir, rev.Price, rev.StopLoss!.Value, rev.TimeUtc));
        var cancels = orders.Where(o => o.Action == "cancel").ToList();
        Assert.Equal(2, cancels.Count);
        Assert.All(cancels, c => Assert.Equal(T0.AddHours(24), c.TimeUtc));
        Assert.Equal("filled", Assert.Single(cancels, c => !c.Reversal).Reason);   // the broker filled it: the cancel finds nothing
        Assert.Equal("stop filled", Assert.Single(cancels, c => c.Reversal).Reason);
    }

    /// <summary>Real IC Markets 1H candles: each role placed at most once, cancels only after their place, stop-loss on the safe side.</summary>
    [Theory]
    [InlineData("EURUSD")]
    [InlineData("BTCUSD")]
    public void Broker_candles_give_consistent_orders(string symbol)
    {
        var orders = ReplayOrders.Run(symbol, "H1", CandleFile.Read(DataFile($"{symbol}-H1.csv")), [], new DetectorSettings(), 16);
        var places = orders.Where(o => o.Action == "place").ToList();
        Assert.True(places.Count > 20, $"only {places.Count} orders");
        Assert.Equal(places.Count, places.Select(p => p.Key).Distinct().Count());
        Assert.All(places.Where(p => p.StopLoss is not null), p => Assert.True((p.Price - p.StopLoss!.Value) * p.Dir > 0));
        foreach (var c in orders.Where(o => o.Action == "cancel"))
            Assert.Contains(places, p => p.Key == c.Key && p.TimeUtc <= c.TimeUtc);
    }

    [Fact]
    public void Csv_has_one_row_per_order_with_the_header()
    {
        var path = Path.GetTempFileName();
        try
        {
            var orders = Run(Chart((160, 163, 155, 158), (158, 162, 154, 157)));
            ReplayOrders.Write(path, "EURUSD", "H1", orders);
            var lines = File.ReadAllLines(path);
            Assert.Equal(ReplayOrders.Header, lines[0]);
            Assert.StartsWith("2026-10-01 21:00:00,EURUSD|H1|202610011900|M,EURUSD,H1,M,place,Buy,163.000000,154.000000,2026-10-02 13:00:00,", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
