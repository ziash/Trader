using System.Globalization;
using System.Text.RegularExpressions;
using Trader.Core.Csv;
using Trader.Core.OpenApi;

namespace Trader.DashboardExport;

/// <summary>Who a position belongs to and which trade it is part of (dashboard.md §2.3): owner, <c>P&lt;mainPositionId&gt;</c>, role.</summary>
public sealed record TradeLink(string Owner, string Trade, string Role);

/// <summary>
/// Reads the bots' order labels (tm.md §7, scout.md §3). A position's label is that of its orders, opening and closing alike.
/// TM's legs name their trade: <c>TM-&lt;hex&gt;-P&lt;main&gt;-MIR|HMAIN|HMIR</c>, or <c>TM-&lt;hex&gt;-O&lt;orderId&gt;-MIR</c> for the mirror
/// of a pending order, whose trade is the position that order opened. Every other position is the main of its own trade.
/// </summary>
public sealed class TradeLinker(IReadOnlyDictionary<long, long> positionOfOrder)
{
    private static readonly Regex TmLeg = new(@"^TM-[0-9A-Za-z]+-(P|O)(\d+)-(MIR|HMAIN|HMIR)$");

    public static string Owner(string label) => label switch
    {
        "" => "manual",
        _ when label.StartsWith("Scout-", StringComparison.Ordinal) => "Scout",
        _ when label.StartsWith("TM-", StringComparison.Ordinal) => "TM",
        _ => Regex.Match(label, "^[A-Za-z0-9]{1,32}") is { Success: true } m ? m.Value : "other",
    };

    public TradeLink Link(string label, long positionId)
    {
        var owner = Owner(label);
        if (TmLeg.Match(label) is { Success: true } m)
        {
            var id = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var trade = m.Groups[1].Value == "P" ? $"P{id}"
                : positionOfOrder.TryGetValue(id, out var main) ? $"P{main}" : $"O{id}";
            return new TradeLink(owner, trade, m.Groups[3].Value);
        }
        return new TradeLink(owner, $"P{positionId}", "MAIN");
    }
}

/// <summary>One row of deals.csv (dashboard.md §3.3).</summary>
public sealed record DealRow(
    DateTime TimeUtc, long DealId, long OrderId, long PositionId, string Symbol, string Side, double Lots, double Price, int Digits,
    bool Close, double? EntryPrice, double Gross, double Swap, double Commission, double? Balance, TradeLink Link, string Label)
{
    public static readonly string[] Header =
    [
        "time_utc", "deal_id", "order_id", "position_id", "symbol", "side", "lots", "price", "kind", "entry_price",
        "gross_usd", "swap_usd", "commission_usd", "net_usd", "balance_usd", "owner", "trade", "role", "label",
    ];

    public double Net => Gross + Swap + Commission;

    public string[] Values() =>
    [
        CsvFormat.Time(TimeUtc), CsvFormat.Int(DealId), CsvFormat.Int(OrderId), CsvFormat.Int(PositionId), Symbol, Side,
        Lots.ToString("0.##", CultureInfo.InvariantCulture), CsvFormat.Num(Price, Digits), Close ? "close" : "open", CsvFormat.Num(EntryPrice, Digits),
        CsvFormat.Num(Gross, 2), CsvFormat.Num(Swap, 2), CsvFormat.Num(Commission, 2), CsvFormat.Num(Net, 2), CsvFormat.Num(Balance, 2),
        Link.Owner, Link.Trade, Link.Role, Label,
    ];
}

/// <summary>What the snapshot needs to know about a symbol: its name, price digits and lot size.</summary>
public sealed record SymbolFacts(long SymbolId, string Name, int Digits, long LotUnits)
{
    public double Lots(long volume) => LotUnits > 0 ? volume / 100.0 / LotUnits : 0;
    public double Round(double price) => Math.Round(price, Digits);
}

public sealed record SnapshotAccount(
    double Balance, double Equity, double MarginUsed, double FreeMargin, double? MarginLevelPct, string Leverage, int Positions, int PendingOrders);

public sealed record SnapshotPosition(
    long PositionId, string Owner, string Trade, string Role, string Symbol, string Side, double Lots, double Entry,
    double? StopLoss, double? TakeProfit, double OpenResultUsd, double MarginUsd, string OpenedUtc, string Label);

public sealed record SnapshotOrder(
    long OrderId, string Owner, string Trade, string Role, string Symbol, string Type, string Side, double Lots, double? Level,
    double? StopLoss, string PlacedUtc, string Label);

/// <summary>snapshot-&lt;broker&gt;-&lt;ctid&gt;.json (dashboard.md §3.2): the account's latest values.</summary>
public sealed record SnapshotFile(
    string Account, string Broker, long Ctid, string TimeUtc, SnapshotAccount Status,
    IReadOnlyList<SnapshotPosition> Positions, IReadOnlyList<SnapshotOrder> Orders, IReadOnlyList<ConditionsRow>? Conditions = null);

/// <summary>Turns the broker's replies into the snapshot, deal rows and trade links. No connection here: the tests feed it directly.</summary>
public static class SnapshotBuilder
{
    public static string Utc(long unixMs) => CsvFormat.Time(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);

    /// <summary>Order → position, from the order history, the deals and the open positions' orders.</summary>
    public static Dictionary<long, long> PositionOfOrder(IEnumerable<ProtoOAOrder> orders, IEnumerable<ProtoOADeal> deals)
    {
        var map = new Dictionary<long, long>();
        foreach (var d in deals)
            map.TryAdd(d.OrderId, d.PositionId);
        foreach (var o in orders.Where(o => o.PositionId > 0))
            map.TryAdd(o.OrderId, o.PositionId);
        return map;
    }

