using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trader.Core.Hosting;
using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>
/// Limits on the money at risk across the whole account (scout.md §2.9, review P-03). Off by default: <c>"portfolio": { "enabled": true }</c> in scout.json.
/// Percentages are of equity. Starting values, to be calibrated on replay.
/// </summary>
/// <param name="TradePercent">One trade, all legs. Above it the size is reduced, or the order refused if the broker's minimum is still too much.</param>
/// <param name="TotalPercent">Everything open and pending together.</param>
/// <param name="SymbolPercent">One symbol: caps the staircase of add-on entries.</param>
/// <param name="GroupPercent">One group of symbols that tend to move together (<see cref="RiskGroups"/>).</param>
/// <param name="MarginUsePercent">Margin use allowed while this is on: 33 % is TM's 300 % margin level, so Scout stops adding trades before TM stops mirroring them.</param>
/// <param name="MaxTrades">Trades open and pauses pending together: a pending trade is counted as if it had filled.</param>
/// <param name="MaxPendingOrders">Entry orders waiting on the broker. TM's hedge and mirror orders are not counted.</param>
/// <param name="HedgeCycleR">Added to a leg guarded by a hedge order, as a fraction of its distance to the hedge: the cost of a hedge that fills and is stopped out.</param>
public sealed record PortfolioSettings(
    bool Enabled = false,
    double TradePercent = 0.5,
    double TotalPercent = 5,
    double SymbolPercent = 1,
    double GroupPercent = 2,
    double MarginUsePercent = 33,
    int MaxTrades = 8,
    int MaxPendingOrders = 12,
    double HedgeCycleR = 0.5);

/// <summary>Symbols that tend to move together (review P-03). A static map to start with; a looser member counts at half.</summary>
internal static class RiskGroups
{
    private static readonly (string Group, string Symbol, double Weight, int UsdSide)[] Members =
    [
        ("USD", "EURUSD", 1, -1), ("USD", "GBPUSD", 1, -1), ("USD", "USDJPY", 1, 1), // buying EURUSD sells dollars, buying USDJPY buys them
        ("JPY", "USDJPY", 1, 0), ("JPY", "GBPJPY", 1, 0), ("JPY", "EURJPY", 1, 0), ("JPY", "AUDJPY", 1, 0),
        ("Metals", "XAUUSD", 1, 0), ("Metals", "XAGUSD", 1, 0), ("Metals", "XPTUSD", 1, 0),
        ("US equity", "US500", 1, 0), ("US equity", "USTEC", 1, 0), ("US equity", "DE40", 0.5, 0),
        ("Crypto", "BTCUSD", 1, 0), ("Crypto", "ETHUSD", 1, 0), ("Crypto", "SOLUSD", 1, 0),
        ("Energy", "XTIUSD", 1, 0), ("Energy", "XBRUSD", 1, 0), ("Energy", "XNGUSD", 0.5, 0),
    ];

    /// <summary>The groups a trade counts in. USD is split by the dollar's direction: long and short dollars do not add up.</summary>
    public static IEnumerable<(string Bucket, double Weight)> Of(string symbol, int dir) =>
        Members.Where(m => m.Symbol == symbol).Select(m => (m.UsdSide == 0 ? m.Group : $"{m.Group} {(m.UsdSide * dir > 0 ? "long" : "short")}", m.Weight));
}

/// <summary>Which trade each mirror belongs to, read from TM's saved state (<c>~/tradebot/run/tm/</c>). Unreadable = no links: mirrors then count on their own stop-loss, or stop new orders if they have none.</summary>
internal static class TmLinks
{
    private sealed record Leg(long PositionId);

    private sealed record Trade(long MainId, Leg? Mirror);

    public static IReadOnlyDictionary<long, long> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<long, long>();
            var trades = JsonSerializer.Deserialize<List<Trade>>(File.ReadAllText(path)) ?? [];
            return trades.Where(t => t is { MainId: not 0, Mirror.PositionId: not 0 }).GroupBy(t => t.Mirror!.PositionId).ToDictionary(g => g.Key, g => g.First().MainId);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<long, long>();
        }
    }
}

