using Trader.Core.MarketData;
using Trader.Core.OpenApi;
using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>
/// A tiny broker for one symbol: positions, stop orders, stop-losses and take-profits, filled at the price that
/// triggers them. Runs TM's planner like the real loop: plan, carry out the actions, repeat until nothing changes.
/// </summary>
internal sealed class FakeBroker
{
    public const long SymbolId = 1;
    public static readonly SymbolInfo EurUsd = new(SymbolId, "EURUSD", "EURUSD", "EUR", "USD", 5, 4, 100_000, true, ProtoOATradingMode.Enabled,
        TradingSchedule.AlwaysOpen, 100_000, 100_000);

    private long _nextId = 1000;
    public List<Pos> Positions { get; } = [];
    public List<Ord> Orders { get; } = [];
    public Dictionary<long, double> ClosePrices { get; } = [];
    public List<TmAction> Log { get; } = [];
    public TradeBook Book { get; } = new("");
    public TmConfig Config { get; init; } = new([new TmAccount("brokerA", 1, ["EURUSD"])]);
    public DateTime Now { get; set; } = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    public double Bid { get; private set; } = 1.16000;
    public double Ask => Math.Round(Bid + Spread, 5);
    public double Spread { get; set; } = 0.00002;
    public double? Distance { get; set; } = 0.0010;
    public HashSet<string> Refuse { get; } = [];

    private static string Label(long mainId, string role) => $"TM-abcdef-{TmLabel.Leg(mainId, role)}";

    public Pos OpenManual(int dir, long volume = 50_000_000, double? stopLoss = null, double? takeProfit = null, DateTime? openUtc = null, bool trailing = false)
    {
        var p = new Pos(_nextId++, SymbolId, dir, volume, dir > 0 ? Ask : Bid, stopLoss, takeProfit, "", openUtc ?? Now, trailing);
        Positions.Add(p);
        return p;
    }

    public void Close(long positionId)
    {
        var p = Positions.Single(x => x.Id == positionId);
        Positions.Remove(p);
        ClosePrices[p.Id] = p.Dir > 0 ? Bid : Ask;
    }

    public void SetStopLoss(long positionId, double? sl)
    {
        var i = Positions.FindIndex(x => x.Id == positionId);
        Positions[i] = Positions[i] with { StopLoss = sl };
    }

    /// <summary>The owner switches a position's trailing stop on or off in cTrader.</summary>
    public void SetTrailing(long positionId, bool trailing)
    {
        var i = Positions.FindIndex(x => x.Id == positionId);
        Positions[i] = Positions[i] with { Trailing = trailing };
    }

    /// <summary>The owner places a pending stop order of their own (no label).</summary>
    public Ord PlaceManualStop(int dir, double price, long volume = 50_000_000)
    {
        var o = new Ord(_nextId++, SymbolId, dir, volume, price, "");
        Orders.Add(o);
        return o;
    }

    public void SetTakeProfit(long positionId, double? tp)
    {
        var i = Positions.FindIndex(x => x.Id == positionId);
        Positions[i] = Positions[i] with { TakeProfit = tp };
    }

    public void DragOrder(string role, double price)
    {
        var i = Orders.FindIndex(o => o.Label.EndsWith("-" + role));
        Orders[i] = Orders[i] with { StopPrice = price };
    }

    public Pos? Find(string role) => Positions.SingleOrDefault(p => p.Label.EndsWith("-" + role));
    public Ord? Order(string role) => Orders.SingleOrDefault(o => o.Label.EndsWith("-" + role));

    /// <summary>Moves the price (bid) and lets stop orders, stop-losses and take-profits trigger, then runs TM.</summary>
    public List<TmAction> MoveTo(double bid)
    {
        Bid = bid;
        Trigger();
        return Run();
    }

    private void Trigger()
    {
        foreach (var o in Orders.ToList())
        {
            if (o.Dir > 0 ? Ask >= o.StopPrice : Bid <= o.StopPrice)
            {
                Orders.Remove(o);
                Positions.Add(new Pos(_nextId++, SymbolId, o.Dir, o.Volume, o.Dir > 0 ? Ask : Bid, null, null, o.Label, Now));
            }
        }
        foreach (var p in Positions.ToList())
        {
            var exit = p.Dir > 0 ? Bid : Ask;
            var hitSl = p.StopLoss is { } sl && (p.Dir > 0 ? exit <= sl : exit >= sl);
            var hitTp = p.TakeProfit is { } tp && (p.Dir > 0 ? exit >= tp : exit <= tp);
            if (hitSl || hitTp)
                Close(p.Id);
        }
    }

    /// <summary>Runs the planner until it has nothing more to do (at most 10 rounds). Returns every action.</summary>
    public List<TmAction> Run()
    {
        var all = new List<TmAction>();
        for (var round = 0; round < 10; round++)
        {
            var env = new PlanEnv
            {
                Config = Config,
                BotName = "TM",
                Now = Now,
                Symbol = id => id == SymbolId ? EurUsd : null,
                Quote = _ => new Quote(1, SymbolId, Bid, Ask, Now),
                Distance = _ => Distance,
                ClosePrice = id => ClosePrices.TryGetValue(id, out var p) ? p : null,
            };
            var actions = Planner.Plan(Book, new Snapshot(Positions.ToList(), Orders.ToList()), env);
            all.AddRange(actions);
            Log.AddRange(actions);
            var changed = false;
            foreach (var a in actions)
                changed |= Apply(a);
            if (!changed)
                break;
        }
        return all;
    }

    private bool Apply(TmAction a)
    {
        if (a is Notify or NeedDistance)
            return false;
        if (Refuse.Contains(a.GetType().Name))
        {
            a.Failed?.Invoke("REFUSED");
            return true;
        }
        OrderFill? fill = null;
        switch (a)
        {
            case OpenMirror m:
                var price = m.Dir > 0 ? Ask : Bid;
                var mirror = new Pos(_nextId++, SymbolId, m.Dir, m.Volume, price, m.StopLoss, null, Label(m.Trade!.MainId, TmLabel.Mirror), Now);
                Positions.Add(mirror);
                fill = new OrderFill(mirror.Id, mirror.Id, mirror.Id, price, m.Volume, Now, 0, mirror.Label, TimeSpan.Zero);
                break;
            case PlaceHedge h when h.Price is null:
                var hp = new Pos(_nextId++, SymbolId, h.Dir, h.Volume, h.Dir > 0 ? Ask : Bid, null, null, Label(h.Trade!.MainId, h.Role), Now);
                Positions.Add(hp);
                fill = new OrderFill(hp.Id, hp.Id, hp.Id, hp.Entry, h.Volume, Now, 0, hp.Label, TimeSpan.Zero);
                break;
            case PlaceHedge h:
                Orders.Add(new Ord(_nextId++, SymbolId, h.Dir, h.Volume, h.Price!.Value, Label(h.Trade!.MainId, h.Role)));
                break;
            case MoveOrder mo:
                var i = Orders.FindIndex(o => o.Id == mo.OrderId);
                Orders[i] = Orders[i] with { StopPrice = mo.Price };
                break;
            case CancelOrder c:
                Orders.RemoveAll(o => o.Id == c.OrderId);
                break;
            case SetProtection sp:
                var j = Positions.FindIndex(p => p.Id == sp.PositionId);
                Positions[j] = Positions[j] with { StopLoss = sp.StopLoss, TakeProfit = sp.TakeProfit, Trailing = sp.Trailing };
                break;
            case ClosePosition cp:
                Close(cp.PositionId);
                break;
        }
        a.Done?.Invoke(fill);
        return true;
    }
}
