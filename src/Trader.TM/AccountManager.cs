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
    private const int MaxActionsPerCycle = 20;

    private readonly ILogger _log = log.ForAccount(account.AccountId);
    private readonly ConcurrentDictionary<long, double> _closePrices = new();
    private readonly Dictionary<long, (DateOnly Day, double Distance)> _distances = new();
    private readonly SemaphoreSlim _wake = new(0);
    private volatile CTraderSession? _session;
    private volatile IReadOnlyDictionary<long, SymbolInfo> _symbols = new Dictionary<long, SymbolInfo>();
    private volatile bool _dirty = true;
    private DateTime _lastReconcile = DateTime.MinValue;
    private volatile IReadOnlyList<string> _openTrades = Describe(book, account);

    public long AccountId => account.AccountId;

    /// <summary>One line per trade TM is managing, for the news and Friday pop-ups (tm.md §6). Safe to read from any thread.</summary>
    public IReadOnlyList<string> OpenTrades => _openTrades;

    private static List<string> Describe(TradeBook book, TmAccount account) =>
        book.Trades.Where(t => !t.Done)
            .Select(t => $"{account.Broker} {t.Symbol} {(t.Dir > 0 ? "long" : "short")} P{t.MainId}")
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
        if (_dirty || _snapshot is null || now - _lastReconcile >= FullReconcile)
        {
            _dirty = false;
            _snapshot = Snapshot.From(await session.ReconcileAsync(account.AccountId));
            _lastReconcile = now;
        }

        var symbols = _symbols;
        var env = new PlanEnv
        {
            Config = config,
            BotName = identity.BotName,
            Now = now,
            Symbol = id => symbols.GetValueOrDefault(id),
            Quote = id => feed.Last(account.AccountId, id),
            Distance = id => _distances.TryGetValue(id, out var d) && d.Day == DateOnly.FromDateTime(now) ? d.Distance : null,
            ClosePrice = id => _closePrices.TryGetValue(id, out var p) ? p : null,
        };

        var actions = Planner.Plan(book, _snapshot, env);
        var changed = false;
        foreach (var action in actions.Take(MaxActionsPerCycle))
            changed |= await ExecuteAsync(session, action, symbols);
        if (changed || actions.Count > MaxActionsPerCycle)
            _dirty = true;
        book.Save();
        _openTrades = Describe(book, account);
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

        var t = action.Trade!;
        var s = symbols[t.SymbolId];
        var sent = DateTime.UtcNow;
        var label = identity.Label(TmLabel.Leg(t.MainId, action.Role));
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
                    await session.PlaceStopOrderAsync(ctid, s.SymbolId, Snapshot.Side(h.Dir), h.Volume, h.Price!.Value, label, ClientId(), comment, OrderTimeout);
                    break;
                case MoveOrder mo:
                    requested = Num(mo.Price, s);
                    await session.AmendStopOrderAsync(ctid, mo.OrderId, mo.Price, OrderTimeout);
                    break;
                case CancelOrder c:
                    await session.CancelOrderAsync(ctid, c.OrderId, OrderTimeout);
                    break;
                case SetProtection sp:
                    requested = $"SL {(sp.StopLoss is null ? "none" : Num(sp.StopLoss, s))} TP {(sp.TakeProfit is null ? "none" : Num(sp.TakeProfit, s))}";
                    await session.AmendPositionProtectionAsync(ctid, sp.PositionId, sp.StopLoss, sp.TakeProfit, OrderTimeout);
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
            notify.Warning("TmActionFailed", $"{account.Broker} {s.Name}: {Describe(action)} refused",
                $"Trade P{t.MainId}: {ex.ErrorCode} {ex.Message}. TM tries again next second if the rule still applies.", ctid);
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
            CsvFormat.Time(sent), $"P{t.MainId}", s.Name, role, kind, side, lots, requested, Num(fill?.Price, s),
            CsvFormat.Num(fill?.Latency.TotalMilliseconds, 0), result, detail,
        ]);

    private static string Target(TmAction a) => a switch
    {
        MoveOrder m => $"order {m.OrderId}",
        CancelOrder c => $"order {c.OrderId}",
        SetProtection p => $"position {p.PositionId}",
        ClosePosition c => $"position {c.PositionId}",
        _ => "",
    };

    private static string Describe(TmAction a) => a switch
    {
        OpenMirror => "opening the mirror",
        PlaceHedge => "placing a hedge",
        MoveOrder => "moving a hedge",
        CancelOrder => "cancelling a hedge",
        SetProtection => "setting stop-loss / take-profit",
        ClosePosition => "closing a position",
        _ => a.GetType().Name,
    };

    private static string SideText(int dir) => dir > 0 ? "buy" : "sell";
    private static string Lots(SymbolInfo s, long volume) => CsvFormat.Num(s.LotsForVolume(volume), 2);
    private static string Num(double? v, SymbolInfo s) => CsvFormat.Num(v, s.Digits);
    private static string ClientId() => "tm-" + Guid.NewGuid().ToString("N")[..12];
}
