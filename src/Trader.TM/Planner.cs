using System.Globalization;
using Trader.Core.MarketData;
using Trader.Core.Notifications;

namespace Trader.TM;

/// <summary>What the planner needs besides the trades and the snapshot.</summary>
internal sealed class PlanEnv
{
    public required TmConfig Config { get; init; }
    public required string BotName { get; init; }
    public required DateTime Now { get; init; }

    /// <summary>Details of a listed symbol; null for symbols TM does not manage.</summary>
    public required Func<long, SymbolInfo?> Symbol { get; init; }

    public required Func<long, Quote?> Quote { get; init; }

    /// <summary>Default hedge distance (price) for a symbol; null = not known yet (TM fetches the candles).</summary>
    public required Func<long, double?> Distance { get; init; }

    /// <summary>Price a position was closed at, from the execution events; null if not seen.</summary>
    public required Func<long, double?> ClosePrice { get; init; }
}

/// <summary>
/// TM's rules (plan 10.9.3–10.9.5) as one function: from the account's positions and orders, the prices and the
/// remembered trades, work out what should be changed on the server. It runs every second; anything that did not
/// work is simply planned again next time, so it is safe after missed messages, reconnects and restarts. State
/// that only TM knows (fills seen, steps done) is updated here or by an action's <see cref="TmAction.Done"/>.
/// </summary>
internal static class Planner
{
    public static List<TmAction> Plan(TradeBook book, Snapshot snap, PlanEnv env)
    {
        var actions = new List<TmAction>();
        var tmPos = new Dictionary<(long, string), Pos>();
        var tmOrd = new Dictionary<(long, string), Ord>();
        foreach (var p in snap.Positions)
            if (TmLabel.TryParse(p.Label, env.BotName, out var id, out var role))
                tmPos[(id, role)] = p;
        foreach (var o in snap.Orders)
            if (TmLabel.TryParse(o.Label, env.BotName, out var id, out var role))
                tmOrd[(id, role)] = o;
        var mains = snap.Positions.Where(p => p.Label.Length == 0 && env.Symbol(p.SymbolId) is not null).ToDictionary(p => p.Id);

        // TM orders without a remembered trade first, so their main is not taken over a second time.
        foreach (var mainId in tmPos.Keys.Concat(tmOrd.Keys).Select(k => k.Item1).Distinct().Where(id => book.Find(id) is null).ToList())
            Rebuild(book, mainId, mains.GetValueOrDefault(mainId), tmPos, tmOrd, env, actions);

        foreach (var main in mains.Values.Where(m => book.Find(m.Id) is null))
            TakeOver(book, main, env, actions);

        foreach (var trade in book.Trades.ToList())
            new TradePlan(trade, mains.GetValueOrDefault(trade.MainId), tmPos, tmOrd, env, actions).Run();

        foreach (var done in book.Trades.Where(t => t.Done).ToList())
        {
            book.Trades.Remove(done);
            actions.Add(new Notify(done, "", NotificationLevel.Info, "TradeDone", $"{done.Symbol} trade P{done.MainId} finished",
                $"All legs and hedges closed; hedges result {Fmt(done.Main.Realized + (done.Mirror?.Realized ?? 0))} per unit"));
        }
        return actions;
    }

