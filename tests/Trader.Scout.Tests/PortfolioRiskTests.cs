using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>The money at risk across the account (scout.md §2.9, review P-03). Equity is $100,000 and one lot of every symbol is 100,000 units, so 0.01 of price on a lot is $1,000.</summary>
public sealed class PortfolioRiskTests
{
    private const long Lot = 10_000_000;
    private const double Equity = 100_000;
    private const string ScoutM = "Scout-abcdef-H1-202610011900-M", ScoutR = "Scout-abcdef-H1-202610011900-R";

    private static readonly string[] Names = ["EURUSD", "GBPUSD", "USDJPY", "GBPJPY", "XAUUSD", "XAGUSD", "US500", "USTEC", "DE40", "BTCUSD", "XNGUSD", "XTIUSD"];

    private static readonly Dictionary<long, SymbolInfo> Symbols = Names.Select((n, i) => Sym(i + 1, n)).ToDictionary(s => s.SymbolId);

    private static readonly PortfolioSettings Wide = new(true, TradePercent: 100, TotalPercent: 100, SymbolPercent: 100, GroupPercent: 100, MaxTrades: 1000, MaxPendingOrders: 1000);

    private static SymbolInfo Sym(long id, string name, long min = 100_000) =>
        new(id, name, name, "EUR", "USD", 5, 4, 100_000, true, ProtoOATradingMode.Enabled, TradingSchedule.AlwaysOpen, min, 100_000);

    private static long Id(string name) => Array.IndexOf(Names, name) + 1;

    private static ProtoOAPosition Pos(long id, string symbol, int dir, double price, double? stop = null, string label = "", long volume = Lot)
    {
        var p = new ProtoOAPosition { PositionId = id, Price = price, TradeData = new ProtoOATradeData { SymbolId = Id(symbol), Volume = volume, TradeSide = dir > 0 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell, Label = label } };
        if (stop is { } s)
            p.StopLoss = s;
        return p;
    }

    private static ProtoOAOrder Ord(long id, string symbol, int dir, double price, double? stop = null, string label = "", long volume = Lot)
    {
        var o = new ProtoOAOrder { OrderId = id, OrderType = ProtoOAOrderType.Stop, StopPrice = price, TradeData = new ProtoOATradeData { SymbolId = Id(symbol), Volume = volume, TradeSide = dir > 0 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell, Label = label } };
        if (stop is { } s)
            o.StopLoss = s;
        return o;
    }

    private static RiskBook Book(PortfolioSettings? limits, IEnumerable<ProtoOAPosition>? positions = null, IEnumerable<ProtoOAOrder>? orders = null, double? equity = Equity,
        Dictionary<long, long>? links = null, double? rate = 1, SizingSettings? sizing = null)
    {
        var rec = new ProtoOAReconcileRes();
        rec.Position.AddRange(positions ?? []);
        rec.Order.AddRange(orders ?? []);
        return RiskBook.Build(limits ?? Wide, equity, new RiskInputs(rec, Symbols, _ => rate, _ => 0, (l, id) => AccountScout.KeyOf(l, id, Symbols),
            sizing ?? new SizingSettings(SlippageSpreads: 0), links ?? []));
    }

    private static PortfolioDecision Try(RiskBook book, string symbol, int dir = 1, double perLot = 1000, long volume = Lot, string trade = "new", int legs = 1) =>
        book.Decide(Symbols[Id(symbol)], volume, perLot, legs, trade, dir);

