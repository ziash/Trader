using System.Globalization;
using System.Xml.Linq;
using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

public sealed class OrderChartTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    // The first main stop the detector finds on real EURUSD 1H candles, placed 10 s after its candle closed.
    private static (OrderPicture Picture, IReadOnlyList<Candle> Candles) FirstOrder(string symbol, int digits, double pip)
    {
        var candles = CandleFile.Read(Path.Combine(AppContext.BaseDirectory, "Data", $"{symbol}-H1.csv"));
        var detector = new Detector();
        var e = candles.SelectMany(detector.Add).Skip(20).First(x => x.Kind == SetupEventKind.NewStop);
        var placed = e.CandleUtc.AddHours(1).AddSeconds(10);
        return (new OrderPicture(symbol, "H1", digits, pip, "brokerA", 49123893, 1035054512, e.Dir, false, e.Level, e.FarSide,
            placed, placed.AddHours(16), e.PauseStartUtc!.Value, "H4", "with"), candles);
    }

    [Theory]
    [InlineData("EURUSD", 5, 0.0001)]
    [InlineData("BTCUSD", 2, 1.0)]
    public void Picture_shows_the_60_candles_closed_before_the_order_and_its_levels(string symbol, int digits, double pip)
    {
        var (p, candles) = FirstOrder(symbol, digits, pip);
        var text = OrderChart.Svg(p, candles);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"chart-sample-{symbol}.svg"), text); // to look at by eye

        var doc = XDocument.Parse(text);
        Assert.Equal(OrderChart.CandlesShown, doc.Descendants(Svg + "g").Count(g => (string?)g.Attribute("class") == "candle"));
        var texts = doc.Descendants(Svg + "text").Select(t => t.Value).ToList();
        var level = p.Level.ToString("F" + digits, CultureInfo.InvariantCulture);
        Assert.Contains(texts, t => t == $"{(p.Dir == 1 ? "Buy" : "Sell")} {level}");
        Assert.Contains(texts, t => t == $"SL {p.StopLoss!.Value.ToString("F" + digits, CultureInfo.InvariantCulture)}");
        Assert.Contains(texts, t => t.Contains("H4 trend: with") && t.Contains("order 1035054512"));
        Assert.True(text.Length < 40_000, $"{text.Length} bytes");
    }

    [Fact]
    public void Picture_drawn_later_is_the_same_and_never_shows_later_candles()
    {
        var (p, candles) = FirstOrder("EURUSD", 5, 0.0001);
        var upToOrder = candles.Where(c => c.OpenUtc.AddHours(1) <= p.PlacedUtc).ToList();
        Assert.True(upToOrder.Count < candles.Count);
        Assert.Equal(OrderChart.Svg(p, upToOrder), OrderChart.Svg(p, candles));
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_machine_language()
    {
        var (p, candles) = FirstOrder("EURUSD", 5, 0.0001);
        var invariant = OrderChart.Svg(p, candles);
        var before = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "ur-PK", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(invariant, OrderChart.Svg(p, candles));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
        foreach (var a in XDocument.Parse(invariant).Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName is "x" or "y" or "x1" or "y2" or "width"))
            Assert.True(double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _), $"{a.Name}={a.Value}");
    }

    [Fact]
    public void File_name_names_the_chart_pause_role_and_order()
    {
        var (p, _) = FirstOrder("EURUSD", 5, 0.0001);
        Assert.Equal($"EURUSD-H1-{p.PauseStartUtc:yyyyMMddHHmm}-M-1035054512.svg", OrderChart.FileName(p));
        Assert.EndsWith("-R-7.svg", OrderChart.FileName(p with { Reversal = true, OrderId = 7 }));
    }

    [Fact]
    public void Past_orders_are_read_back_from_every_instance_log_with_their_trend_tag()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scout-log-{Guid.NewGuid():N}");
        try
        {
            var day = Path.Combine(root, "Scout-f8317b", "2026-11-03");
            Directory.CreateDirectory(day);
            // Files from before the chart column existed.
            File.WriteAllText(Path.Combine(day, "orders.csv"),
                "time_utc,symbol,timeframe,setup_utc,role,action,side,lots,price_req,stop_loss,expires_utc,order_id,position_id,price_fill,slip_pips,gross,net,result,detail\n"
                + "2026-11-03 17:42:57,GBPJPY,H1,2026-11-03 15:00:00,M,place,Buy,0.10,212.686,212.100,2026-11-04 09:42:57,1035054512,,,,,,ok,Scout-f8317b-H1-202611031500-M\n"
                + "2026-11-03 17:43:00,EURUSD,H1,2026-11-03 14:00:00,M,place,Sell,0.10,1.11800,1.11900,2026-11-04 09:43:00,,,,,,,refused,TRADING_BAD_STOPS: x\n"
                + "2026-11-03 17:46:01,GBPJPY,H1,2026-11-03 15:00:00,M,filled,Buy,0.10,212.686,212.100,,1035054512,692232259,212.690,0.4,,,ok,\n");
            File.WriteAllText(Path.Combine(day, "setups.csv"),
                "time_utc,candle_utc,symbol,timeframe,event,side,level,far_side,setup_utc,pause_candles,pause_height_atr,move_atr,pullback,atr,higher_tf,higher_trend,with_higher,mode,reason\n"
                + "2026-11-03 17:42:50,2026-11-03 16:00:00,GBPJPY,H1,NewStop,Buy,212.686,212.100,2026-11-03 15:00:00,2,0.80,3.10,0.20,0.300,H4,down,against,Trade,\n");
            Directory.CreateDirectory(Path.Combine(root, "TM-a76098", "2026-11-03"));

            var placed = OrderLog.ReadPlaced(root);

            var o = Assert.Single(placed).Value;
            Assert.Equal((1035054512L, "GBPJPY", 1, 212.686, 212.100, "H4", "against"), (o.OrderId, o.Symbol, o.Dir, o.Price, o.StopLoss!.Value, o.HigherTimeframe, o.WithHigher));
            Assert.Equal(new DateTime(2026, 11, 3, 17, 42, 57, DateTimeKind.Utc), o.TimeUtc);
            Assert.Equal(new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc), o.SetupUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Higher_trend_worked_out_later_matches_what_Scout_saw_live()
    {
        var h1 = CandleFile.Read(Path.Combine(AppContext.BaseDirectory, "Data", "EURUSD-H1.csv"));
        var h4 = h1.GroupBy(c => c.OpenUtc.Date.AddHours(c.OpenUtc.Hour / 4 * 4))
            .Select(g => new Candle(g.Key, g.First().Open, g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close)).ToList();
        var tracker = new ChartTracker(new ScoutChart("EURUSD", "H1"), new DetectorSettings());
        var live = new List<(DateTime Now, int Trend)>();
        foreach (var c in h1)
        {
            var now = c.OpenUtc.AddHours(1).AddSeconds(10);
            tracker.Feed(h1.Where(x => x.OpenUtc <= c.OpenUtc).TakeLast(3).ToList(), h4.Where(x => x.OpenUtc <= c.OpenUtc).TakeLast(2).ToList(), now);
            live.Add((now, tracker.Higher!.Trend));
        }
        Assert.Contains(live, x => x.Trend == 1);
        Assert.Contains(live, x => x.Trend == -1);
        Assert.All(live.Where((_, i) => i % 7 == 0), x => Assert.Equal(x.Trend, tracker.HigherTrendAt(x.Now)));
    }

    [Fact]
    public void The_picture_path_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-state-{Guid.NewGuid():N}.json");
        try
        {
            var s = ScoutState.Load(path);
            s.Roles["EURUSD|H1|202611031400|M"] = new RoleRecord { FirstSeenUtc = DateTime.UtcNow, OrderId = 42, Chart = "brokerA/1/Scout-x/2026-11-03/charts/a.svg" };
            s.Save(DateTime.UtcNow);
            Assert.Equal("brokerA/1/Scout-x/2026-11-03/charts/a.svg", ScoutState.Load(path).Roles["EURUSD|H1|202611031400|M"].Chart);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