    private static void TakeOver(TradeBook book, Pos main, PlanEnv env, List<TmAction> actions)
    {
        var s = env.Symbol(main.SymbolId)!;
        if (env.Quote(main.SymbolId) is null)
            return; // no price yet; next time

        double h;
        string source;
        if (main.StopLoss is { } sl && main.Dir * (main.Entry - sl) > 0)
        {
            h = sl;
            source = "your stop-loss";
        }
        else if (env.Distance(main.SymbolId) is { } d)
        {
            h = Math.Round(main.Entry - main.Dir * d, s.Digits);
            source = $"{env.Config.RangePercent:0.#} % of the {env.Config.RangeDays}-day range";
        }
        else
        {
            actions.Add(new NeedDistance(main.SymbolId));
            return;
        }

        var t = new TradeState
        {
            MainId = main.Id,
            SymbolId = main.SymbolId,
            Symbol = s.Name,
            Dir = main.Dir,
            H = h,
            StartedUtc = env.Now,
            NoMirror = env.Now - main.OpenUtc > TimeSpan.FromSeconds(env.Config.MirrorWithinSeconds),
            Main = new LegState { PositionId = main.Id, Entry = main.Entry, Volume = main.Volume, R = Math.Abs(h - main.Entry) },
        };
        book.Trades.Add(t);
        var what = $"{s.Name} {(main.Dir > 0 ? "long" : "short")} {s.LotsForVolume(main.Volume):0.##} lot";
        TightHedge(t, s, env, actions);
        actions.Add(t.NoMirror
            ? new Notify(t, "MAIN", NotificationLevel.Warning, "MirrorSkipped", $"{what} found late: hedges only",
                $"Position {main.Id} opened {main.OpenUtc:HH:mm:ss}Z, before TM saw it. No mirror; hedge at {P(h, s)} ({source})")
            : new Notify(t, "MAIN", NotificationLevel.Info, "TradeTakenOver", $"{what} taken over",
                $"Position {main.Id}: mirror + 2 hedges at {P(h, s)} ({source}); 1R = {P(t.Main.R, s)}"));
    }

    /// <summary>A hedge level within a few spreads of the entry: every hedge cycle then costs about 1R in spread and slippage.</summary>
    private static void TightHedge(TradeState t, SymbolInfo s, PlanEnv env, List<TmAction> actions)
    {
        if (env.Quote(t.SymbolId) is not { } q || q.Spread <= 0 || t.Main.R >= env.Config.SpreadFloor * q.Spread)
            return;
        actions.Add(new Notify(t, "MAIN", NotificationLevel.Warning, "TightHedge", $"{t.Symbol}: hedge only {t.Main.R / q.Spread:0.#} spreads away",
            $"1R = {P(t.Main.R, s)}, spread {P(q.Spread, s)}. Each hedge cycle will cost about 1R; a level at least {env.Config.SpreadFloor:0.#} spreads away is safer."));
    }

    /// <summary>TM orders without a remembered trade (state file lost or from another TM instance): rebuild what can be seen.</summary>
    private static void Rebuild(TradeBook book, long mainId, Pos? main, Dictionary<(long, string), Pos> tmPos, Dictionary<(long, string), Ord> tmOrd,
        PlanEnv env, List<TmAction> actions)
    {
        var mirror = tmPos.GetValueOrDefault((mainId, TmLabel.Mirror));
        var hedgeOrders = new[] { TmLabel.HedgeMain, TmLabel.HedgeMirror }.Select(r => tmOrd.GetValueOrDefault((mainId, r))).OfType<Ord>().ToList();
        var hedgePositions = new[] { TmLabel.HedgeMain, TmLabel.HedgeMirror }.Select(r => tmPos.GetValueOrDefault((mainId, r))).OfType<Pos>().ToList();
        var symbolId = main?.SymbolId ?? mirror?.SymbolId ?? hedgeOrders.Concat<object>(hedgePositions).Select(x => x is Ord o ? o.SymbolId : ((Pos)x).SymbolId).First();
        var dir = main?.Dir ?? mirror?.Dir ?? -(hedgeOrders.Select(o => o.Dir).Concat(hedgePositions.Select(p => p.Dir)).First());
        double? h = hedgeOrders.Select(o => (double?)o.StopPrice).FirstOrDefault() ?? hedgePositions.Select(p => (double?)p.Entry).FirstOrDefault() ?? mirror?.StopLoss;
        var s = env.Symbol(symbolId);
        if (h is null || s is null)
            return;

        LegState Leg(Pos? p, string hedgeRole)
        {
            var hedge = tmPos.GetValueOrDefault((mainId, hedgeRole));
            return new LegState
            {
                PositionId = p?.Id ?? 0,
                Entry = p?.Entry ?? h.Value,
                Volume = p?.Volume ?? 0,
                R = p is null ? 1 : Math.Max(Math.Abs(h.Value - p.Entry), s.PipSize),
                Protected = p?.StopLoss is { } sl && dir * (sl - p.Entry) >= 0,
                StopCleared = true,
                HedgePositionId = hedge?.Id,
                HedgeFill = hedge?.Entry ?? 0,
                Armed = hedge?.StopLoss is { } hsl && -dir * (hsl - hedge.Entry) > 0,
                EverHedged = hedge is not null,
            };
        }

        var t = new TradeState
        {
            MainId = mainId, SymbolId = symbolId, Symbol = s.Name, Dir = dir, H = h.Value, StartedUtc = env.Now,
            NoMirror = mirror is null, MirrorRequested = mirror is not null,
            Main = Leg(main, TmLabel.HedgeMain),
            Mirror = mirror is null && !tmPos.ContainsKey((mainId, TmLabel.HedgeMirror)) && !tmOrd.ContainsKey((mainId, TmLabel.HedgeMirror))
                ? null : Leg(mirror, TmLabel.HedgeMirror),
        };
        book.Trades.Add(t);
        actions.Add(new Notify(t, "", NotificationLevel.Warning, "TradeRebuilt", $"{s.Name} trade P{mainId} picked up from its orders",
            $"No saved state; hedge level {P(h.Value, s)} taken from the server. Earlier hedge results are not known."));
    }

