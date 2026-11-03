using System.Collections.Concurrent;
using System.Globalization;
using Google.Protobuf;
using Serilog;
using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.MarketData;
using Trader.Core.Notifications;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary>
/// Runs Scout on one account (scout.md §2): reads each chart's closed candles from the broker, feeds the detectors,
/// logs every setup event to setups.csv and, in Trade mode, places the first order of each setup and cancels it again
/// when the setup ends without a fill (Scout spec §4.2). Never moves an order and never touches a position: TM takes
/// the orders over by their label. Acts only while this instance is Primary on the account (open-code-pieces.md §6).
/// </summary>
internal sealed class AccountScout(
    ScoutAccount account,
    ScoutConfig config,
    BotIdentity identity,
    ILogger log,
    Notifier notify,
    InstanceRegistry registry,
    ScoutState state,
    DailyCsvWriter setupsCsv,
    DailyCsvWriter ordersCsv,
    bool dryRun)
{
    public static readonly string[] SetupsHeader =
    [
        "time_utc", "candle_utc", "symbol", "timeframe", "event", "side", "level", "far_side", "setup_utc", "pause_candles",
        "pause_height_atr", "move_atr", "pullback", "atr", "higher_tf", "higher_trend", "with_higher", "mode", "reason",
    ];

    public static readonly string[] OrdersHeader =
    [
        "time_utc", "symbol", "timeframe", "setup_utc", "role", "action", "side", "lots", "price_req", "stop_loss", "expires_utc",
        "order_id", "position_id", "price_fill", "slip_pips", "gross", "net", "result", "detail",
    ];

    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FullSync = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(10);  // keep asking for a candle this long after it should have closed
    private static readonly TimeSpan HistoryGap = TimeSpan.FromMilliseconds(300); // historical data: 5 requests/s per connection
    private static readonly SemaphoreSlim HistoryLock = new(1, 1);           // shared by all accounts on the one connection
    private static DateTime _lastHistoryRequest = DateTime.MinValue;

    private readonly ILogger _log = log.ForAccount(account.AccountId);
    private readonly List<ChartTracker> _charts = account.Charts.Select(c => new ChartTracker(c, config.SetupSettings)).ToList();
    private readonly Dictionary<ChartTracker, DateTime> _nextFetch = [];
    private readonly ConcurrentQueue<ProtoOAExecutionEvent> _executions = new();
    private readonly Dictionary<string, string> _endReason = [];   // key → why its setup ended (from the last Cancelled event)
    private readonly HashSet<long> _cancelling = [];
    private readonly SemaphoreSlim _wake = new(0);
    private volatile CTraderSession? _session;
    private volatile IReadOnlyDictionary<long, SymbolInfo> _symbols = new Dictionary<long, SymbolInfo>();
    private bool _loaded;
    private bool _dirty = true;
    private DateTime _lastSync = DateTime.MinValue;

    public long AccountId => account.AccountId;

    /// <summary>After every (re)connect, once the symbols are resolved.</summary>
    public void SetSession(CTraderSession session, IReadOnlyList<SymbolInfo> symbols)
    {
        _symbols = symbols.ToDictionary(s => s.SymbolId);
        _session = session;
        _dirty = true;
        _wake.Release();
    }

    /// <summary>Every message from the server (receive thread): order events are queued for the loop.</summary>
    public void OnMessage(IMessage message)
    {
        if (message is not ProtoOAExecutionEvent e || e.CtidTraderAccountId != account.AccountId)
            return;
        _executions.Enqueue(e);
        _wake.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            await _wake.WaitAsync(Cycle, ct);
            while (_wake.CurrentCount > 0)
                await _wake.WaitAsync(ct);

            if (_session is not { } session || session.Closed.IsCompleted || registry.RoleOn(account.AccountId) != InstanceRole.Primary)
                continue;
            try
            {
                await RunOnceAsync(session);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
            {
                _log.Event("ScoutCycleError").Warning("{Broker}: {Message}; retrying", account.Broker, ex.Message);
                _dirty = true;
            }
        }
    }

    private async Task RunOnceAsync(CTraderSession session)
    {
        var symbols = _symbols.Values.ToDictionary(s => s.Name);
        if (!_loaded)
        {
            await LoadHistoryAsync(session, symbols);
            _loaded = true;
        }

        if (HandleExecutions() && !dryRun)
            state.Save(DateTime.UtcNow);

        var now = DateTime.UtcNow;
        foreach (var chart in _charts)
        {
            if (!symbols.TryGetValue(chart.Chart.Symbol, out var sym) || now < _nextFetch.GetValueOrDefault(chart))
                continue;
            var before = chart.Main.LastCandleUtc;
            var main = await HistoryAsync(session, sym, chart.Chart.Timeframe, (before ?? now.AddDays(-3)) - 3 * chart.Length, now);
            var higher = chart.HigherTimeframe is { } h
                ? await HistoryAsync(session, sym, h, (chart.Higher!.LastCandleUtc ?? now.AddDays(-30)) - 2 * Timeframes.Length(h), now)
                : [];
            now = DateTime.UtcNow;
            foreach (var e in chart.Feed(main, higher, now))
                Record(chart, sym, e, now);
            _nextFetch[chart] = NextFetch(chart, now);
            _dirty |= chart.Main.LastCandleUtc != before;
        }

        if (_dirty || now - _lastSync >= FullSync)
        {
            _dirty = false;
            _lastSync = now;
            await SyncAsync(session, symbols);
        }
    }

    // The next candle is due when the last one's period has passed twice; until then nothing to ask for. A candle that
    // does not come (market closed, weekend) is asked for every 30 s for 10 minutes, then once per candle length.
    private static DateTime NextFetch(ChartTracker chart, DateTime now)
    {
        if (chart.Main.LastCandleUtc is not { } last)
            return now.AddSeconds(30);
        var due = last + 2 * chart.Length + TimeSpan.FromSeconds(5);
        if (due > now)
            return due;
        return now - due < LateWindow ? now.AddSeconds(30) : now + chart.Length;
    }

    /// <summary>Rebuilds the detectors from past candles once per process start; their events are not logged or acted on.</summary>
    private async Task LoadHistoryAsync(CTraderSession session, Dictionary<string, SymbolInfo> symbols)
    {
        var now = DateTime.UtcNow;
        foreach (var chart in _charts)
        {
            if (!symbols.TryGetValue(chart.Chart.Symbol, out var sym))
                continue;
            var days = TimeSpan.FromDays(config.HistoryDays);
            var main = await HistoryAsync(session, sym, chart.Chart.Timeframe, now - Max(days, 200 * chart.Length), now);
            var higher = chart.HigherTimeframe is { } h ? await HistoryAsync(session, sym, h, now - Max(days, 100 * Timeframes.Length(h)), now) : [];
            var skipped = chart.Feed(main, higher, now).Count;
            var d = chart.Main;
            _log.Event("ScoutHistory").Information(
                "{Broker} {Symbol} {Tf}: {Candles} candles ({Events} past events); trend {Trend}, {Higher} trend {HigherTrend}; pending {Main}{Rev}",
                account.Broker, sym.Name, chart.Chart.Timeframe, d.Count, skipped, TrendName(d.Trend), chart.HigherTimeframe ?? "-",
                TrendName(chart.Higher?.Trend ?? 0), d.MainLevel is { } m ? $"{Side(d.Trend)} Stop {Num(m, sym)}" : "none",
                d.ReversalLevel is { } r ? $", reversal {Side(-d.Trend)} Stop {Num(r, sym)}" : "");
            _nextFetch[chart] = NextFetch(chart, DateTime.UtcNow);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task<IReadOnlyList<Candle>> HistoryAsync(CTraderSession session, SymbolInfo sym, string tf, DateTime from, DateTime to)
    {
        await HistoryLock.WaitAsync();
        try
        {
            var wait = _lastHistoryRequest + HistoryGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
            _lastHistoryRequest = DateTime.UtcNow;
            return await session.GetTrendbarsAsync(account.AccountId, sym.SymbolId, Timeframes.Period(tf), from, to);
        }
        finally
        {
            HistoryLock.Release();
        }
    }

    // ── Setup events → setups.csv and the log ────────────────────────────────────────────────────────────────────────
    private void Record(ChartTracker chart, SymbolInfo sym, TaggedEvent te, DateTime now)
    {
        var e = te.Event;
        var tf = chart.Chart.Timeframe;
        if (e is { Kind: SetupEventKind.Cancelled, PauseStartUtc: { } ps })
            _endReason[ScoutState.Key(sym.Name, tf, ps, e.Reversal)] = e.Reason ?? "cancelled";
        var p = e.Pause;
        setupsCsv.Write(now,
        [
            CsvFormat.Time(now), CsvFormat.Time(e.CandleUtc), sym.Name, tf, e.Kind.ToString(), Side(e.Dir), Num(e.Level, sym),
            e.FarSide is { } f ? Num(f, sym) : "", e.PauseStartUtc is { } s ? CsvFormat.Time(s) : "", p?.Candles.ToString(CultureInfo.InvariantCulture) ?? "",
            p is null ? "" : CsvFormat.Num((p.High - p.Low) / e.Atr, 2), p is null ? "" : CsvFormat.Num(p.MoveAtr, 2), p is null ? "" : CsvFormat.Num(p.Pullback, 2),
            Num(e.Atr, sym), chart.HigherTimeframe ?? "", TrendName(te.HigherTrend), te.WithHigher, chart.Chart.Mode.ToString(), e.Reason ?? "",
        ]);
        if (e.Kind is SetupEventKind.StopMoved or SetupEventKind.ReversalMoved)
            return;
        _log.Event("ScoutSetup").Information("{Broker} {Symbol} {Tf} candle {Candle:yyyy-MM-dd HH:mm}: {Kind} {Side} {Level} ({Higher} {With}){Reason}",
            account.Broker, sym.Name, tf, e.CandleUtc, e.Kind, Side(e.Dir), Num(e.Level, sym), chart.HigherTimeframe, te.WithHigher,
            e.Reason is null ? "" : $" — {e.Reason}");
    }

    // ── Orders: what the detectors want vs. what is on the account ──────────────────────────────────────────────────
    private sealed record Want(ChartTracker Chart, SymbolInfo Sym, string Key, DateTime PauseStart, bool Reversal, int Dir, double Level, double? Far);

    private async Task SyncAsync(CTraderSession session, Dictionary<string, SymbolInfo> symbols)
    {
        var now = DateTime.UtcNow;
        var rec = await session.ReconcileAsync(account.AccountId);
        var ourOrders = new Dictionary<string, ProtoOAOrder>();
        foreach (var o in rec.Order)
            if (KeyOf(o.TradeData.Label, o.TradeData.SymbolId) is { } k)
                ourOrders[k] = o;
        var ourPositions = new Dictionary<string, ProtoOAPosition>();
        var changed = false;
        foreach (var p in rec.Position)
            if (KeyOf(p.TradeData.Label, p.TradeData.SymbolId) is { } k)
            {
                ourPositions[k] = p;
                if (state.Roles.TryGetValue(k, out var r) && r.PositionId is null)
                {
                    r.PositionId = p.PositionId;
                    r.Result = "filled";
                    changed = true;
                }
            }

        var wants = new Dictionary<string, Want>();
        foreach (var chart in _charts)
        {
            var d = chart.Main;
            if (d.PauseStartUtc is not { } ps || !symbols.TryGetValue(chart.Chart.Symbol, out var sym))
                continue;
            if (d.MainLevel is { } m)
            {
                var k = ScoutState.Key(sym.Name, chart.Chart.Timeframe, ps, false);
                wants[k] = new Want(chart, sym, k, ps, false, d.Trend, m, d.MainFarSide);
            }
            if (d.ReversalLevel is { } r)
            {
                var k = ScoutState.Key(sym.Name, chart.Chart.Timeframe, ps, true);
                wants[k] = new Want(chart, sym, k, ps, true, -d.Trend, r, d.ReversalFarSide);
            }
        }

        // One cancels the other (Scout spec §4.4 I4), and setups that ended: cancel our unfilled orders.
        foreach (var (key, order) in ourOrders)
        {
            var siblingFilled = ourPositions.ContainsKey(ScoutState.Sibling(key)) || state.Roles.GetValueOrDefault(ScoutState.Sibling(key))?.PositionId is not null;
            if (!siblingFilled && wants.ContainsKey(key))
                continue;
            var reason = siblingFilled ? "the other order of the pair filled" : _endReason.GetValueOrDefault(key, "setup ended");
            await CancelAsync(session, key, order, reason);
        }

        foreach (var want in wants.Values)
        {
            if (state.Roles.ContainsKey(want.Key) || ourOrders.ContainsKey(want.Key) || ourPositions.ContainsKey(want.Key))
                continue;
            var sibling = ScoutState.Sibling(want.Key);
            if (ourPositions.ContainsKey(sibling) || state.Roles.GetValueOrDefault(sibling)?.PositionId is not null)
                continue;
            if (want.Chart.Chart.Mode == ScoutMode.Watch)
                Alert(want, now);
            else
                await PlaceAsync(session, want, now);
            changed = true;
        }
        if (changed && !dryRun)
            state.Save(now);
    }

    private string? KeyOf(string? label, long symbolId)
    {
        if (!BotIdentity.TryParseLabel(label, out var bot, out _, out var leg) || bot != ScoutConfig.BotName || leg is null)
            return null;
        var parts = leg.Split('-');
        if (parts.Length != 3 || !_symbols.TryGetValue(symbolId, out var sym)
            || !DateTime.TryParseExact(parts[1], "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ps))
            return null;
        return ScoutState.Key(sym.Name, parts[0], ps, parts[2] == "R");
    }

    private void Alert(Want w, DateTime now)
    {
        var text = $"{w.Sym.Name} {w.Chart.Chart.Timeframe}: new {(w.Reversal ? "reversal " : "")}{Side(w.Dir)} Stop at {Num(w.Level, w.Sym)}";
        notify.Info("Setup", $"Scout: {text}", $"Stop-loss {(w.Far is { } f ? Num(f, w.Sym) : "none")}; {w.Chart.HigherTimeframe} trend {WithHigher(w)}", account.AccountId);
        OrderRow(now, w, "alert", 0, null, null, null, "", "watch mode");
        if (!dryRun)
            state.Roles[w.Key] = new RoleRecord { FirstSeenUtc = now, Result = "alerted" };
    }

    private async Task PlaceAsync(CTraderSession session, Want w, DateTime now)
    {
        var s = w.Sym;
        var price = Math.Round(w.Level, s.Digits);
        double? sl = w.Far is { } f && (f - price) * w.Dir < 0 ? Math.Round(f, s.Digits) : null;
        var volume = s.VolumeForLots(config.Lots);
        var expires = now + config.ExpiryCandles * w.Chart.Length;
        var leg = $"{w.Chart.Chart.Timeframe}-{w.PauseStart:yyyyMMddHHmm}-{(w.Reversal ? "R" : "M")}";
        var label = identity.Label(leg);
        var comment = $"{CTraderSession.SentComment(now)} Scout {s.Name} {w.Chart.Chart.Timeframe} {(w.Reversal ? "reversal" : "main")} {w.Chart.HigherTimeframe}:{WithHigher(w)}";
        if (dryRun)
        {
            OrderRow(now, w, "place", 0, sl, expires, null, "dry run", label, volume);
            _log.Information("{Broker} {Symbol}: would place {Side} Stop {Price} SL {Sl} ({Label})", account.Broker, s.Name, Side(w.Dir), Num(price, s), sl is null ? "none" : Num(sl.Value, s), label);
            return;
        }

        var record = new RoleRecord { FirstSeenUtc = now };
        state.Roles[w.Key] = record;
        try
        {
            var order = await session.PlacePendingOrderAsync(account.AccountId, s.SymbolId, ProtoOAOrderType.Stop,
                w.Dir == 1 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell, volume, price, sl, null, label, ClientId(), comment, OrderTimeout,
                trailingStopLoss: sl is not null, expiresUtc: expires);
            record.OrderId = order.OrderId;
            record.Result = "placed";
            OrderRow(now, w, "place", order.OrderId, sl, expires, null, "ok", label, volume);
            _log.Event("ScoutOrder").Information("{Broker} {Symbol} {Tf}: placed {Side} Stop {Price} SL {Sl} {Lots} lot, expires {Expires:HH:mm}Z, order {OrderId} ({Label})",
                account.Broker, s.Name, w.Chart.Chart.Timeframe, Side(w.Dir), Num(price, s), sl is null ? "none" : Num(sl.Value, s), config.Lots, expires, order.OrderId, label);
            notify.Info("OrderPlaced", $"Scout: {s.Name} {w.Chart.Chart.Timeframe} {Side(w.Dir)} Stop {Num(price, s)}",
                $"{(w.Reversal ? "Reversal stop; " : "")}SL {(sl is null ? "none" : Num(sl.Value, s))}, {config.Lots} lot; {w.Chart.HigherTimeframe} {WithHigher(w)}. TM takes it over.", account.AccountId);
        }
        catch (CTraderApiException ex)
        {
            record.Result = $"refused: {ex.ErrorCode}";
            OrderRow(now, w, "place", 0, sl, expires, null, "refused", $"{ex.ErrorCode}: {ex.Message}", volume);
            _log.Event("ScoutOrderRefused").Warning("{Broker} {Symbol}: {Side} Stop {Price} refused: {Error}", account.Broker, s.Name, Side(w.Dir), Num(price, s), ex.Message);
            notify.Warning("OrderRefused", $"Scout: {s.Name} {Side(w.Dir)} Stop {Num(price, s)} refused", ex.Message, account.AccountId);
        }
    }

    private async Task CancelAsync(CTraderSession session, string key, ProtoOAOrder order, string reason)
    {
        if (!_symbols.TryGetValue(order.TradeData.SymbolId, out var s) || _cancelling.Contains(order.OrderId))
            return;
        var parts = key.Split('|');
        var now = DateTime.UtcNow;
        if (dryRun)
        {
            _log.Information("{Broker} {Symbol}: would cancel order {OrderId} ({Reason})", account.Broker, s.Name, order.OrderId, reason);
            return;
        }
        _cancelling.Add(order.OrderId);
        string result;
        try
        {
            await session.CancelOrderAsync(account.AccountId, order.OrderId, OrderTimeout);
            result = "ok";
            _log.Event("ScoutCancel").Information("{Broker} {Symbol}: cancelled order {OrderId} {Side} Stop {Price} — {Reason}",
                account.Broker, s.Name, order.OrderId, SideOf(order.TradeData.TradeSide), Num(order.StopPrice, s), reason);
        }
        catch (CTraderApiException ex)
        {
            result = $"refused: {ex.ErrorCode}";
            _log.Event("ScoutCancelRefused").Warning("{Broker} {Symbol}: cancel of order {OrderId} refused: {Error}", account.Broker, s.Name, order.OrderId, ex.Message);
        }
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), s.Name, parts[1], SetupTime(parts[2]), parts[3], "cancel", SideOf(order.TradeData.TradeSide), Lots(s, order.TradeData.Volume),
            Num(order.StopPrice, s), order.HasStopLoss ? Num(order.StopLoss, s) : "", "", order.OrderId.ToString(CultureInfo.InvariantCulture), "", "", "", "", "", result, reason,
        ]);
    }

    // ── Execution events: fills, expiries and closes of what Scout started (for the review, Scout spec §4.6) ────────
    /// <returns>True when the memory changed (a fill).</returns>
    private bool HandleExecutions()
    {
        var changed = false;
        while (_executions.TryDequeue(out var e))
        {
            var now = DateTime.UtcNow;
            if (e.Order is { } o && KeyOf(o.TradeData.Label, o.TradeData.SymbolId) is { } key && _symbols.TryGetValue(o.TradeData.SymbolId, out var s))
            {
                var parts = key.Split('|');
                switch (e.ExecutionType)
                {
                    case ProtoOAExecutionType.OrderFilled when o.OrderType == ProtoOAOrderType.Stop && e.Position is not null && e.Deal is not null:
                        if (state.Roles.TryGetValue(key, out var r))
                        {
                            r.PositionId = e.Position.PositionId;
                            r.Result = "filled";
                            changed = true;
                        }
                        var slip = (e.Deal.ExecutionPrice - o.StopPrice) * (o.TradeData.TradeSide == ProtoOATradeSide.Buy ? 1 : -1) / s.PipSize;
                        ExecRow(now, s, parts, "filled", o, e.Position.PositionId, Num(e.Deal.ExecutionPrice, s), CsvFormat.Num(slip, 1), "", "", "");
                        _log.Event("ScoutFilled").Information("{Broker} {Symbol}: order {OrderId} filled at {Price} (slip {Slip:F1} pips) → position {PositionId}",
                            account.Broker, s.Name, o.OrderId, Num(e.Deal.ExecutionPrice, s), slip, e.Position.PositionId);
                        _dirty = true; // the other order of the pair is cancelled in the next sync
                        break;
                    case ProtoOAExecutionType.OrderExpired:
                        ExecRow(now, s, parts, "expired", o, null, "", "", "", "", "");
                        break;
                    case ProtoOAExecutionType.OrderCancelled when o.OrderType == ProtoOAOrderType.Stop:
                        if (!_cancelling.Remove(o.OrderId))
                            ExecRow(now, s, parts, "cancelled", o, null, "", "", "", "", "not by Scout (owner or TM)");
                        break;
                    case ProtoOAExecutionType.OrderRejected:
                        ExecRow(now, s, parts, "rejected", o, null, "", "", "", "", e.ErrorCode ?? "");
                        break;
                }
            }

            // A deal that closes (part of) a position that came from Scout's order: its result.
            if (e.Deal is { ClosePositionDetail: { } close } deal && state.Roles.FirstOrDefault(x => x.Value.PositionId == deal.PositionId) is { Key: { } pk }
                && _symbols.TryGetValue(deal.SymbolId, out var ps))
            {
                var digits = close.HasMoneyDigits ? (int)close.MoneyDigits : 2;
                double Money(long v) => v / Math.Pow(10, digits);
                var net = Money(close.GrossProfit) + Money(close.Swap) + Money(close.Commission);
                var parts = pk.Split('|');
                ordersCsv.Write(now,
                [
                    CsvFormat.Time(now), ps.Name, parts[1], SetupTime(parts[2]), parts[3], "closed", SideOf(deal.TradeSide == ProtoOATradeSide.Buy ? ProtoOATradeSide.Sell : ProtoOATradeSide.Buy),
                    Lots(ps, deal.FilledVolume), "", "", "", deal.OrderId.ToString(CultureInfo.InvariantCulture), deal.PositionId.ToString(CultureInfo.InvariantCulture),
                    Num(deal.ExecutionPrice, ps), "", CsvFormat.Num(Money(close.GrossProfit), digits), CsvFormat.Num(net, digits), "ok", "",
                ]);
                _log.Event("ScoutClosed").Information("{Broker} {Symbol}: position {PositionId} closed {Lots} lot at {Price}, gross {Gross}, net {Net}",
                    account.Broker, ps.Name, deal.PositionId, Lots(ps, deal.FilledVolume), Num(deal.ExecutionPrice, ps), Money(close.GrossProfit), net);
            }
        }
        return changed;
    }

    private void ExecRow(DateTime now, SymbolInfo s, string[] parts, string action, ProtoOAOrder o, long? positionId, string fill, string slip, string gross, string net, string detail) =>
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), s.Name, parts[1], SetupTime(parts[2]), parts[3], action, SideOf(o.TradeData.TradeSide), Lots(s, o.TradeData.Volume),
            Num(o.StopPrice, s), o.HasStopLoss ? Num(o.StopLoss, s) : "", "", o.OrderId.ToString(CultureInfo.InvariantCulture),
            positionId?.ToString(CultureInfo.InvariantCulture) ?? "", fill, slip, gross, net, "ok", detail,
        ]);

    private void OrderRow(DateTime now, Want w, string action, long orderId, double? sl, DateTime? expires, long? positionId, string result, string detail, long? volume = null) =>
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), w.Sym.Name, w.Chart.Chart.Timeframe, CsvFormat.Time(w.PauseStart), w.Reversal ? "R" : "M", action, Side(w.Dir),
            volume is { } v ? Lots(w.Sym, v) : "", Num(w.Level, w.Sym), sl is { } x ? Num(x, w.Sym) : "", expires is { } ex ? CsvFormat.Time(ex) : "",
            orderId == 0 ? "" : orderId.ToString(CultureInfo.InvariantCulture), positionId?.ToString(CultureInfo.InvariantCulture) ?? "", "", "", "", "", result, detail,
        ]);

    private static string WithHigher(Want w)
    {
        var t = w.Chart.Higher?.Trend ?? 0;
        return t == 0 ? "unknown" : t == w.Dir ? "with" : "against";
    }

    private static string SetupTime(string compact) =>
        DateTime.TryParseExact(compact, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? CsvFormat.Time(t) : compact;

    private static string ClientId() => Guid.NewGuid().ToString("N")[..16];
    private static string Side(int dir) => dir == 1 ? "Buy" : dir == -1 ? "Sell" : "";
    private static string SideOf(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? "Buy" : "Sell";
    private static string TrendName(int t) => t == 1 ? "up" : t == -1 ? "down" : "unknown";
    private static string Num(double v, SymbolInfo s) => CsvFormat.Num(v, s.Digits);
    private static string Lots(SymbolInfo s, long volume) => CsvFormat.Num(s.LotsForVolume(volume), 2);
}