/// <param name="Trade">What it belongs to: legs of one trade add up.</param>
/// <param name="OneOf">Only one item of this <paramref name="Trade"/> can happen (the two orders of a pause): the largest counts.</param>
internal sealed record RiskItem(string Trade, string Symbol, int Dir, double Usd, bool Pending, bool OneOf, string What);

/// <summary>The order's volume, perhaps reduced, and its risk in USD; or why it is refused.</summary>
internal sealed record PortfolioDecision(long? Volume, double RiskCash, string? Refusal);

/// <summary>What the book is built from: the broker's own list of positions and orders, and how to value them.</summary>
/// <param name="MainOfMirror">Mirror positionId → its main's positionId, from TM's state (<see cref="TmLinks"/>). A mirror that filled from an order carries the order's id in its label, so the broker's
/// data alone does not say which trade it is in. A mirror not listed is counted on its own stop-loss.</param>
internal sealed record RiskInputs(
    ProtoOAReconcileRes Reconcile,
    IReadOnlyDictionary<long, SymbolInfo> Symbols,
    Func<string, double?> UsdPer,
    Func<long, double> SpreadOf,
    Func<string?, long, string?> ScoutKey,
    SizingSettings Sizing,
    IReadOnlyDictionary<long, long> MainOfMirror);

/// <summary>
/// The money at risk on one account, from the broker's confirmed positions and orders, and the decision on each new order (scout.md §2.9). A calculation
/// only: no broker, no clock. It decides about new risk alone: nothing here touches what is already on the account, or TM's protection of it.
/// When something cannot be valued it refuses rather than guesses (<see cref="Problem"/>).
/// </summary>
internal sealed partial class RiskBook(PortfolioSettings limits, double? equity)
{
    private const double Tolerance = 1e-6;
    private readonly List<RiskItem> _items = [];

    public IReadOnlyList<RiskItem> Items => _items;

    public int PendingOrders { get; private set; }

    /// <summary>Why no new risk can be judged now: something on the account has no known limit to its loss, or cannot be valued.</summary>
    public string? Problem { get; private set; }

    public void Fail(string why) => Problem ??= why;

    public static RiskBook Build(PortfolioSettings limits, double? equity, RiskInputs input)
    {
        var book = new RiskBook(limits, equity);
        book.AddPositions(input);
        book.AddOrders(input);
        return book;
    }

    [GeneratedRegex(@"^([PO])(\d+)-(MIR|HMAIN|HMIR)$")]
    private static partial Regex TmLegPattern();

    /// <summary>A TM label (tm.md header): <c>P&lt;mainPositionId&gt;-MIR</c>, <c>-HMAIN</c>, <c>-HMIR</c>, or <c>O&lt;ownerOrderId&gt;-MIR</c> for a mirror order.</summary>
    private static (long Id, string Role, bool ByOrder)? TmLeg(string? label)
    {
        if (!BotIdentity.TryParseLabel(label, out var bot, out _, out var leg) || bot != ScoutConfig.TmBotName || leg is null)
            return null;
        var m = TmLegPattern().Match(leg);
        return m.Success ? (long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, m.Groups[1].Value == "O") : null;
    }

    private static bool IsHedge(string role) => role is "HMAIN" or "HMIR";

