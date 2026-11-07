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

    private const string SetupsHeader = "time_utc,candle_utc,symbol,timeframe,event,side,level,far_side,setup_utc,pause_candles,pause_height_atr,move_atr,pullback,atr,higher_tf,higher_trend,with_higher,mode,reason\n";
    private const string OrdersHeader = "time_utc,symbol,timeframe,setup_utc,role,action,side,lots,price_req,stop_loss,expires_utc,order_id,position_id,price_fill,slip_pips,gross,net,result,detail,chart\n";

    private static void Write(string root, string instance, string day, string file, string text)
    {
        var dir = Path.Combine(root, instance, day);
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, file), (File.Exists(Path.Combine(dir, file)) ? "" : file == "setups.csv" ? SetupsHeader : OrdersHeader) + text);
    }

    // The GBPUSD 1H move of 05–06 Nov 2026 (scout.md §7), as Scout's logs recorded it, across two instances.
    private static string GbpusdLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scout-history-{Guid.NewGuid():N}");
        Write(root, "Scout-6d48a6", "2026-11-05", "setups.csv",
            "2026-11-05 03:00:09,2026-11-05 02:00:00,GBPUSD,H1,NewStop,Buy,1.34921,1.34822,2026-11-05 01:00:00,2,0.69,2.61,0.26,0.00144,H4,up,with,Trade,\n"
            + "2026-11-05 21:00:06,2026-11-05 20:00:00,GBPUSD,H1,NewStop,Sell,1.34498,1.34634,2026-11-05 19:00:00,2,0.89,1.64,0.54,0.00177,H4,down,with,Trade,\n"
            + "2026-11-05 21:00:07,2026-11-05 20:00:00,EURUSD,H1,NewStop,Sell,1.10000,1.10100,2026-11-05 19:00:00,2,0.89,1.64,0.54,0.00177,H4,down,with,Trade,\n");
        Write(root, "Scout-6d48a6", "2026-11-05", "orders.csv",
            "2026-11-05 03:00:19,GBPUSD,H1,2026-11-05 01:00:00,M,place,Buy,0.10,1.34921,1.34822,2026-11-05 19:00:19,1035910422,,,,,,ok,Scout-6d48a6-H1-202611050100-M,\n"
            + "2026-11-05 03:15:33,GBPUSD,H1,2026-11-05 01:00:00,M,filled,Buy,0.10,1.34921,1.34822,,1035910422,692675771,1.34921,0.0,,,ok,,\n"
            + "2026-11-05 21:00:16,GBPUSD,H1,2026-11-05 19:00:00,M,place,Sell,0.10,1.34498,1.34634,2026-11-06 13:00:16,1036403098,,,,,,ok,Scout-6d48a6-H1-202611051900-M,\n"
            + "2026-11-05 21:00:17,GBPUSD,H1,2026-11-05 19:00:00,M,filled,Sell,0.10,1.34498,1.34634,,1036403098,692923991,1.34493,0.5,,,ok,,\n"
            + "2026-11-05 22:01:07,GBPUSD,H1,2026-11-05 19:00:00,M,closed,Sell,0.10,,,,1036422173,692923991,1.34640,,-14.70,-15.91,ok,,\n");
        Write(root, "Scout-567e7f", "2026-11-06", "setups.csv",
            "2026-11-06 12:00:08,2026-11-06 11:00:00,GBPUSD,H1,NewStop,Sell,1.33816,1.34029,2026-11-06 10:00:00,2,1.48,5.60,0.26,0.00144,H4,down,with,Trade,\n"
            + "2026-11-06 12:00:09,2026-11-06 11:00:00,GBPUSD,H4,NewStop,Sell,1.33700,1.34029,2026-11-06 08:00:00,2,1.48,5.60,0.26,0.00144,D1,down,with,Trade,\n"
            + "2026-11-06 21:00:08,2026-11-06 20:00:00,GBPUSD,H1,NewStop,Sell,1.33464,1.33572,2026-11-06 19:00:00,2,0.66,7.29,0.12,0.00164,H4,down,with,Trade,\n");
        Write(root, "Scout-567e7f", "2026-11-06", "orders.csv",
            "2026-11-06 07:00:10,GBPUSD,H1,2026-11-06 05:00:00,M,skip,Sell,0.10,1.34100,1.34200,,,,,,,,no margin,needs $106.80,\n"
            + "2026-11-06 12:00:18,GBPUSD,H1,2026-11-06 10:00:00,M,place,Sell,0.10,1.33816,1.34029,2026-11-07 04:00:18,1036689445,,,,,,ok,Scout-6d48a6-H1-202611061000-M,\n"
            + "2026-11-06 13:12:25,GBPUSD,H1,2026-11-06 10:00:00,M,rejected,Sell,0.10,1.33816,1.34029,,1036689445,,,,,,ok,NOT_ENOUGH_MONEY,\n"
            + "2026-11-06 21:00:18,GBPUSD,H1,2026-11-06 19:00:00,M,skip,Sell,0.10,1.33464,1.33572,,,,,,,,no margin,needs $106.80,\n");
        Directory.CreateDirectory(Path.Combine(root, "TM-a76098", "2026-11-06"));
        return root;
    }

    [Fact]
    public void Other_setups_are_read_with_what_had_become_of_each_when_the_order_was_placed()
    {
        var root = GbpusdLogs();
        try
        {
            var history = OrderLog.ReadHistory(root, "GBPUSD", "H1", new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 6, 16, 0, 18, DateTimeKind.Utc));

            Assert.Equal(
            [
                ("2026-11-05 01:00", 1, 1.34921, "filled, with TM"),
                ("2026-11-05 19:00", -1, 1.34498, "closed −$15.91"),
                ("2026-11-06 05:00", -1, 1.34100, "not placed: no margin"),
                ("2026-11-06 10:00", -1, 1.33816, "refused: not enough money"),
            ], history.Select(h => (h.PauseStartUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), h.Dir, h.Level, h.Outcome)));
            var closed = history[1];
            Assert.Equal((OutcomeTone.Loss, true), (closed.Tone, closed.Closed));
            Assert.Equal(new DateTime(2026, 11, 5, 21, 0, 17, DateTimeKind.Utc), closed.FilledUtc);
            Assert.Equal(new DateTime(2026, 11, 5, 22, 1, 7, DateTimeKind.Utc), closed.EndUtc);
            Assert.Equal((OutcomeTone.Warning, (DateTime?)null), (history[3].Tone, history[3].FilledUtc));

            // Drawn earlier, the same setups show what was known then: the refused order still waiting, the close not yet there.
            var earlier = OrderLog.ReadHistory(root, "GBPUSD", "H1", new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc));
            Assert.Equal(("waiting", (DateTime?)null), (earlier.Single(h => h.Level == 1.33816).Outcome, earlier.Single(h => h.Level == 1.33816).EndUtc));
            var beforeClose = OrderLog.ReadHistory(root, "GBPUSD", "H1", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 21, 30, 0, DateTimeKind.Utc));
            Assert.Equal(["filled, with TM", "filled, with TM"], beforeClose.Select(h => h.Outcome));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_setup_without_an_order_and_a_part_close_have_their_own_tags()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scout-history-{Guid.NewGuid():N}");
        try
        {
            Write(root, "Scout-aaaaaa", "2026-11-05", "setups.csv",
                "2026-11-05 03:00:09,2026-11-05 02:00:00,EURUSD,H1,NewStop,Buy,1.10000,1.09900,2026-11-05 01:00:00,2,0.69,2.61,0.26,0.00144,H4,up,with,Trade,\n"
                + "2026-11-05 03:00:09,2026-11-05 02:00:00,EURUSD,H1,NewReversal,Sell,1.09900,1.10000,2026-11-05 01:00:00,2,0.69,2.61,0.26,0.00144,H4,up,against,Trade,\n");
            Write(root, "Scout-aaaaaa", "2026-11-05", "orders.csv",
                "2026-11-05 03:00:19,EURUSD,H1,2026-11-05 01:00:00,R,place,Sell,0.10,1.09900,1.10000,,1,,,,,,ok,x,\n"
                + "2026-11-05 03:15:33,EURUSD,H1,2026-11-05 01:00:00,R,filled,Sell,0.10,1.09900,1.10000,,1,2,1.09900,0.0,,,ok,,\n"
                + "2026-11-05 05:00:00,EURUSD,H1,2026-11-05 01:00:00,R,closed,Sell,0.05,,,,3,2,1.09800,,5.00,4.60,ok,,\n");

            var history = OrderLog.ReadHistory(root, "EURUSD", "H1", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal([(false, "no order", OutcomeTone.Muted), (true, "part closed +$4.60", OutcomeTone.Profit)], history.Select(h => (h.Reversal, h.Outcome, h.Tone)));
            Assert.Equal((false, (DateTime?)null), (history[1].Closed, history[1].EndUtc));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An order left on an account Scout no longer watched: no expiry row, but its expiry time tells (Pepperstone, 04→07-Nov-2026).</summary>
    [Fact]
    public void An_order_past_its_expiry_time_shows_as_expired_even_without_an_expiry_row()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scout-history-{Guid.NewGuid():N}");
        try
        {
            Write(root, "Scout-bbbbbb", "2026-11-04", "orders.csv",
                "2026-11-04 18:00:10,AUDJPY,H1,2026-11-04 16:00:00,M,place,Buy,0.10,111.300,111.100,2026-11-05 10:00:10,5,,,,,,ok,x,\n");
            var from = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);

            var before = Assert.Single(OrderLog.ReadHistory(root, "AUDJPY", "H1", from, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc)));
            var after = Assert.Single(OrderLog.ReadHistory(root, "AUDJPY", "H1", from, new DateTime(2026, 11, 7, 17, 27, 0, DateTimeKind.Utc)));

            Assert.Equal(("waiting", OutcomeTone.Info, (DateTime?)null), (before.Outcome, before.Tone, before.EndUtc));
            Assert.Equal(("expired", OutcomeTone.Muted, (DateTime?)new DateTime(2026, 11, 5, 10, 0, 10, DateTimeKind.Utc)), (after.Outcome, after.Tone, after.EndUtc));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Picture_marks_the_other_setups_in_view_at_most_six_and_not_the_order_itself()
    {
        var (p, candles) = FirstOrder("EURUSD", 5, 0.0001);
        Assert.DoesNotContain("other-setup", OrderChart.Svg(p, candles));

        var shown = candles.Where(c => c.OpenUtc.AddHours(1) <= p.PlacedUtc).TakeLast(OrderChart.CandlesShown).ToList();
        PastSetup At(int candle, string outcome, OutcomeTone tone, bool reversal = false) =>
            new(shown[candle].OpenUtc, reversal, -1, shown[candle].Low, shown[candle].OpenUtc.AddHours(2), shown[candle].OpenUtc.AddHours(3), outcome, tone, tone is OutcomeTone.Loss or OutcomeTone.Profit);
        var others = new List<PastSetup>
        {
            At(0, "out of view", OutcomeTone.Muted) with { PauseStartUtc = shown[0].OpenUtc.AddHours(-1) },
            At(5, "expired", OutcomeTone.Muted),
            At(10, "closed −$15.91", OutcomeTone.Loss),
            At(20, "refused: not enough money", OutcomeTone.Warning),
            At(25, "cancelled: stop filled", OutcomeTone.Muted, reversal: true),
            At(30, "not placed: no margin", OutcomeTone.Warning),
            At(35, "closed +$12.10", OutcomeTone.Profit),
            At(40, "filled, with TM", OutcomeTone.Info),
            new(p.PauseStartUtc, false, p.Dir, p.Level, null, null, "this order", OutcomeTone.Info, false),
        };

        var text = OrderChart.Svg(p with { Others = others }, candles);

        var groups = XDocument.Parse(text).Descendants(Svg + "g").Where(g => (string?)g.Attribute("class") == "other-setup").ToList();
        Assert.Equal(OrderChart.MaxOthers, groups.Count);
        var tags = groups.Select(g => g.Elements(Svg + "text").Last().Value).ToList();
        Assert.Equal(["Sell · closed −$15.91", "Sell · refused: not enough money", "Sell reversal · cancelled: stop filled", "Sell · not placed: no margin",
            "Sell · closed +$12.10", "Sell · filled, with TM"], tags);
        Assert.Equal(2, groups.Count(g => g.Elements(Svg + "text").Any(t => t.Value == "✕")));
        Assert.Equal(OrderChart.Svg(p with { Others = others }, candles.Where(c => c.OpenUtc.AddHours(1) <= p.PlacedUtc).ToList()), text);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chart-sample-others.svg"), text); // to look at by eye
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
