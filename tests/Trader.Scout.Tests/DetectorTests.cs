using System.Globalization;
using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>The detector against Scout_Pine's rules (Scout spec §3.1): small made-up charts, and real IC Markets 1H candles.</summary>
public sealed class DetectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>Candles from (open, high, low, close), one hour apart from <see cref="T0"/>.</summary>
    private static List<Candle> Chart(params (double O, double H, double L, double C)[] bars) =>
        bars.Select((b, i) => new Candle(T0.AddHours(i), b.O, b.H, b.L, b.C)).ToList();

    private static List<SetupEvent> Run(Detector d, IEnumerable<Candle> candles) => candles.SelectMany(d.Add).ToList();

    /// <summary>16 quiet candles (range 10, so ATR ≈ 10), then a strong rally of three 20-point candles.</summary>
    private static List<(double, double, double, double)> QuietThenRally()
    {
        var bars = new List<(double, double, double, double)>();
        for (var i = 0; i < 16; i++)
            bars.Add((100, 105, 95, 100));
        bars.Add((100, 121, 99, 120));
        bars.Add((120, 141, 119, 140));
        bars.Add((140, 161, 139, 160));
        return bars;
    }

    [Fact]
    public void Pause_after_a_rally_gives_a_buy_stop_at_the_wick_high_with_the_stop_loss_below()
    {
        var bars = QuietThenRally();
        bars.Add((160, 163, 155, 158)); // 1st pause candle
        bars.Add((158, 162, 154, 157)); // 2nd: the stop is placed (Scout spec §2.1 rules 3, 4)
        var d = new Detector();
        var events = Run(d, Chart([.. bars]));

        var stop = Assert.Single(events, e => e.Kind == SetupEventKind.NewStop);
        Assert.Equal((1, 163.0, 154.0), (stop.Dir, stop.Level, stop.FarSide!.Value));
        Assert.Equal(T0.AddHours(19), stop.PauseStartUtc);
        Assert.Equal(1, d.Trend);
        Assert.Equal(163, d.MainLevel);
        Assert.Equal(T0.AddHours(20), d.MainSinceUtc); // the 2nd pause candle gave the level
        Assert.Null(d.ReversalSinceUtc);
    }

    [Fact]
    public void Smart_level_counts_a_long_spike_wick_at_its_body()
    {
        var bars = QuietThenRally();
        bars.Add((160, 175, 156, 158)); // a 15-point wick above the body: longer than 0.6 × ATR → counts at the body (160)
        bars.Add((158, 161, 155, 157));
        var events = Run(new Detector(), Chart([.. bars]));
        Assert.Equal(161, Assert.Single(events, e => e.Kind == SetupEventKind.NewStop).Level);

        var wicks = Run(new Detector(new DetectorSettings { Level = LevelMode.Wicks }), Chart([.. bars]));
        Assert.Equal(175, Assert.Single(wicks, e => e.Kind == SetupEventKind.NewStop).Level);
    }

    [Fact]
    public void Filled_stop_ends_the_setup_and_a_rolling_over_pause_adds_the_reversal_stop()
    {
        var bars = QuietThenRally();
        bars.Add((160, 163, 155, 158));
        bars.Add((158, 162, 154, 157));
        bars.Add((157, 161, 153, 156));
        bars.Add((156, 158, 152, 154)); // 4th candle, lower highs: reversal Sell Stop below the pause (rule 5)
        bars.Add((154, 165, 153, 164)); // reaches the Buy Stop at 163
        var events = Run(new Detector(), Chart([.. bars]));

        var rev = Assert.Single(events, e => e.Kind == SetupEventKind.NewReversal);
        Assert.Equal((-1, 152.0, 163.0, true), (rev.Dir, rev.Level, rev.FarSide!.Value, rev.Reversal));
        Assert.Contains(events, e => e.Kind == SetupEventKind.StopFilled && e.Level == 163);
        var pair = Assert.Single(events, e => e.Kind == SetupEventKind.Cancelled);
        Assert.Equal((true, "stop filled"), (pair.Reversal, pair.Reason));
    }

    [Fact]
    public void Candles_must_be_newer_than_the_last_one()
    {
        var d = new Detector();
        d.Add(new Candle(T0, 1, 2, 0, 1));
        Assert.Throws<ArgumentException>(() => d.Add(new Candle(T0, 1, 2, 0, 1)));
    }

    /// <summary>
    /// Real IC Markets 1H candles (Aug–Nov 2026): the same fills, levels, cancels and turns as the Python prototype
    /// (Scout research/prototype/proto.py, which runs Scout_Pine's rules) on the same candles. The detector also reports
    /// a cancelled reversal stop, which the prototype leaves out.
    /// </summary>
    [Theory]
    [InlineData("EURUSD")]
    [InlineData("GBPJPY")]
    [InlineData("BTCUSD")]
    public void Same_events_as_the_prototype_on_broker_candles(string symbol)
    {
        var expected = File.ReadAllLines(DataFile($"{symbol}-H1.proto.txt"));
        var events = Run(new Detector(), CandleFile.Read(DataFile($"{symbol}-H1.csv")));
        var actual = new List<string>();
        foreach (var e in events)
        {
            string? kind = e.Kind switch
            {
                SetupEventKind.NewStop => "SET",
                SetupEventKind.StopMoved => "MOVE",
                SetupEventKind.NewReversal or SetupEventKind.ReversalMoved => "REV-SET",
                SetupEventKind.StopFilled => "TRIG",
                SetupEventKind.ReversalFilled => "TRIG-REV",
                SetupEventKind.Cancelled when !e.Reversal && e.Reason is "trend turned" or "pause too long or too deep" => "CANCEL",
                SetupEventKind.TrendTurned => "FLIP",
                _ => null,
            };
            if (kind is not null)
                actual.Add(string.Create(CultureInfo.InvariantCulture, $"{e.CandleUtc:yyyy-MM-dd HH:mm} {kind} {(e.Dir == 1 ? "BUY" : "SELL")} {e.Level:F5}"));
        }
        Assert.Equal(expected, actual);
    }

    /// <summary>The owner's EURUSD top (Scout spec §2.1 rule 5, 6–7 Oct): a Buy Stop above the top range first, then the reversal Sell Stop fills.</summary>
    [Fact]
    public void EURUSD_top_buy_stop_first_then_the_reversal_sell_stop_fills()
    {
        var events = Run(new Detector(), CandleFile.Read(DataFile("EURUSD-H1.csv")))
            .Where(e => e.CandleUtc >= new DateTime(2026, 10, 6, 12, 0, 0) && e.CandleUtc <= new DateTime(2026, 10, 7, 2, 0, 0))
            .Where(e => e.Kind is not (SetupEventKind.StopMoved or SetupEventKind.ReversalMoved))
            .Select(e => (e.Kind, e.Dir))
            .ToList();
        Assert.Equal(
        [
            (SetupEventKind.NewStop, 1), (SetupEventKind.NewReversal, -1), (SetupEventKind.ReversalFilled, -1), (SetupEventKind.Cancelled, 1),
        ], events);
    }
}