    /// <summary>Each position's label, from its orders (opening and closing orders carry it) and the open positions.</summary>
    public static Dictionary<long, string> LabelOfPosition(IEnumerable<ProtoOAOrder> orders, ProtoOAReconcileRes reconcile)
    {
        var map = new Dictionary<long, string>();
        foreach (var p in reconcile.Position)
            map[p.PositionId] = p.TradeData.Label ?? "";
        foreach (var o in orders.Where(o => o.PositionId > 0))
            map.TryAdd(o.PositionId, o.TradeData.Label ?? "");
        return map;
    }

    public static DealRow Deal(ProtoOADeal d, SymbolFacts s, string label, TradeLinker linker)
    {
        var digits = (int)(d.HasMoneyDigits ? d.MoneyDigits : 2);
        double Money(long v, uint? md = null) => v / Math.Pow(10, md ?? (uint)digits);
        var c = d.ClosePositionDetail;
        var md = c is { HasMoneyDigits: true } ? c.MoneyDigits : (uint)digits;
        return new DealRow(
            DateTimeOffset.FromUnixTimeMilliseconds(d.ExecutionTimestamp).UtcDateTime, d.DealId, d.OrderId, d.PositionId, s.Name,
            d.TradeSide == ProtoOATradeSide.Buy ? "buy" : "sell", s.Lots(d.FilledVolume), s.Round(d.ExecutionPrice), s.Digits,
            c is not null, c is null ? null : s.Round(c.EntryPrice),
            c is null ? 0 : Money(c.GrossProfit, md), c is null ? 0 : Money(c.Swap, md),
            c is null ? Money(d.Commission) : Money(c.Commission, md), c is null ? null : Money(c.Balance, md),
            linker.Link(label, d.PositionId), label);
    }

    public static SnapshotFile Snapshot(
        DashAccount account, DateTime nowUtc, ProtoOATrader trader, ProtoOAReconcileRes reconcile, ProtoOAGetPositionUnrealizedPnLRes pnl,
        IReadOnlyDictionary<long, SymbolFacts> symbols, TradeLinker linker)
    {
        var status = AccountStatus.From(trader, reconcile, pnl, nowUtc);
        var leverage = trader.HasLeverageInCents ? $"1:{trader.LeverageInCents / 100}" : "";
        var net = pnl.Positions.ToDictionary(p => p.PositionId, p => Money(p.Net, pnl.MoneyDigits));
        SymbolFacts Sym(long id) => symbols.TryGetValue(id, out var s) ? s : new SymbolFacts(id, $"symbol {id}", 5, 1);

        var positions = reconcile.Position.Select(p =>
        {
            var s = Sym(p.TradeData.SymbolId);
            var label = p.TradeData.Label ?? "";
            var link = linker.Link(label, p.PositionId);
            return new SnapshotPosition(p.PositionId, link.Owner, link.Trade, link.Role, s.Name, Side(p.TradeData.TradeSide), s.Lots(p.TradeData.Volume),
                s.Round(p.Price), p.HasStopLoss ? s.Round(p.StopLoss) : null, p.HasTakeProfit ? s.Round(p.TakeProfit) : null,
                Math.Round(net.GetValueOrDefault(p.PositionId), 2), Math.Round(Money((long)p.UsedMargin, p.HasMoneyDigits ? p.MoneyDigits : 2), 2),
                Utc(p.TradeData.OpenTimestamp), label);
        }).OrderBy(p => p.Symbol, StringComparer.Ordinal).ThenBy(p => p.Trade, StringComparer.Ordinal).ThenBy(p => p.PositionId).ToList();

        var orders = reconcile.Order.Where(o => !o.ClosingOrder).Select(o =>
        {
            var s = Sym(o.TradeData.SymbolId);
            var label = o.TradeData.Label ?? "";
            var link = o.PositionId > 0 ? linker.Link(label, o.PositionId) : linker.Link(label, 0) with { Trade = TradeOfPending(label, o.OrderId, linker) };
            double? level = o.HasStopPrice ? s.Round(o.StopPrice) : o.HasLimitPrice ? s.Round(o.LimitPrice) : null;
            return new SnapshotOrder(o.OrderId, link.Owner, link.Trade, link.Role, s.Name, o.OrderType.ToString().ToLowerInvariant(), Side(o.TradeData.TradeSide),
                s.Lots(o.TradeData.Volume), level, o.HasStopLoss ? s.Round(o.StopLoss) : null, Utc(o.TradeData.OpenTimestamp), label);
        }).OrderBy(o => o.Symbol, StringComparer.Ordinal).ThenBy(o => o.OrderId).ToList();

        return new SnapshotFile(account.Id, account.Broker, account.AccountId, CsvFormat.Time(nowUtc),
            new SnapshotAccount(Math.Round(status.Balance, 2), Math.Round(status.Equity, 2), Math.Round(status.UsedMargin, 2), Math.Round(status.FreeMargin, 2),
                status.MarginLevel is { } l ? Math.Round(l, 1) : null, leverage, positions.Count, orders.Count),
            positions, orders);
    }

    /// <summary>A pending order has no position yet: TM's logs name its trade O&lt;orderId&gt; (tm.md §7), and so does this.</summary>
    private static string TradeOfPending(string label, long orderId, TradeLinker linker) =>
        linker.Link(label, 0) is { Role: not "MAIN" } leg ? leg.Trade : $"O{orderId}";

    private static double Money(long value, uint digits) => value / Math.Pow(10, digits);

    private static string Side(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? "buy" : "sell";
}
