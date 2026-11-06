using System.Text.RegularExpressions;
using Trader.Core.Csv;
using Trader.Core.MarketData;
using Trader.DashboardExport;

namespace Trader.DashboardExport.Tests;

/// <summary>The account snapshot, deals.csv and the trade pictures (dashboard.md §2.3, §3.2, §3.3; build step 5a).</summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-snap-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly SymbolFacts Sol = new(101, "SOLUSD", 3, 1);

    // The SOLUSD trade of 05–06 Nov 2026 as the broker reported it (issue-log.md #1), main 692692856.
    private static readonly TradeLinker Linker = new(new Dictionary<long, long> { [1035938797] = 692692856 });

    private static DealRow Fill(string time, long dealId, long positionId, string side, double price, string label, double gross = 0, double swap = 0, bool close = false) =>
        new(DateTime.Parse(time + "Z").ToUniversalTime(), dealId, dealId + 1000, positionId, "SOLUSD", side, 20, price, 3, close, close ? 96.359 : null,
            gross, swap, 0, close ? 3000 : null, Linker.Link(label, positionId), label);

    private static readonly DealRow[] Trade =
    [
        Fill("2026-11-05 07:04:52", 1, 692692856, "buy", 96.359, "Scout-6d48a6-H1-202611050300-M"),
        Fill("2026-11-05 07:04:52", 2, 692692858, "buy", 96.359, "TM-38ade9-O1035938797-MIR"),
        Fill("2026-11-05 07:31:05", 3, 692715012, "sell", 95.717, "TM-38ade9-P692692856-HMAIN"),
        Fill("2026-11-05 07:31:05", 4, 692715013, "sell", 95.717, "TM-38ade9-P692692856-HMIR"),
        Fill("2026-11-05 08:09:20", 5, 692715012, "buy", 96.044, "TM-38ade9-P692692856-HMAIN", gross: -6.54, close: true),
        Fill("2026-11-05 08:09:20", 6, 692715013, "buy", 96.044, "TM-38ade9-P692692856-HMIR", gross: -6.54, close: true),
        Fill("2026-11-05 12:25:50", 7, 692729521, "sell", 95.722, "TM-38ade9-P692692856-HMAIN"),
        Fill("2026-11-05 12:25:50", 8, 692729523, "sell", 95.722, "TM-38ade9-P692692856-HMIR"),
        Fill("2026-11-06 16:14:28", 9, 692692856, "sell", 89.832, "Scout-6d48a6-H1-202611050300-M", gross: -130.54, swap: -0.98, close: true),
        Fill("2026-11-06 16:14:29", 10, 692692858, "sell", 89.842, "TM-38ade9-O1035938797-MIR", gross: -130.34, swap: -0.98, close: true),
        Fill("2026-11-06 16:14:29", 11, 692729521, "buy", 89.954, "TM-38ade9-P692692856-HMAIN", gross: 115.36, close: true),
        Fill("2026-11-06 16:14:29", 12, 692729523, "buy", 89.954, "TM-38ade9-P692692856-HMIR", gross: 115.36, close: true),
    ];

    private static readonly SnapshotFile NoPositions = new("brokerA-49123893", "brokerA", 49123893, "2026-11-06 16:30:00",
        new SnapshotAccount(3394.35, 2278.93, 1868.02, 410.91, 122, "1:500", 0, 0), [], []);

    [Theory]
    [InlineData("", "manual", "P5", "MAIN")]
    [InlineData("Scout-6d48a6-H1-202611050300-M", "Scout", "P5", "MAIN")]
    [InlineData("TM-38ade9-P692692856-HMAIN", "TM", "P692692856", "HMAIN")]
    [InlineData("TM-38ade9-O1035938797-MIR", "TM", "P692692856", "MIR")]  // the mirror of a pending order: the trade of the position it opened
    [InlineData("TM-38ade9-O777-MIR", "TM", "O777", "MIR")]               // that order not filled (yet)
    [InlineData("AuditBot-77fbd8", "AuditBot", "P5", "MAIN")]
    [InlineData("TM-38ade9-P1-MAIN", "TM", "P5", "MAIN")]                  // not a TM leg label: a trade of its own
    public void Labels_give_owner_trade_and_role(string label, string owner, string trade, string role) =>
        Assert.Equal(new TradeLink(owner, trade, role), Linker.Link(label, 5));

    [Fact]
    public void A_trades_result_covers_every_leg_not_the_main_alone()
    {
        var t = Assert.Single(TradeHistory.Build(Trade, NoPositions, new Dictionary<long, MainOrder>(), new Dictionary<string, long> { ["SOLUSD"] = 101 }, []));
        Assert.Equal(("P692692856", "Scout", 12), (t.Trade, t.Owner, t.Deals.Count));
        Assert.Equal(-45.20, t.Result, 2);                        // main −131.52, mirror −131.32, hedges +2 × 115.36 and −2 × 6.54
        Assert.Equal(-131.52, t.Deals.Where(d => d.Link.Role == "MAIN").Sum(d => d.Net), 2);
        Assert.False(t.Open);
        Assert.Equal(new DateTime(2026, 11, 6, 16, 14, 29, DateTimeKind.Utc), t.ClosedUtc);
    }

    [Fact]
    public void Open_legs_count_with_their_open_result_and_keep_the_trade_open()
    {
        var leg = new SnapshotPosition(692729521, "TM", "P692692856", "HMAIN", "SOLUSD", "sell", 20, 95.722, null, null, 12.5, 100, "2026-11-05 12:25:50", "");
        var snapshot = NoPositions with { Positions = [leg] };
        var t = Assert.Single(TradeHistory.Build(Trade[..8], snapshot, new Dictionary<long, MainOrder>(), new Dictionary<string, long>(), []));
        Assert.True(t.Open);
        Assert.Null(t.ClosedUtc);
        Assert.Equal(-13.08 + 12.5, t.Result, 2);
    }

    [Fact]
    public void Tms_actions_give_the_stop_loss_and_hedge_level_over_time_including_those_under_the_pending_order_name()
    {
        var main = new MainOrder(1035938797, new DateTime(2026, 11, 5, 5, 0, 16, DateTimeKind.Utc), 96.352, 95.722);
        TmAction A(string t, string trade, string role, string action, string price) => new(DateTime.Parse(t + "Z").ToUniversalTime(), trade, role, action, price, "ok");
        TmAction[] actions =
        [
            A("2026-11-05 05:00:16", "O1035938797", "MIR", "PlaceMirrorOrder", "buy-stop 96.352 SL 95.722 TP none"),
            A("2026-11-05 07:04:53", "P692692856", "HMAIN", "PlaceHedge", "95.7220"),
            A("2026-11-05 07:04:53", "P692692856", "MAIN", "SetProtection", "SL none TP none"),
            A("2026-11-05 08:09:20", "P692692856", "HMAIN", "PlaceHedge", "95.7220"),   // same level again: one line
            A("2026-11-05 09:00:00", "P692692856", "HMAIN", "MoveOrder", "95.6000"),
            A("2026-11-06 16:14:30", "P692692856", "MAIN", "SetProtection", "SL 98.2700 TP none"),
            A("2026-11-05 09:30:00", "P999", "HMAIN", "MoveOrder", "1.0"),              // another trade
        ];
        var t = Assert.Single(TradeHistory.Build(Trade[..2], NoPositions, new Dictionary<long, MainOrder> { [692692856] = main }, new Dictionary<string, long>(), actions.OrderBy(a => a.Utc).ToList()));
        Assert.Equal(6, t.Actions.Count);
        Assert.Equal(main.PlacedUtc, t.StartUtc);
        Assert.Equal([95.722, null, 98.27], t.StopLossSteps().Select(s => s.Price));
        Assert.Equal([95.722, 95.6], t.HedgeSteps().Select(s => s.Price));
    }

    [Fact]
    public void Deals_are_written_once_each_into_the_file_of_their_day()
    {
        var account = Path.Combine(_dir, "logs/brokerA/49123893");
        Assert.Equal(12, SnapshotRunner.AppendDeals(account, Trade, new DateOnly(2026, 11, 1)));
        Assert.Equal(0, SnapshotRunner.AppendDeals(account, Trade.Reverse(), new DateOnly(2026, 11, 1)));
        var day5 = CsvTable.Read(Path.Combine(account, "Dashboard/2026-11-05/deals.csv"));
        var day6 = CsvTable.Read(Path.Combine(account, "Dashboard/2026-11-06/deals.csv"));
        Assert.Equal((8, 4), (day5.Rows.Count, day6.Rows.Count));
        Assert.Equal(DealRow.Header, day5.Header);
        var close = day6.Rows[0];
        Assert.Equal(("close", "-131.52", "Scout", "P692692856", "MAIN", "89.832"), (close["kind"], close["net_usd"], close["owner"], close["trade"], close["role"], close["price"]));
    }

    [Fact]
    public void The_trade_picture_shows_levels_fills_and_the_whole_result()
    {
        var candles = Enumerable.Range(0, 48).Select(i => new Candle(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc).AddHours(i), 96, 96.5, 89.5, 95)).ToList();
        var p = new TradePicture("brokerA", 49123893, "P692692856", "Scout", "SOLUSD", 3, "H1", new DateTime(2026, 11, 5, 5, 0, 16, DateTimeKind.Utc),
            new DateTime(2026, 11, 6, 16, 14, 29, DateTimeKind.Utc), 96.352, [new(new DateTime(2026, 11, 5, 5, 0, 16, DateTimeKind.Utc), 95.722)],
            [new(new DateTime(2026, 11, 5, 7, 4, 53, DateTimeKind.Utc), 95.722)], Trade, -45.20, false);
        var svg = TradeChart.Svg(p, candles);
        Assert.StartsWith("<svg", svg);
        Assert.Contains("result −$45.20", svg);
        Assert.Equal(48, Regex.Matches(svg, "class=\"candle\"").Count);
        Assert.Contains("MAIN+MIR", svg);                          // main and mirror filled together: one marker
        Assert.Contains("H 95.722", svg);
        Assert.Single(Regex.Matches(svg, "class=\"entry\""));
        Assert.DoesNotContain(",", Regex.Match(svg, "<line[^>]*class=\"sl\"[^>]*>").Value);   // numbers with '.', whatever the language
        Assert.Equal("H4", TradeChart.TimeframeFor(p.StartUtc, p.StartUtc.AddDays(5)));
    }

    [Fact]
    public void The_export_never_uses_an_order_function()
    {
        // dashboard.md §3.2, §6.1: the snapshot only asks. Every CTraderSession call that can place, change or close an order
        // has one of these names (RequestAsync could send any message); none may appear in this program's code.
        string[] orderCalls = ["PlaceMarketOrderAsync", "ClosePositionAsync", "PlaceStopOrderAsync", "PlacePendingOrderAsync", "AmendPendingOrderAsync",
            "AmendStopOrderAsync", "CancelOrderAsync", "AmendPositionProtectionAsync", "ProtoOANewOrderReq", "ProtoOAClosePositionReq", "ProtoOACancelOrderReq", "ProtoOAAmendOrderReq",
            "ProtoOAAmendPositionSLTPReq", "RequestAsync"];
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Trader.DashboardExport"));
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var code = File.ReadAllText(f);
            Assert.All(orderCalls, call => Assert.DoesNotContain(call, code));
        }
        // And the session's read calls it does use are not order calls.
        var session = typeof(Trader.Core.OpenApi.CTraderSession);
        Assert.All(new[] { "GetDealsAsync", "GetOrdersAsync", "GetTrendbarsAsync", "ReconcileAsync", "GetUnrealizedPnLAsync", "GetTraderAsync" },
            m => Assert.NotNull(session.GetMethod(m)));
    }
}