    internal static string P(double price, SymbolInfo s) => price.ToString("F" + s.Digits, CultureInfo.InvariantCulture);

    private static string Fmt(double v) => v.ToString("+0.#####;-0.#####;0", CultureInfo.InvariantCulture);

    /// <summary>Plans one trade. One protection change per position per run, so two rules never fight over it.</summary>
    private sealed class TradePlan(TradeState t, Pos? mainPos, Dictionary<(long, string), Pos> tmPos, Dictionary<(long, string), Ord> tmOrd,
        PlanEnv env, List<TmAction> actions)
    {
        private readonly HashSet<long> _touched = [];
        private SymbolInfo _s = null!;
        private Quote _q;

        private int HedgeDir => -t.Dir;

        public void Run()
        {
            if (env.Symbol(t.SymbolId) is not { } s || env.Quote(t.SymbolId) is not { } q)
                return;
            _s = s;
            _q = q;

            var mirPos = tmPos.GetValueOrDefault((t.MainId, TmLabel.Mirror));
            if (!t.NoMirror && t.Mirror is null)
            {
                if (mirPos is not null)
                    t.Mirror = new LegState { PositionId = mirPos.Id, Entry = mirPos.Entry, Volume = mirPos.Volume, R = Math.Abs(t.H - mirPos.Entry) };
                else if (!t.MirrorRequested && mainPos is not null)
                {
                    t.MirrorRequested = true;
                    actions.Add(new OpenMirror(t, t.Dir, mainPos.Volume, t.H)
                    {
                        Done = fill => t.Mirror = new LegState
                        {
                            PositionId = fill!.PositionId, Entry = fill.Price, Volume = fill.FilledVolume, R = Math.Abs(t.H - fill.Price),
                        },
                        Failed = _ => t.NoMirror = true,
                    }); // the mirror's own hedge follows next run, once its entry is known
                }
            }

            HedgeLevelChange();
            Leg(t.Main, mainPos, "MAIN", TmLabel.HedgeMain);
            if (t.Mirror is not null)
                Leg(t.Mirror, mirPos, TmLabel.Mirror, TmLabel.HedgeMirror);
        }

        /// <summary>The owner moved the hedge level: a new stop-loss on the main, or a dragged hedge order (10.9.4).</summary>
        private void HedgeLevelChange()
        {
            var hedgeOpen = new[] { TmLabel.HedgeMain, TmLabel.HedgeMirror }.Any(r => tmPos.ContainsKey((t.MainId, r)));
            double? newH = null;
            string how = "";
            if (mainPos?.StopLoss is { } sl && t.Main.StopCleared && !t.Main.Protected)
            {
                newH = sl;
                how = "new stop-loss on the main";
            }
            else
            {
                var dragged = t.Legs().Where(l => !l.Leg.Done && !l.Leg.Protected && !l.Leg.Raised)
                    .Select(l => tmOrd.GetValueOrDefault((t.MainId, l.HedgeRole))).OfType<Ord>()
                    .FirstOrDefault(o => !Same(o.StopPrice, t.H));
                if (dragged is not null)
                {
                    newH = dragged.StopPrice;
                    how = "hedge order moved";
                }
            }
            if (newH is not { } h || Same(h, t.H))
                return;

            if (hedgeOpen || t.Dir * (t.Main.Entry - h) <= 0 || !ValidStop(HedgeDir, h))
            {
                actions.Add(new Notify(t, "MAIN", NotificationLevel.Warning, "HedgeMoveIgnored", $"{t.Symbol}: hedge level not moved",
                    $"{how} at {P(h, _s)} ignored ({(hedgeOpen ? "a hedge is filled" : "not on the protective side of the entry")}); hedges stay at {P(t.H, _s)}"));
                return;
            }
            t.H = Math.Round(h, _s.Digits);
            foreach (var (leg, _, _) in t.Legs())
                leg.R = Math.Abs(t.H - leg.Entry);
            actions.Add(new Notify(t, "MAIN", NotificationLevel.Info, "HedgeMoved", $"{t.Symbol}: hedges moved to {P(t.H, _s)}",
                $"{how}; 1R is now {P(t.Main.R, _s)}"));
            TightHedge(t, _s, env, actions);
        }

        private void Leg(LegState leg, Pos? pos, string role, string hedgeRole)
        {
            if (leg.Done)
                return;
            var hOrd = tmOrd.GetValueOrDefault((t.MainId, hedgeRole));
            var hPos = tmPos.GetValueOrDefault((t.MainId, hedgeRole));
            var name = role == "MAIN" ? "main" : "mirror";

            if (pos is null || pos.Id != leg.PositionId && leg.PositionId != 0)
            {
                // The leg is closed (by the owner, its stop-loss / take-profit, or the 3R rule): remove its hedge.
                if (hOrd is not null)
                    actions.Add(new CancelOrder(t, hedgeRole, hOrd.Id));
                if (hPos is not null)
                    actions.Add(new ClosePosition(t, hedgeRole, hPos.Id, hPos.Volume));
                if (hOrd is null && hPos is null)
                {
                    leg.Done = true;
                    actions.Add(new Notify(t, role, NotificationLevel.Info, "LegClosed", $"{t.Symbol} {name} closed", "Its hedge is removed; the other leg carries on"));
                }
                return;
            }

            leg.Entry = pos.Entry;
            leg.Volume = pos.Volume;
            var hd = HedgeDir;
            var legExit = t.Dir > 0 ? _q.Bid : _q.Ask;   // price the leg would close at
            var hedgeExit = hd > 0 ? _q.Bid : _q.Ask;    // price the hedge would close at
            var hedgeEntry = hd > 0 ? _q.Ask : _q.Bid;   // price a new hedge would open at
            var profit = t.Dir * (legExit - leg.Entry);
            var tp = role == "MAIN" ? pos.TakeProfit : mainPos?.TakeProfit ?? pos.TakeProfit;

            // What happened to the hedge since last time.
            if (hPos is not null && leg.HedgePositionId != hPos.Id)
            {
                leg.HedgePositionId = hPos.Id;
                leg.HedgeFill = hPos.Entry;
                leg.Armed = false;
                leg.EverHedged = true;
                actions.Add(new Notify(t, hedgeRole, NotificationLevel.Info, "HedgeFilled", $"{t.Symbol} {name} hedged at {P(hPos.Entry, _s)}",
                    $"Loss frozen; hedge stop {P(hPos.Entry - hd * env.Config.HedgeStopR * leg.R, _s)}, armed at {P(hPos.Entry + hd * env.Config.ArmR * leg.R, _s)}"));
            }
            else if (hPos is null && leg.HedgePositionId is { } closedId)
            {
                var close = env.ClosePrice(closedId) ?? hedgeExit;
                var gain = hd * (close - leg.HedgeFill);
                leg.Realized += gain;
                leg.Raised = leg.Armed; // closed at fill + 1 pip: re-hedge 1R beyond the price (10.9.4)
                leg.Armed = false;
                leg.HedgePositionId = null;
                actions.Add(new Notify(t, hedgeRole, NotificationLevel.Info, "HedgeClosed", $"{t.Symbol} {name} hedge closed at {P(close, _s)}",
                    $"Hedge result {gain / leg.R:+0.00;-0.00}R; re-hedging"));
            }

            if (leg.Protected)
            {
                if (hOrd is not null)
                    actions.Add(new CancelOrder(t, hedgeRole, hOrd.Id));
                if (hPos is not null)
                    actions.Add(new ClosePosition(t, hedgeRole, hPos.Id, hPos.Volume));
                var keep = pos.StopLoss is { } sl && t.Dir * (sl - leg.Entry) >= -_s.PipSize * 0.05 ? sl : leg.Entry;
                Protect(pos, role, Math.Round(keep, _s.Digits), tp);
                return;
            }

            // Loss limits (10.9.5): the leg, its open hedge and its earlier hedges together, in R.
            var result = profit + (hPos is not null ? hd * (hedgeExit - hPos.Entry) : 0) + leg.Realized;
            var lossR = leg.R > 0 ? -result / leg.R : 0;
            if (AtLeast(lossR * leg.R, env.Config.CloseR * leg.R))
            {
                actions.Add(new ClosePosition(t, role, pos.Id, pos.Volume));
                if (hPos is not null)
                    actions.Add(new ClosePosition(t, hedgeRole, hPos.Id, hPos.Volume));
                if (hOrd is not null)
                    actions.Add(new CancelOrder(t, hedgeRole, hOrd.Id));
                actions.Add(new Notify(t, role, NotificationLevel.Critical, "LossLimit", $"{t.Symbol} {name} closed at -{lossR:0.0}R",
                    $"Loss limit {env.Config.CloseR:0.#}R reached; leg and hedge closed"));
                return;
            }
            if (AtLeast(lossR * leg.R, env.Config.WarnR * leg.R) && !leg.Warned)
            {
                leg.Warned = true;
                actions.Add(new Notify(t, role, NotificationLevel.Warning, "LossWarning", $"{t.Symbol} {name} at -{lossR:0.0}R",
                    $"TM closes it at -{env.Config.CloseR:0.#}R"));
            }

            // Break-even at 1R (R2): stop-loss first, the hedge is removed next time.
            if (AtLeast(profit, leg.R) && hPos is null)
            {
                if (Protect(pos, role, Math.Round(leg.Entry, _s.Digits), tp, () => leg.Protected = true))
                    actions.Add(new Notify(t, role, NotificationLevel.Info, "BreakEven", $"{t.Symbol} {name} at +1R: stop at break-even",
                        $"Stop-loss {P(leg.Entry, _s)}; hedge removed"));
                return;
            }

            // The hedge itself (10.9.4).
            if (hPos is not null)
            {
                if (hOrd is not null)
                    actions.Add(new CancelOrder(t, hedgeRole, hOrd.Id));
                var hedgeProfit = hd * (hedgeExit - hPos.Entry);
                if (!leg.Armed && AtLeast(hedgeProfit, env.Config.ArmR * leg.R))
                {
                    if (Protect(hPos, hedgeRole, Math.Round(hPos.Entry + hd * _s.PipSize, _s.Digits), null, () => leg.Armed = true))
                        actions.Add(new Notify(t, hedgeRole, NotificationLevel.Info, "HedgeArmed", $"{t.Symbol} {name} hedge exit armed",
                            $"Hedge closes at {P(hPos.Entry + hd * _s.PipSize, _s)} if the move reverses"));
                }
                else
                {
                    var stop = Math.Round(leg.Armed ? hPos.Entry + hd * _s.PipSize : hPos.Entry - hd * env.Config.HedgeStopR * leg.R, _s.Digits);
                    // In a fast market the price can pass the hedge's stop before the stop is set; the broker then
                    // refuses it. The rule says the hedge closes there, so close it at market.
                    if (hd > 0 ? stop > hedgeExit : stop < hedgeExit)
                        actions.Add(new ClosePosition(t, hedgeRole, hPos.Id, hPos.Volume));
                    else
                        Protect(hPos, hedgeRole, stop, null);
                }
            }
            else if (hOrd is not null)
            {
                if (leg.Raised)
                {
                    if (ValidStop(hd, t.H))
                        actions.Add(new MoveOrder(t, hedgeRole, hOrd.Id, t.H) { Done = _ => leg.Raised = false });
                }
                else if (!Same(hOrd.StopPrice, t.H))
                    actions.Add(new MoveOrder(t, hedgeRole, hOrd.Id, t.H));
            }
            else if (ValidStop(hd, t.H))
                actions.Add(new PlaceHedge(t, hedgeRole, hd, pos.Volume, t.H) { Done = _ => leg.Raised = false });
            else if (leg.EverHedged)
                actions.Add(new PlaceHedge(t, hedgeRole, hd, pos.Volume, Math.Round(hedgeEntry + hd * leg.R, _s.Digits)) { Done = _ => leg.Raised = true });
            else
            {
                actions.Add(new PlaceHedge(t, hedgeRole, hd, pos.Volume, null));
                actions.Add(new Notify(t, hedgeRole, NotificationLevel.Warning, "HedgeAtMarket", $"{t.Symbol} {name} hedged at market",
                    $"Price was already beyond the hedge level {P(t.H, _s)}"));
            }

            // The leg's own stop-loss goes once it is hedged (10.9.3); TM keeps the mirror's take-profit equal to the main's.
            var hedged = hOrd is not null || hPos is not null;
            if (pos.StopLoss is not null && (leg.StopCleared || hedged))
                Protect(pos, role, null, tp, () => leg.StopCleared = true);
            else
            {
                if (hedged)
                    leg.StopCleared = true;
                Protect(pos, role, pos.StopLoss, tp);
            }
        }

        /// <summary>Plans a stop-loss / take-profit change if the position differs; false if nothing to do or already planned.</summary>
        private bool Protect(Pos p, string role, double? stopLoss, double? takeProfit, Action? done = null)
        {
            if (Same(p.StopLoss, stopLoss) && Same(p.TakeProfit, takeProfit))
            {
                done?.Invoke();
                return false;
            }
            if (!_touched.Add(p.Id))
                return false;
            actions.Add(new SetProtection(t, role, p.Id, stopLoss, takeProfit) { Done = done is null ? null : _ => done() });
            return true;
        }

        /// <summary>a ≥ b, allowing for floating-point rounding (1.16200 − 1.16100 is 0.000999…).</summary>
        private bool AtLeast(double a, double b) => a >= b - _s.PipSize * 0.01;

        private bool Same(double? a, double? b) =>
            a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < _s.PipSize * 0.05;

        /// <summary>A buy-stop must sit above the ask, a sell-stop below the bid.</summary>
        private bool ValidStop(int dir, double price) =>
            dir > 0 ? price > _q.Ask + _s.PipSize * 0.5 : price < _q.Bid - _s.PipSize * 0.5;
    }
}