    private static int DirOf(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? 1 : -1;

    private void AddPositions(RiskInputs input)
    {
        var rec = input.Reconcile;
        var ids = rec.Position.Select(p => p.PositionId).ToHashSet();
        long MainIdOfMirror(ProtoOAPosition p, (long Id, string Role, bool ByOrder) leg) =>
            leg.ByOrder ? input.MainOfMirror.GetValueOrDefault(p.PositionId, p.PositionId) : leg.Id;
        var mirrored = rec.Position.Select(p => (Pos: p, Leg: TmLeg(p.TradeData.Label))).Where(x => x.Leg is { Role: "MIR" }).Select(x => MainIdOfMirror(x.Pos, x.Leg!.Value)).ToHashSet();
        var hedgeOrders = new Dictionary<(long, string), ProtoOAOrder>();
        foreach (var o in rec.Order)
            if (TmLeg(o.TradeData.Label) is { ByOrder: false } leg && IsHedge(leg.Role))
                hedgeOrders[(leg.Id, leg.Role)] = o;
        var hedges = new Dictionary<(long, string), ProtoOAPosition>();
        foreach (var p in rec.Position)
            if (TmLeg(p.TradeData.Label) is { ByOrder: false } leg && IsHedge(leg.Role))
                hedges[(leg.Id, leg.Role)] = p;

        foreach (var p in rec.Position)
        {
            var tm = TmLeg(p.TradeData.Label);
            if (tm is { Role: "HMAIN" } h1 && ids.Contains(h1.Id) || tm is { Role: "HMIR" } h2 && mirrored.Contains(h2.Id))
                continue; // counted with its leg
            var isMirror = tm is { Role: "MIR" };
            var mainId = tm is not { } own ? p.PositionId : isMirror ? MainIdOfMirror(p, own) : own.Id;
            var guarded = tm is null || isMirror; // a leg; a hedge whose leg has gone stands on its own stop-loss
            var hedgeRole = isMirror ? "HMIR" : "HMAIN";
            var what = $"position {p.PositionId}";
            if (Value(input, p.TradeData.SymbolId, what) is not { } v)
                continue;

            var (dir, entry, units) = (DirOf(p.TradeData.TradeSide), p.Price, p.TradeData.Volume / 100.0);
            double Usd(double priceDistance, double u) => Math.Max(0, priceDistance) * u * v.Rate;
            double? atStop = p.HasStopLoss ? Usd(dir * (entry - p.StopLoss), units) : null;

            double risk;
            string how;
            if (hedges.GetValueOrDefault((mainId, hedgeRole)) is { } hedge && guarded)
            {
                var frozen = Usd(dir * (entry - hedge.Price), units);
                var hedgeUnits = hedge.TradeData.Volume / 100.0;
                risk = frozen + (hedge.HasStopLoss ? Usd(dir * (hedge.StopLoss - hedge.Price), hedgeUnits) : frozen * limits.HedgeCycleR);
                how = "hedge filled: frozen loss plus the hedge's stop";
            }
            else if (hedgeOrders.GetValueOrDefault((mainId, hedgeRole)) is { } order && guarded)
            {
                risk = Usd(dir * (entry - order.StopPrice), units) * (1 + limits.HedgeCycleR);
                risk = atStop is { } stop ? Math.Min(risk, stop) : risk;
                how = "hedge order waiting";
            }
            else if (atStop is { } stop)
            {
                (risk, how) = (stop, "stop on the server");
            }
            else
            {
                Fail($"{v.Symbol.Name} {what} has no stop-loss and no hedge: its loss has no limit");
                continue;
            }
            _items.Add(new RiskItem($"P{mainId}", v.Symbol.Name, dir, risk, false, false, $"{what}: {how}"));
        }
    }

    private void AddOrders(RiskInputs input)
    {
        var rec = input.Reconcile;
        var entries = rec.Order.Where(IsEntry).ToList();
        var byId = entries.ToDictionary(o => o.OrderId);
        var mirrorOf = new Dictionary<long, ProtoOAOrder>();
        foreach (var o in entries)
            if (TmLeg(o.TradeData.Label) is { ByOrder: true, Role: "MIR" } leg)
                mirrorOf[leg.Id] = o;

        foreach (var o in entries)
        {
            var label = o.TradeData.Label;
            var tm = TmLeg(label);
            if (tm is { } t && IsHedge(t.Role))
                continue; // guards a position: counted with its leg
            var key = input.ScoutKey(label, o.TradeData.SymbolId);
            var mainOrder = tm is { ByOrder: true } m ? byId.GetValueOrDefault(m.Id) : null;
            if (mainOrder is not null && input.ScoutKey(mainOrder.TradeData.Label, mainOrder.TradeData.SymbolId) is not null)
                continue; // the mirror of a Scout order: already in that order's legs
            var what = $"order {o.OrderId}";
            if (Value(input, o.TradeData.SymbolId, what) is not { } v)
                continue;

            double? stopLoss = o.HasStopLoss ? o.StopLoss : mirrorOf.TryGetValue(o.OrderId, out var mirror) && mirror.HasStopLoss ? mirror.StopLoss : null;
            if (stopLoss is not { } sl)
            {
                Fail($"{v.Symbol.Name} {what} has no stop-loss: its loss has no limit");
                continue;
            }
            var (dir, entry) = (DirOf(o.TradeData.TradeSide), EntryPrice(o));
            var distance = Math.Max(0, dir * (entry - sl));
            var lots = v.Symbol.LotsForVolume(o.TradeData.Volume);
            if (tm is null)
                PendingOrders++;
            if (key is not null)
            {
                var perLot = PositionSizer.LossPerLot(v.Symbol, input.Sizing, entry, distance, input.SpreadOf(o.TradeData.SymbolId), v.Rate);
                _items.Add(new RiskItem(PauseOf(key), v.Symbol.Name, dir, input.Sizing.Legs * lots * perLot, true, true, $"{what}: Scout, as if filled"));
            }
            else
            {
                var trade = mainOrder is not null ? $"O{mainOrder.OrderId}" : $"O{o.OrderId}";
                _items.Add(new RiskItem(trade, v.Symbol.Name, dir, distance * (o.TradeData.Volume / 100.0) * v.Rate, true, false, $"{what}: as if filled"));
            }
        }
    }

    /// <summary>A trade: the two orders of one pause share it (only one can fill).</summary>
    public static string PauseOf(string key) => key[..^2];

    private static bool IsEntry(ProtoOAOrder o) => o.OrderType is ProtoOAOrderType.Stop or ProtoOAOrderType.Limit or ProtoOAOrderType.StopLimit;

    private static double EntryPrice(ProtoOAOrder o) => o.OrderType == ProtoOAOrderType.Limit ? o.LimitPrice : o.StopPrice;

    private (SymbolInfo Symbol, double Rate)? Value(RiskInputs input, long symbolId, string what)
    {
        if (!input.Symbols.TryGetValue(symbolId, out var s))
        {
            Fail($"{what} is on a symbol Scout does not follow (id {symbolId}), so it cannot be valued");
            return null;
        }
        if (input.UsdPer(s.QuoteAsset) is not { } rate || rate <= 0)
        {
            Fail($"no price converts {s.QuoteAsset} to USD, so {s.Name} {what} cannot be valued");
            return null;
        }
        return (s, rate);
    }

    private double Sum(Func<RiskItem, double> weight, RiskItem? extra = null) =>
        _items.Concat(extra is null ? [] : [extra]).Where(i => weight(i) > 0).GroupBy(i => i.Trade)
            .Sum(g => g.First().OneOf ? g.Max(i => i.Usd * weight(i)) : g.Sum(i => i.Usd * weight(i)));

    private static double BucketWeight(RiskItem i, string bucket) => RiskGroups.Of(i.Symbol, i.Dir).Where(b => b.Bucket == bucket).Select(b => b.Weight).FirstOrDefault();

    public int Trades => _items.Select(i => i.Trade).Distinct().Count();

    public double Total => Sum(_ => 1);

    public double OnSymbol(string symbol) => Sum(i => i.Symbol == symbol ? 1 : 0);

    public double InBucket(string bucket) => Sum(i => BucketWeight(i, bucket));

    /// <summary>
    /// The decision on one new order. <paramref name="lossPerLot"/>: one leg's loss per lot at the stop, with its costs. <paramref name="trade"/>: its pause. Nothing is counted
    /// until <see cref="Take"/>.
    /// </summary>
    public PortfolioDecision Decide(SymbolInfo s, long volume, double lossPerLot, int legs, string trade, int dir)
    {
        static PortfolioDecision No(string why) => new(null, 0, why);

        if (equity is not { } e || e <= 0)
            return No("account status not available");
        if (Problem is { } problem)
            return No(problem);

        var perLot = legs * lossPerLot;
        var risk = s.LotsForVolume(volume) * perLot;
        var most = e * limits.TradePercent / 100;
        if (risk > most + Tolerance)
        {
            if (s.VolumeNotAbove(most / perLot) is not { } smaller)
                return No($"{Usd(risk)} for this trade is above {Pct(limits.TradePercent)} of equity ({Usd(most)}), and the broker's minimum {N(s.LotsForVolume(s.MinVolume))} lot is still too much");
            volume = Math.Min(volume, smaller);
            risk = s.LotsForVolume(volume) * perLot;
        }

        var item = New(s.Name, dir, risk, trade);
        var newTrade = _items.All(i => i.Trade != trade);
        if (Trades + (newTrade ? 1 : 0) > limits.MaxTrades)
            return No($"{Trades} trades open or pending; at most {limits.MaxTrades}");
        if (PendingOrders + 1 > limits.MaxPendingOrders)
            return No($"{PendingOrders} entry orders waiting; at most {limits.MaxPendingOrders}");
        if (Over("all trades", limits.TotalPercent, _ => 1, item, e) is { } total)
            return No(total);
        if (Over(s.Name, limits.SymbolPercent, i => i.Symbol == s.Name ? 1 : 0, item, e) is { } symbol)
            return No(symbol);
        foreach (var (bucket, _) in RiskGroups.Of(s.Name, dir))
            if (Over($"group {bucket}", limits.GroupPercent, i => BucketWeight(i, bucket), item, e) is { } group)
                return No(group);
        return new PortfolioDecision(volume, risk, null);
    }

    /// <summary>Counts an order placed in this round, so orders of symbols that fire together are judged against each other.</summary>
    public void Take(string symbol, int dir, double risk, string trade)
    {
        _items.Add(New(symbol, dir, risk, trade));
        PendingOrders++;
    }

    private static RiskItem New(string symbol, int dir, double risk, string trade) => new(trade, symbol, dir, risk, true, true, "new order");

    private string? Over(string name, double percent, Func<RiskItem, double> weight, RiskItem item, double e)
    {
        var after = Sum(weight, item);
        var limit = e * percent / 100;
        return after > limit + Tolerance ? $"{name}: {Usd(after)} with this trade, above {Pct(percent)} of equity ({Usd(limit)})" : null;
    }

    /// <summary>The account's exposure for <c>--risk</c> (scout.md §5).</summary>
    public IEnumerable<string> Report()
    {
        var e = equity ?? 0;
        string Line(string name, double usd, double percent) => $"{name},{usd.ToString("0.00", CultureInfo.InvariantCulture)},{(e > 0 ? 100 * usd / e : 0).ToString("0.00", CultureInfo.InvariantCulture)},{percent.ToString("0.##", CultureInfo.InvariantCulture)}";
        yield return $"Equity {Usd(e)}; portfolio limits are {(limits.Enabled ? "ON" : "OFF (shown as they would apply)")}; {Trades} trades open or pending (at most {limits.MaxTrades}); {PendingOrders} entry orders waiting (at most {limits.MaxPendingOrders}).";
        if (Problem is not null)
            yield return $"NEW ORDERS WOULD BE REFUSED: {Problem}";
        yield return "scope,risk_usd,percent_of_equity,limit_percent";
        yield return Line("all trades", Total, limits.TotalPercent);
        foreach (var symbol in _items.Select(i => i.Symbol).Distinct().Order())
            yield return Line(symbol, OnSymbol(symbol), limits.SymbolPercent);
        foreach (var bucket in _items.SelectMany(i => RiskGroups.Of(i.Symbol, i.Dir)).Select(b => b.Bucket).Distinct().Order())
            yield return Line($"group {bucket}", InBucket(bucket), limits.GroupPercent);
        yield return "symbol,side,risk_usd,trade,detail";
        foreach (var i in _items.OrderByDescending(i => i.Usd))
            yield return string.Join(',', i.Symbol, i.Dir > 0 ? "Buy" : "Sell", i.Usd.ToString("0.00", CultureInfo.InvariantCulture), i.Trade, i.What);
    }

    private static string Usd(double v) => "$" + v.ToString("N2", CultureInfo.InvariantCulture);

    private static string Pct(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + " %";

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
