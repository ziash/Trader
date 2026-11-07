using System.Collections.Concurrent;
using Google.Protobuf;
using Serilog;
using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.MarketData;
using Trader.Core.Notifications;
using Trader.Core.OpenApi;

namespace Trader.TM;

/// <summary>
/// Runs TM on one account: every second (sooner after an order event) it reads the account's positions and orders,
/// lets the <see cref="Planner"/> decide, and carries out the actions one by one. Only while this instance is
/// Primary on the account (open-code-pieces.md §6), so two TM copies never act twice. Every order it sends is a row in actions.csv.
/// </summary>
internal sealed class AccountManager(
    TmAccount account,
    TmConfig config,
    BotIdentity identity,
    ILogger log,
    Notifier notify,
    InstanceRegistry registry,
    PriceFeed feed,
    TradeBook book,
    DailyCsvWriter actionsCsv)
{
    public static readonly string[] Header =
    [
        "time_utc", "trade", "symbol", "role", "action", "side", "lots", "price_req", "price_fill", "latency_ms", "result", "detail",
    ];

    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FullReconcile = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(30);     // account status with the full reconcile
    private static readonly TimeSpan StatusLogEvery = TimeSpan.FromMinutes(15);  // an AccountStatus line in events.csv
    private static readonly TimeSpan MarginAlertEvery = TimeSpan.FromHours(1);   // the margin-low pop-up at most this often
    private static readonly TimeSpan RescueGap = TimeSpan.FromSeconds(20);       // between two margin rescues
    private static readonly TimeSpan StatusRetry = TimeSpan.FromMinutes(5);      // after the broker did not answer the status (e.g. a replay broker without it)
    private static readonly TimeSpan FailedAlertEvery = TimeSpan.FromMinutes(10); // the same refusal pops up at most this often
    private const int MaxActionsPerCycle = 20;

    private readonly ILogger _log = log.ForAccount(account.AccountId);
    private readonly ConcurrentDictionary<long, double> _closePrices = new();
    private readonly ConcurrentDictionary<long, long> _orderFills = new(); // pending orderId → the position it opened
    private readonly Dictionary<long, (DateOnly Day, double Distance)> _distances = new();
    private readonly SemaphoreSlim _wake = new(0);
    private volatile CTraderSession? _session;
    private volatile IReadOnlyDictionary<long, SymbolInfo> _symbols = new Dictionary<long, SymbolInfo>();
    private volatile bool _dirty = true;
    private DateTime _lastReconcile = DateTime.MinValue;
    private volatile IReadOnlyList<string> _openTrades = Describe(book, account);
    private readonly Dictionary<string, DateTime> _failedAlerts = [];
    private AccountStatus? _status;
    private readonly SpreadGuard _guard = new(config);
    private readonly HashSet<long> _triggerRefused = [];   // orders / positions whose trigger change the broker refused (tm.md §5.2)
    private bool _statusDue = true;
    private DateTime _statusLogged = DateTime.MinValue, _marginAlerted = DateTime.MinValue, _lastRescue = DateTime.MinValue, _statusFailed = DateTime.MinValue;

    public long AccountId => account.AccountId;

    /// <summary>One line per trade TM is managing, for the news and Friday pop-ups (tm.md §6). Safe to read from any thread.</summary>
    public IReadOnlyList<string> OpenTrades => _openTrades;

    private static List<string> Describe(TradeBook book, TmAccount account) =>
        book.Trades.Where(t => !t.Done)
            .Select(t => $"{account.Broker} {t.Symbol} {(t.Dir > 0 ? "long" : "short")} {t.Name}{(t.Pending ? " (pending order)" : "")}")
            .ToList();

    /// <summary>After every (re)connect, once the symbols are resolved and subscribed.</summary>
    public void SetSession(CTraderSession session, IReadOnlyList<SymbolInfo> symbols)
    {
        _symbols = symbols.ToDictionary(s => s.SymbolId);
        _session = session;
        _dirty = true;
        _wake.Release();
    }

    /// <summary>Every message from the server (receive thread): order events make the next run re-read the account.</summary>
    public void OnMessage(IMessage message)
    {
        if (message is not ProtoOAExecutionEvent e || e.CtidTraderAccountId != account.AccountId)
            return;
        if (e.Deal?.ClosePositionDetail is not null)
            _closePrices[e.Deal.PositionId] = e.Deal.ExecutionPrice;
        if (e is { ExecutionType: ProtoOAExecutionType.OrderFilled, Order.OrderType: ProtoOAOrderType.Stop or ProtoOAOrderType.Limit, Position: not null })
            _orderFills[e.Order.OrderId] = e.Position.PositionId;
        _dirty = true;
        _wake.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            await _wake.WaitAsync(Cycle, ct);
            while (_wake.CurrentCount > 0)
                await _wake.WaitAsync(ct); // one run for a burst of events

            if (_session is not { } session || session.Closed.IsCompleted || registry.RoleOn(account.AccountId) != InstanceRole.Primary)
                continue;
            try
            {
                await RunOnceAsync(session);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
            {
                _log.Event("TmCycleError").Warning("{Broker}: {Message}; retrying", account.Broker, ex.Message);
                _dirty = true;
            }
        }
    }

    private Snapshot? _snapshot;

    private async Task RunOnceAsync(CTraderSession session)
    {
        var now = DateTime.UtcNow;
        var statusDue = (_statusDue || _status is null || now - _status.TimeUtc >= StatusEvery) && now - _statusFailed >= StatusRetry;
        if (_dirty || _snapshot is null || now - _lastReconcile >= FullReconcile || statusDue)
        {
            _dirty = false;
            var reconcile = await session.ReconcileAsync(account.AccountId);
            _snapshot = Snapshot.From(reconcile);
            _lastReconcile = now;
            if (statusDue)
                await ReadStatusAsync(session, reconcile, now);
        }

        var symbols = _symbols;
        UpdateGuards(symbols, now);
        var env = new PlanEnv
        {
            Config = config,
            BotName = identity.BotName,
            Now = now,
            Symbol = id => symbols.GetValueOrDefault(id),
            Quote = id => feed.Last(account.AccountId, id),
            Distance = id => _distances.TryGetValue(id, out var d) && d.Day == DateOnly.FromDateTime(now) ? d.Distance : null,
            ClosePrice = id => _closePrices.TryGetValue(id, out var p) ? p : null,
            FilledPosition = id => _orderFills.TryGetValue(id, out var p) ? p : null,
            Account = _status is { } st && now - st.TimeUtc < 5 * StatusEvery ? st : null,
            RescueAllowed = _status is { } fresh && fresh.TimeUtc > _lastRescue.AddSeconds(5) && now - _lastRescue >= RescueGap,
            Guarded = _guard.IsGuarded,
        };

        var actions = Planner.Plan(book, _snapshot, env);
        if (actions.Any(a => a is Notify { Event: "MarginRescue" }))
            (_lastRescue, _statusDue) = (now, true);
        var changed = false;
        foreach (var action in actions.Take(MaxActionsPerCycle))
            changed |= await ExecuteAsync(session, action, symbols);
        if (changed || actions.Count > MaxActionsPerCycle)
            _dirty = true;
        book.Save();
        _openTrades = Describe(book, account);
    }

    /// <summary>
    /// The spread guard per symbol (tm.md §5.2, issue-log.md #2): a line in events.csv when it starts and ends; a pop-up
    /// when the spread is still wide after the maximum time and TM acts again.
    /// </summary>
    private void UpdateGuards(IReadOnlyDictionary<long, SymbolInfo> symbols, DateTime now)
    {
        foreach (var s in symbols.Values)
        {
            if (_guard.Update(s.SymbolId, feed.Last(account.AccountId, s.SymbolId), s.Schedule, now) is not { } change)
                continue;
            if (change.Kind == "long")
                notify.Warning("SpreadGuardLong", $"{account.Broker} {s.Name}: spread still wide",
                    $"{change.Reason}; TM acts on the price again (tm.md §5.2)", account.AccountId);
            else
                _log.Event("SpreadGuard").Information("{Broker} {Symbol}: spread guard {Kind} — {Reason}{Then}", account.Broker, s.Name, change.Kind,
                    change.Reason, change.Kind == "on" ? "; TM's own reactions to the price wait" : "");
        }
    }

    /// <summary>
    /// Balance, equity and margin (tm.md §5.1, issue-log.md #1): what the planner's margin rules read, a line in events.csv
    /// every 15 minutes, and a pop-up when the margin level is below the warning level. Read-only.
    /// </summary>
    private async Task ReadStatusAsync(CTraderSession session, ProtoOAReconcileRes reconcile, DateTime now)
    {
        try
        {
            _status = await session.GetAccountStatusAsync(account.AccountId, reconcile);
            _statusDue = false;
        }
        catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
        {
            if (now - _statusFailed >= MarginAlertEvery)
                _log.Event("TmNoAccountStatus").Warning("{Broker}: account status not available ({Message}); margin rules wait for it", account.Broker, ex.Message);
            _statusFailed = now;
            return;
        }

        var level = _status.MarginLevel;
        var low = level < config.MarginWarnLevel;
        if (now - _statusLogged >= StatusLogEvery || low && now - _marginAlerted >= MarginAlertEvery)
        {
            _statusLogged = now;
            _log.Event("AccountStatus").Information("{Broker} account {Account}: {Status}; {Positions} positions, {Orders} orders",
                account.Broker, account.AccountId, _status.Describe(), _status.Positions, _status.Orders);
        }
        if (low && now - _marginAlerted >= MarginAlertEvery)
        {
            _marginAlerted = now;
            notify.Send(level < config.MarginRescueLevel ? NotificationLevel.Critical : NotificationLevel.Warning, "MarginLow",
                $"{account.Broker}: margin level {level:0} %",
                $"{_status.Describe()}. Below {config.MarginWarnLevel:0} % TM opens no mirrors; below {config.MarginRescueLevel:0} % it closes trades, the one holding the most margin first.",
                account.AccountId);
        }
    }

    /// <summary>Carries out one action. Returns true when it changed something on the server.</summary>
    private async Task<bool> ExecuteAsync(CTraderSession session, TmAction action, IReadOnlyDictionary<long, SymbolInfo> symbols)
    {
        var ctid = account.AccountId;
        switch (action)
        {
            case Notify n:
                notify.Send(n.Level, n.Event, $"{account.Broker} {n.Title}", n.Message, ctid);
                return false;
            case NeedDistance d:
                await FetchDistanceAsync(session, symbols[d.SymbolId]);
                return false;
        }

        var triggerTarget = action switch { MoveOrder mv => mv.OrderId, SetProtection st => st.PositionId, _ => 0L };
        if (action.TriggerOnly && _triggerRefused.Contains(triggerTarget))
            return false; // refused once: not sent again every second

        var t = action.Trade!;
        var s = symbols[t.SymbolId];
        var sent = DateTime.UtcNow;
        var label = identity.Label(action is PlaceMirrorOrder ? TmLabel.OrderLeg(t.MainOrderId) : TmLabel.Leg(t.MainId, action.Role));
        var comment = CTraderSession.SentComment(sent);
        string kind = action.GetType().Name, side = "", lots = "", requested = "";
        try
        {
            OrderFill? fill = null;
            switch (action)
            {
                case OpenMirror m:
                    var price = m.Dir > 0 ? feed.Last(ctid, s.SymbolId)?.Ask : feed.Last(ctid, s.SymbolId)?.Bid;
                    (side, lots, requested) = (SideText(m.Dir), Lots(s, m.Volume), Num(price, s));
                    fill = await session.PlaceMarketOrderAsync(ctid, s.SymbolId, Snapshot.Side(m.Dir), m.Volume, label, ClientId(),
                        price is { } p ? s.RelativeDistance(Math.Abs(m.StopLoss - p)) : null, OrderTimeout, comment);
                    break;
                case PlaceHedge h when h.Price is null:
                    (side, lots) = (SideText(h.Dir), Lots(s, h.Volume));
                    fill = await session.PlaceMarketOrderAsync(ctid, s.SymbolId, Snapshot.Side(h.Dir), h.Volume, label, ClientId(), null, OrderTimeout, comment);
                    break;
                case PlaceHedge h:
                    (side, lots, requested) = (SideText(h.Dir), Lots(s, h.Volume), Num(h.Price, s));
                    await session.PlaceStopOrderAsync(ctid, s.SymbolId, Snapshot.Side(h.Dir), h.Volume, h.Price!.Value, label, ClientId(), comment, OrderTimeout,
                        config.TriggerMethod);
                    break;
                case PlaceMirrorOrder po:
                    (side, lots, requested) = (SideText(po.Like.Dir), Lots(s, po.Like.Volume), $"{po.Like.Name} {Num(po.Like.Price, s)} {Protection(po.StopLoss, po.TakeProfit, s)}");
                    await session.PlacePendingOrderAsync(ctid, s.SymbolId, po.Like.Type, Snapshot.Side(po.Like.Dir), po.Like.Volume, po.Like.Price,
                        po.StopLoss, po.TakeProfit, label, ClientId(), comment, OrderTimeout);
                    break;
                case AmendMirrorOrder am:
                    (lots, requested) = (Lots(s, am.Like.Volume), $"{am.Like.Name} {Num(am.Like.Price, s)} {Protection(am.StopLoss, am.TakeProfit, s)}");
                    await session.AmendPendingOrderAsync(ctid, am.OrderId, am.Like.Type, am.Like.Volume, am.Like.Price, am.StopLoss, am.TakeProfit, OrderTimeout);
                    break;
                case MoveOrder mo:
                    requested = Num(mo.Price, s);
                    await session.AmendStopOrderAsync(ctid, mo.OrderId, mo.Price, OrderTimeout, config.TriggerMethod);
                    break;
                case CancelOrder c:
                    await session.CancelOrderAsync(ctid, c.OrderId, OrderTimeout);
                    break;
                case SetProtection sp:
                    requested = $"SL {(sp.StopLoss is null ? "none" : Num(sp.StopLoss, s) + (sp.Trailing ? " trailing" : ""))} TP {(sp.TakeProfit is null ? "none" : Num(sp.TakeProfit, s))}";
                    await session.AmendPositionProtectionAsync(ctid, sp.PositionId, sp.StopLoss, sp.TakeProfit, OrderTimeout, trailingStopLoss: sp.Trailing,
                        stopLossTrigger: sp.TmStop ? config.TriggerMethod : null);
                    break;
                case ClosePosition cp:
                    lots = Lots(s, cp.Volume);
                    fill = await session.ClosePositionAsync(ctid, cp.PositionId, cp.Volume, OrderTimeout);
                    break;
            }
            Row(sent, t, s, action.Role, kind, side, lots, requested, fill, "ok", Target(action));
            _log.Event("TmAction").Information("{Broker} {Symbol} P{Main} {Role}: {Action} {Detail} ok{Fill}",
                account.Broker, s.Name, t.MainId, action.Role, kind, Target(action) + (requested.Length > 0 ? " " + requested : ""),
                fill is null ? "" : $", filled {Num(fill.Price, s)} in {fill.Latency.TotalMilliseconds:0} ms");
            action.Done?.Invoke(fill);
        }
        catch (CTraderApiException ex)
        {
            Row(sent, t, s, action.Role, kind, side, lots, requested, null, ex.ErrorCode, $"{Target(action)} {ex.Message}".Trim());
            action.Failed?.Invoke(ex.ErrorCode);
            if (action.TriggerOnly)
                _triggerRefused.Add(triggerTarget);
            if (ex.ErrorCode == Planner.NotEnoughMoney)
                _statusDue = true;
            // Every refusal is a row in actions.csv; the same one pops up (and is an events.csv line) once per 10 minutes.
            var key = $"{t.Name}|{action.Role}|{kind}|{ex.ErrorCode}";
            if (DateTime.UtcNow - _failedAlerts.GetValueOrDefault(key, DateTime.MinValue) >= FailedAlertEvery)
            {
                _failedAlerts[key] = DateTime.UtcNow;
                notify.Warning("TmActionFailed", $"{account.Broker} {s.Name}: {Describe(action)} refused",
                    $"Trade {t.Name}: {ex.ErrorCode} {ex.Message}. TM tries again if the rule still applies"
                    + (ex.ErrorCode == Planner.NotEnoughMoney ? " (after a minute; past the hedge level it closes the leg instead)." : "."), ctid);
            }
        }
        return true;
    }

    /// <summary>Default hedge distance (tm.md §3): a share of the average daily range of the last N days, at least a multiple of the spread.</summary>
    private async Task FetchDistanceAsync(CTraderSession session, SymbolInfo s)
    {
        var now = DateTime.UtcNow;
        double? atr = null;
        try
        {
            var candles = (await session.GetTrendbarsAsync(account.AccountId, s.SymbolId, ProtoOATrendbarPeriod.D1, now.AddDays(-14), now))
                .Where(c => c.OpenUtc.AddDays(1) <= now).ToList(); // completed days only
            atr = Candle.AverageTrueRange(candles, config.RangeDays);
        }
        catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
        {
            _log.Event("TmNoHistory").Warning("{Broker} {Symbol}: daily candles not available ({Message})", account.Broker, s.Name, ex.Message);
        }

        var spread = feed.Last(account.AccountId, s.SymbolId)?.Spread ?? 0;
        double distance;
        string how;
        if (atr is { } a && a > 0)
        {
            distance = Math.Max(a * config.RangePercent / 100, spread * config.SpreadFloor);
            how = $"{config.RangePercent:0.#} % of the {config.RangeDays}-day average range {Planner.P(a, s)}";
        }
        else if (config.Fallback(s.Name) is { } f)
        {
            distance = f;
            how = "fallback from tm.json";
            notify.Warning("TmFallbackDistance", $"{account.Broker} {s.Name}: default hedge distance from tm.json", "Daily candles were not available", account.AccountId);
        }
        else
        {
            distance = (feed.Last(account.AccountId, s.SymbolId)?.Mid ?? 0) * 0.003;
            how = "0.3 % of the price (no candles, no fallback in tm.json)";
            notify.Warning("TmFallbackDistance", $"{account.Broker} {s.Name}: default hedge distance 0.3 % of price", "No candles and no fallback in tm.json", account.AccountId);
        }
        distance = Math.Max(Math.Round(distance, s.Digits), s.PipSize);
        _distances[s.SymbolId] = (DateOnly.FromDateTime(now), distance);
        _log.Event("TmDistance").Information("{Broker} {Symbol}: default hedge distance {Distance} ({How}; spread {Spread})",
            account.Broker, s.Name, Planner.P(distance, s), how, Planner.P(spread, s));
    }

    private void Row(DateTime sent, TradeState t, SymbolInfo s, string role, string kind, string side, string lots, string requested,
        OrderFill? fill, string result, string detail) =>
        actionsCsv.Write(sent,
        [
            CsvFormat.Time(sent), t.Name, s.Name, role, kind, side, lots, requested, Num(fill?.Price, s),
            CsvFormat.Num(fill?.Latency.TotalMilliseconds, 0), result, detail,
        ]);

    private static string Target(TmAction a) => a switch
    {
        MoveOrder m => $"order {m.OrderId}",
        AmendMirrorOrder m => $"order {m.OrderId}",
        CancelOrder c => $"order {c.OrderId}",
        SetProtection p => $"position {p.PositionId}",
        ClosePosition c => $"position {c.PositionId}",
        _ => "",
    };

    private static string Describe(TmAction a) => a switch
    {
        OpenMirror => "opening the mirror",
        PlaceMirrorOrder => "placing the mirror order",
        AmendMirrorOrder => "moving the mirror order",
        PlaceHedge => "placing a hedge",
        MoveOrder => "moving a hedge",
        CancelOrder => "cancelling a hedge",
        SetProtection => "setting stop-loss / take-profit",
        ClosePosition => "closing a position",
        _ => a.GetType().Name,
    };

    private static string SideText(int dir) => dir > 0 ? "buy" : "sell";
    private static string Protection(double? sl, double? tp, SymbolInfo s) => $"SL {(sl is null ? "none" : Num(sl, s))} TP {(tp is null ? "none" : Num(tp, s))}";
    private static string Lots(SymbolInfo s, long volume) => CsvFormat.Num(s.LotsForVolume(volume), 2);
    private static string Num(double? v, SymbolInfo s) => CsvFormat.Num(v, s.Digits);
    private static string ClientId() => "tm-" + Guid.NewGuid().ToString("N")[..12];
}