    // ── How each thing on the account counts ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void A_position_counts_to_its_stop_and_nothing_once_the_stop_is_at_break_even()
    {
        Assert.Equal(1000, Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.09)]).Total, 6);
        Assert.Equal(1000, Book(null, [Pos(1, "EURUSD", -1, 1.10, 1.11)]).Total, 6);
        Assert.Equal(0, Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.10)]).Total, 6);
        Assert.Equal(0, Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.12)]).Total, 6);
    }

    [Fact]
    public void A_leg_with_only_the_backstop_counts_at_the_backstop()
    {
        Assert.Equal(3000, Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.07)]).Total, 6);
    }

    [Fact]
    public void A_leg_with_a_hedge_order_counts_one_R_plus_the_cycle_allowance()
    {
        var hedge = Ord(5, "EURUSD", -1, 1.09, label: "TM-abcdef-P1-HMAIN");
        var book = Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.07)], [hedge]);
        Assert.Equal(1500, book.Total, 6);
        Assert.Equal(0, book.PendingOrders);
        Assert.Equal(1, book.Trades);
    }

    [Fact]
    public void A_nearer_stop_caps_what_a_hedge_order_leg_can_lose()
    {
        var hedge = Ord(5, "EURUSD", -1, 1.09, label: "TM-abcdef-P1-HMAIN");
        Assert.Equal(500, Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.095)], [hedge]).Total, 6);
    }

    [Fact]
    public void A_filled_hedge_counts_the_frozen_loss_plus_the_risk_to_its_stop()
    {
        var leg = Pos(1, "EURUSD", 1, 1.10);
        Assert.Equal(1500, Book(null, [leg, Pos(2, "EURUSD", -1, 1.09, 1.095, "TM-abcdef-P1-HMAIN")]).Total, 6);
        // armed: the hedge's stop is past its fill, so it can only gain
        Assert.Equal(1000, Book(null, [leg, Pos(2, "EURUSD", -1, 1.09, 1.0899, "TM-abcdef-P1-HMAIN")]).Total, 6);
        // its stop not on yet
        Assert.Equal(1500, Book(null, [leg, Pos(2, "EURUSD", -1, 1.09, null, "TM-abcdef-P1-HMAIN")]).Total, 6);
    }

    [Fact]
    public void A_mirror_from_a_position_and_its_hedge_are_one_trade_with_the_main()
    {
        var book = Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.09), Pos(2, "EURUSD", 1, 1.10, 1.07, "TM-abcdef-P1-MIR")], [Ord(5, "EURUSD", -1, 1.09, label: "TM-abcdef-P1-HMIR")]);
        Assert.Equal(1000 + 1500, book.Total, 6);
        Assert.Equal(1, book.Trades);
    }

    [Fact]
    public void A_mirror_that_filled_from_an_order_is_linked_through_TMs_state()
    {
        // Its label holds the order's id, not the main's: only TM's state joins them. Without the link it stands on its own stop.
        var mirror = Pos(2, "EURUSD", 1, 1.10, 1.07, "TM-abcdef-O77-MIR");
        var hedge = Ord(5, "EURUSD", -1, 1.09, label: "TM-abcdef-P1-HMIR");
        var main = Pos(1, "EURUSD", 1, 1.10, 1.09);

        var linked = Book(null, [main, mirror], [hedge], links: new() { [2] = 1 });
        Assert.Equal(1000 + 1500, linked.Total, 6);
        Assert.Equal(1, linked.Trades);

        var unlinked = Book(null, [main, mirror], [hedge]);
        Assert.Equal(1000 + 3000, unlinked.Total, 6);
        Assert.Equal(2, unlinked.Trades);
        Assert.Null(unlinked.Problem);
    }

    [Fact]
    public void A_hedged_mirror_with_no_link_and_no_stop_stops_new_orders()
    {
        var book = Book(null, [Pos(2, "EURUSD", 1, 1.10, null, "TM-abcdef-O77-MIR")]);
        Assert.Contains("no limit", book.Problem);
    }

    [Fact]
    public void Main_and_reversal_of_one_pause_count_once_at_the_larger()
    {
        var book = Book(null, null, [Ord(1, "EURUSD", 1, 1.10, 1.09, ScoutM), Ord(2, "EURUSD", -1, 1.08, 1.075, ScoutR)], sizing: new SizingSettings(Legs: 1, SlippageSpreads: 0));
        Assert.Equal(1000, book.Total, 6);
        Assert.Equal(1, book.Trades);
        Assert.Equal(2, book.PendingOrders);
    }

    [Fact]
    public void A_Scout_order_counts_all_its_legs_and_its_mirror_order_is_not_counted_again()
    {
        var book = Book(null, null, [Ord(1, "EURUSD", 1, 1.10, 1.09, ScoutM), Ord(2, "EURUSD", 1, 1.10, 1.09, "TM-abcdef-O1-MIR")]);
        Assert.Equal(2000, book.Total, 6);
        Assert.Equal(1, book.PendingOrders);
    }

    [Fact]
    public void The_owners_positions_and_orders_are_counted()
    {
        Assert.Equal(1000, Book(null, [Pos(1, "XAUUSD", 1, 1.10, 1.09)]).OnSymbol("XAUUSD"), 6);
        var book = Book(null, null, [Ord(1, "EURUSD", 1, 1.10, 1.09), Ord(2, "EURUSD", 1, 1.10, 1.09, "TM-abcdef-O1-MIR")]);
        Assert.Equal(2000, book.Total, 6);
        Assert.Equal(1, book.Trades);
        Assert.Equal(1, book.PendingOrders);
    }

    [Fact]
    public void The_owners_order_without_a_stop_takes_it_from_its_mirror()
    {
        var book = Book(null, null, [Ord(1, "EURUSD", 1, 1.10), Ord(2, "EURUSD", 1, 1.10, 1.09, "TM-abcdef-O1-MIR")]);
        Assert.Equal(2000, book.Total, 6);
        Assert.Null(book.Problem);
    }

    // ── Refusing rather than guessing ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void A_position_with_nothing_limiting_its_loss_stops_new_orders()
    {
        var book = Book(null, [Pos(1, "EURUSD", 1, 1.10)]);
        Assert.Contains("no stop-loss and no hedge", Try(book, "XAUUSD").Refusal);
    }

    [Fact]
    public void An_order_without_any_stop_stops_new_orders()
    {
        Assert.Contains("no stop-loss", Try(Book(null, null, [Ord(1, "EURUSD", 1, 1.10)]), "XAUUSD").Refusal);
    }

    [Fact]
    public void Stale_or_missing_data_refuses()
    {
        Assert.Contains("account status", Try(Book(null, equity: null), "EURUSD").Refusal);
        Assert.Contains("converts", Try(Book(null, [Pos(1, "EURUSD", 1, 1.10, 1.09)], rate: null), "XAUUSD").Refusal);
        var unknown = Pos(1, "EURUSD", 1, 1.10, 1.09);
        unknown.TradeData.SymbolId = 999;
        Assert.Contains("does not follow", Try(Book(null, [unknown]), "XAUUSD").Refusal);
        var book = Book(null);
        book.Fail("what became of the order is not known");
        Assert.Contains("not known", Try(book, "XAUUSD").Refusal);
    }

    // ── The limits ──────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Above_the_limit_per_trade_the_size_is_reduced()
    {
        var d = Try(Book(new PortfolioSettings(true, TradePercent: 0.5)), "EURUSD", perLot: 1000, volume: Lot);
        Assert.Equal(Lot / 2, d.Volume); // $500 is 0.5 % of equity
        Assert.Equal(500, d.RiskCash, 6);
    }

    [Fact]
    public void Below_the_brokers_minimum_after_reducing_it_is_refused_and_never_raised()
    {
        var d = Try(Book(new PortfolioSettings(true, TradePercent: 0.0001)), "EURUSD", perLot: 1000, volume: Lot);
        Assert.Null(d.Volume);
        Assert.Contains("minimum", d.Refusal);
    }

    [Fact]
    public void The_limit_across_all_trades_refuses()
    {
        var limits = Wide with { TotalPercent = 5 };
        var book = Book(limits, [Pos(1, "EURUSD", 1, 1.10, 1.06)]);       // $4,000
        Assert.Null(Try(book, "XAUUSD", perLot: 1000).Refusal);           // $5,000: exactly the limit
        Assert.Contains("all trades", Try(book, "XAUUSD", perLot: 1001).Refusal);
    }

    [Fact]
    public void The_limit_per_symbol_caps_the_staircase()
    {
        var limits = Wide with { SymbolPercent = 1 };
        var book = Book(limits, [Pos(1, "XAUUSD", 1, 1.10, 1.095)]);      // $500
        Assert.Null(Try(book, "XAUUSD", perLot: 500).Refusal);
        Assert.Contains("XAUUSD", Try(book, "XAUUSD", perLot: 501).Refusal);
        Assert.Null(Try(book, "BTCUSD", perLot: 900).Refusal);
    }

    [Fact]
    public void The_limit_per_group_refuses_within_the_group_only()
    {
        var limits = Wide with { GroupPercent = 2 };
        var book = Book(limits, [Pos(1, "XAUUSD", 1, 1.10, 1.09), Pos(2, "XAGUSD", 1, 1.10, 1.095)]); // $1,500 in metals
        Assert.Contains("group Metals", Try(book, "XAUUSD", perLot: 501).Refusal);
        Assert.Null(Try(book, "BTCUSD", perLot: 501).Refusal);
    }

    [Fact]
    public void A_group_refuses_a_trade_of_either_direction()
    {
        var limits = Wide with { GroupPercent = 1 };
        var book = Book(limits, [Pos(1, "XAUUSD", 1, 1.10, 1.09)]);
        Assert.Contains("group Metals", Try(book, "XAGUSD", dir: -1, perLot: 1).Refusal);
    }

    [Fact]
    public void Dollars_count_by_their_direction()
    {
        var limits = Wide with { GroupPercent = 1 };
        var longEur = Book(limits, [Pos(1, "EURUSD", 1, 1.10, 1.09)]);    // short dollars, $1,000

        Assert.Contains("group USD short", Try(longEur, "USDJPY", dir: -1, perLot: 1).Refusal); // short USDJPY is short dollars too
        Assert.Null(Try(longEur, "USDJPY", dir: 1, perLot: 1).Refusal);   // long USDJPY is long dollars: another bucket
    }

    [Fact]
    public void A_looser_member_counts_at_half_in_its_group()
    {
        var limits = Wide with { GroupPercent = 1 };
        var book = Book(limits, [Pos(1, "DE40", 1, 1.10, 1.09)]);         // $1,000 of index, $500 in the group
        Assert.Equal(500, book.InBucket("US equity"), 6);
        Assert.Null(Try(book, "US500", perLot: 500).Refusal);
        Assert.Contains("group US equity", Try(book, "US500", perLot: 501).Refusal);
    }

    [Fact]
    public void The_other_order_of_a_pause_adds_only_what_is_above_the_first()
    {
        var limits = Wide with { TotalPercent = 1 };
        var book = Book(limits, null, [Ord(1, "EURUSD", 1, 1.10, 1.09, ScoutM)], sizing: new SizingSettings(Legs: 1, SlippageSpreads: 0)); // $1,000: at the limit
        var pause = "EURUSD|H1|202610011900";
        Assert.Null(Try(book, "EURUSD", dir: -1, perLot: 800, trade: pause).Refusal);
        Assert.Contains("all trades", Try(book, "XAUUSD", perLot: 1, trade: "other").Refusal);
    }

    [Fact]
    public void Trades_are_limited_and_the_second_order_of_a_pause_is_not_another_trade()
    {
        var limits = Wide with { MaxTrades = 2 };
        var book = Book(limits, [Pos(1, "XAUUSD", 1, 1.10, 1.09)], [Ord(1, "EURUSD", 1, 1.10, 1.09, ScoutM)]);
        Assert.Contains("trades", Try(book, "BTCUSD").Refusal);
        Assert.Null(Try(book, "EURUSD", dir: -1, trade: "EURUSD|H1|202610011900").Refusal);
    }

    [Fact]
    public void Waiting_entry_orders_are_limited_and_TMs_orders_do_not_count()
    {
        var limits = Wide with { MaxPendingOrders = 1 };
        Assert.Contains("entry orders", Try(Book(limits, null, [Ord(1, "EURUSD", 1, 1.10, 1.09, ScoutM)]), "XAUUSD").Refusal);
        Assert.Null(Try(Book(limits, [Pos(1, "EURUSD", 1, 1.10, 1.07)], [Ord(5, "EURUSD", -1, 1.09, label: "TM-abcdef-P1-HMAIN")]), "XAUUSD").Refusal);
    }

    [Fact]
    public void What_was_just_placed_counts_for_the_next_order_of_the_same_round()
    {
        var limits = Wide with { TotalPercent = 1 };
        var book = Book(limits);
        Assert.Null(Try(book, "EURUSD").Refusal);
        book.Take("EURUSD", 1, 1000, "a");
        Assert.Contains("all trades", Try(book, "XAUUSD", perLot: 1, trade: "b").Refusal);
    }

    [Fact]
    public void A_refused_order_is_not_counted()
    {
        var book = Book(Wide with { TotalPercent = 1 });
        Assert.NotNull(Try(book, "EURUSD", perLot: 2000).Refusal);
        Assert.Equal(0, book.Total);
        Assert.Empty(book.Items);
    }

    [Fact]
    public void TMs_state_gives_the_links_and_an_unreadable_file_gives_none()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tm-links-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[{"MainId":10,"Mirror":{"PositionId":20,"R":1}},{"MainId":11,"Mirror":null},{"MainId":0,"Mirror":{"PositionId":30}}]""");
            Assert.Equal(new Dictionary<long, long> { [20] = 10 }, TmLinks.Load(path));
            File.WriteAllText(path, "not json");
            Assert.Empty(TmLinks.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Empty(TmLinks.Load(path));
    }
}
