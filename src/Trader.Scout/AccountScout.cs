using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
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
    string logFolder,
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
        "order_id", "position_id", "price_fill", "slip_pips", "gross", "net", "result", "detail", "chart",
    ];

    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FullSync = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(5);   // ask this long after a candle should have closed
    // A late candle (market open by the broker's hours, candle not in yet) is asked for again after these, then once
    // per candle length (scout.md §2). The session paces all candle requests to 2/s (CTraderSession.TrendbarSpacing).
    private static readonly TimeSpan[] LateRetries = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8)];

    private readonly ILogger _log = log.ForAccount(account.AccountId);
    private readonly List<ChartTracker> _charts = account.Charts.Select(c => new ChartTracker(c, config.SetupSettings)).ToList();
    private readonly Dictionary<ChartTracker, DateTime> _nextFetch = [];
    private readonly Dictionary<ChartTracker, int> _misses = [];      // fetches in a row that found a due candle missing
    private readonly ConcurrentQueue<ProtoOAExecutionEvent> _executions = new();
    private readonly Dictionary<string, string> _endReason = [];   // key → why its setup ended (from the last Cancelled event)
    private readonly HashSet<long> _cancelling = [];
    private readonly HashSet<string> _marginSkipped = [];             // setups logged as waiting for margin (logged once each)
    private DateTime _marginAlerted = DateTime.MinValue, _statusFailed = DateTime.MinValue;
    private readonly SemaphoreSlim _wake = new(0);
    private volatile CTraderSession? _session;
    private volatile IReadOnlyDictionary<long, SymbolInfo> _symbols = new Dictionary<long, SymbolInfo>();
    private bool _loaded;
    private bool _dirty = true;
    private DateTime _lastSync = DateTime.MinValue;

    // Controls from the dashboard (scout.md §2.6): the file is read every 30 s; a changed file is acknowledged after the next sync.
    private static readonly TimeSpan ControlCheck = TimeSpan.FromSeconds(30);
    private Controls _controls = Controls.None;
    private DateTime _nextControlCheck = DateTime.MinValue;
    private IReadOnlyList<AppliedVersion>? _appliedVersions;
    private (DateTime File, DateTime Catalogue)? _controlStamps;
    private bool _ackDue;
    private readonly HashSet<string> _heldLogged = [];
    private string ControlPath => Path.Combine(TradebotPaths.Root, "control", $"scout-{account.Broker}-{account.AccountId}.json");
    private string AckPath => Path.ChangeExtension(ControlPath, ".ack.json");

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
        await CheckControlsAsync(session, symbols);
        if (!_loaded)
        {
            await LoadHistoryAsync(session, symbols);
            _loaded = true;
            DrawMissingCharts(symbols);
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
            var offset = Offset(chart);
            var misses = chart.Main.LastCandleUtc != before ? 0
                : before is null || now >= CandleDue(before.Value, chart.Length, sym.Schedule, offset) ? _misses.GetValueOrDefault(chart) + 1
                : 0;
            _misses[chart] = misses;
            var next = NextFetch(chart.Main.LastCandleUtc, chart.Length, sym.Schedule, misses, offset, now);
            if (misses == 1)
                _log.Event("ScoutCandleLate").Information("{Broker} {Symbol} {Tf}: the candle after {Last:yyyy-MM-dd HH:mm} is not in yet; asking again at {Next:HH:mm:ss}Z, then after 2, 4 and 8 min, then once per candle",
                    account.Broker, sym.Name, chart.Chart.Timeframe, before, next);
            _nextFetch[chart] = next;
            _dirty |= chart.Main.LastCandleUtc != before;
        }

        if (_dirty || now - _lastSync >= FullSync)
        {
            _dirty = false;
            _lastSync = now;
            await SyncAsync(session, symbols);
            if (_ackDue)
            {
                _ackDue = false;
                WriteAck(DateTime.UtcNow, null);
            }
        }
    }

    // ── Controls from the dashboard (scout.md §2.6) ─────────────────────────────────────────────────────────────────
    private async Task CheckControlsAsync(CTraderSession session, Dictionary<string, SymbolInfo> symbols)
    {
        var now = DateTime.UtcNow;
        if (now < _nextControlCheck)
            return;
        _nextControlCheck = now + ControlCheck;
        var catalogue = Path.Combine(TradebotPaths.Config, ControlCatalogue.FileName);
        var stamps = (Stamp(ControlPath), Stamp(catalogue));
        if (stamps == _controlStamps)
            return;
        _controlStamps = stamps;

        Controls next;
        long version = 0;
        try
        {
            var file = ControlFile.Load(ControlPath);
            version = file?.Version ?? 0;
            next = Controls.Evaluate(file, ControlCatalogue.Load(catalogue), account, now);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Event("ControlFileBad").Warning("{Broker}: control file not read ({Message}); the last good one stays in force", account.Broker, ex.Message);
            notify.Warning("ControlFileBad", $"Scout {account.Broker}: dashboard controls not read", $"{ex.Message}. The last good controls stay in force.", account.AccountId);
            WriteAck(now, ex.Message, version);
            return;
        }
        if (next == Controls.None && _controls == Controls.None)
            return; // no file, and there never was one: nothing to do or acknowledge

        foreach (var e in Controls.Reset(_controls, next))
            _log.Event("ControlReset").Information("{Broker}: dashboard control {Scope} {Target} {Key} back as configured", account.Broker, e.Scope, e.Target, e.Key);
        _controls = next;
        foreach (var e in next.Entries)
        {
            if (e.Applied)
                _log.Event("ControlApplied").Information("{Broker}: dashboard control {Scope} {Target} {Key} = {Value} applied", account.Broker, e.Scope, e.Target, e.Key, e.Value);
            else
                _log.Event("ControlRefused").Warning("{Broker}: dashboard control {Scope} {Target} {Key} = {Value} refused: {Reason}", account.Broker, e.Scope, e.Target, e.Key, e.Value, e.Reason);
        }
        _heldLogged.Clear();
        await ApplyTimeframesAsync(session, symbols);
        _dirty = true;
        _ackDue = true;
    }

    private static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    /// <summary>Puts each symbol on the timeframe the controls ask for (or back on the configured one), loading the new chart's history.</summary>
    private async Task ApplyTimeframesAsync(CTraderSession session, Dictionary<string, SymbolInfo> symbols)
    {
        for (var i = 0; i < _charts.Count; i++)
        {
            var old = _charts[i];
            var configured = account.Charts.Where(c => c.Symbol == old.Chart.Symbol).ToList();
            if (configured.Count != 1)
                continue;
            var tf = _controls.TimeframeFor(old.Chart.Symbol, configured[0].Timeframe);
            if (tf == old.Chart.Timeframe)
                continue;
            var chart = new ChartTracker(configured[0] with { Timeframe = tf }, config.SetupSettings);
            _charts[i] = chart;
            _nextFetch.Remove(old);
            _misses.Remove(old);
            _log.Event("ScoutTimeframe").Information("{Broker} {Symbol}: now on {Tf} (was {Old}); pending orders from {Old} stay until filled or expired",
                account.Broker, old.Chart.Symbol, tf, old.Chart.Timeframe, old.Chart.Timeframe);
            if (_loaded && symbols.TryGetValue(chart.Chart.Symbol, out var sym))
                await LoadChartAsync(session, sym, chart);
        }
    }

    private void WriteAck(DateTime now, string? error, long? version = null)
    {
        try
        {
            // Each applied version with its time, carried over from the last acknowledgement across restarts (dashboard.md §3.4).
            _appliedVersions ??= ControlAck.Load(AckPath)?.Applied;
            if (error is null && _controls.Version > 0)
                _appliedVersions = ControlAck.History(_appliedVersions, _controls.Version, CsvFormat.Time(now));
            new ControlAck(version ?? _controls.Version, CsvFormat.Time(now), identity.InstanceId, Controls.Known,
                _controls.Entries.Select(e => new AckEntry(e.Scope, e.Target, e.Key, e.Value, e.Applied ? "applied" : "refused", e.Reason, e.Cancelled.ToList())).ToList(),
                error, _appliedVersions).Write(AckPath);
        }
        catch (IOException ex)
        {
            _log.Warning("{Broker}: acknowledgement not written: {Message}", account.Broker, ex.Message);
        }
    }

    // ── When to ask for candles (scout.md §2): market hours, late-candle backoff, charts spread out ────────────────────
    // Each chart asks a few seconds after the others (0–18 s by its place in the list), so 18 symbols don't ask at once.
    private TimeSpan Offset(ChartTracker chart) => TimeSpan.FromSeconds(_charts.IndexOf(chart) % 10 * 2);

    /// <summary>
    /// When the candle after <paramref name="lastCandle"/> can be asked for: once it should have closed, plus the
    /// chart's offset. Candles whose market was closed throughout by the broker's hours (weekend, daily break) are skipped,
    /// so a closed market costs no requests.
    /// </summary>
    internal static DateTime CandleDue(DateTime lastCandle, TimeSpan length, TradingSchedule schedule, TimeSpan offset)
    {
        var open = lastCandle + length;
        for (var i = 0; i < 1000 && !TradedDuring(schedule, open, length); i++)
            open += length;
        return open + length + CloseDelay + offset;
    }

    /// <summary>
    /// The next time to ask: when the next candle is due, or — while a due candle is missing (<paramref name="misses"/>
    /// fetches in a row) — again after 1, 2, 4 and 8 minutes, then once per candle length (at least 8 minutes).
    /// </summary>
    internal static DateTime NextFetch(DateTime? lastCandle, TimeSpan length, TradingSchedule schedule, int misses, TimeSpan offset, DateTime now)
    {
        if (lastCandle is { } last && CandleDue(last, length, schedule, offset) is var due && due > now)
            return due;
        if (misses <= 0)
            return now;
        return now + (misses <= LateRetries.Length ? LateRetries[misses - 1] : Max(length, LateRetries[^1]));
    }

    // True if the market traded at some moment of [open, open + length), checked every 5 minutes (or per candle if shorter).
    private static bool TradedDuring(TradingSchedule schedule, DateTime open, TimeSpan length)
    {
        var step = length < TimeSpan.FromMinutes(5) ? length : TimeSpan.FromMinutes(5);
        for (var t = open; t < open + length; t += step)
            if (schedule.IsOpen(t))
                return true;
        return false;
    }

    /// <summary>Rebuilds the detectors from past candles once per process start; their events are not logged or acted on.</summary>
    private async Task LoadHistoryAsync(CTraderSession session, Dictionary<string, SymbolInfo> symbols)
    {
        foreach (var chart in _charts)
            if (symbols.TryGetValue(chart.Chart.Symbol, out var sym))
                await LoadChartAsync(session, sym, chart);
    }

    private async Task LoadChartAsync(CTraderSession session, SymbolInfo sym, ChartTracker chart)
    {
        var now = DateTime.UtcNow;
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
        _nextFetch[chart] = NextFetch(chart.Main.LastCandleUtc, chart.Length, sym.Schedule, 0, Offset(chart), DateTime.UtcNow);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task<IReadOnlyList<Candle>> HistoryAsync(CTraderSession session, SymbolInfo sym, string tf, DateTime from, DateTime to)
        => await session.GetTrendbarsAsync(account.AccountId, sym.SymbolId, Timeframes.Period(tf), from, to); // paced by the session

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

        // One cancels the other (Scout spec §4.4 I4), the dashboard's controls (§2.6), and setups that ended: cancel our
        // unfilled orders. Orders from a chart Scout no longer reads (timeframe changed) stay until filled or expired.
        foreach (var (key, order) in ourOrders)
        {
            var siblingFilled = ourPositions.ContainsKey(ScoutState.Sibling(key)) || state.Roles.GetValueOrDefault(ScoutState.Sibling(key))?.PositionId is not null;
            var parts = key.Split('|');
            var chartActive = _charts.Any(c => c.Chart.Symbol == parts[0] && c.Chart.Timeframe == parts[1]);
            if (_controls.CancelReason(key, siblingFilled, chartActive, wants.ContainsKey(key), _endReason.GetValueOrDefault(key)) is not { } reason)
                continue;
            var held = !siblingFilled && _controls.HoldBack(key) is not null;
            if (await CancelAsync(session, key, order, reason) && held)
            {
                if (state.Roles.TryGetValue(key, out var role))
                    role.Result = $"held: {reason}"; // placed again if the owner lets it go (scout.md §2.6)
                ControlEntryFor(key)?.Cancelled.Add(order.OrderId);
                changed = true;
            }
        }

        MarginBudget? budget = null;
        var margins = new Dictionary<(long, long), ExpectedMargin>();
        foreach (var want in wants.Values)
        {
            if (_controls.HoldBack(want.Key) is { } held)
            {
                if (!ourOrders.ContainsKey(want.Key) && !ourPositions.ContainsKey(want.Key) && !state.Roles.ContainsKey(want.Key) && _heldLogged.Add(want.Key))
                {
                    OrderRow(now, want, "skip", 0, StopLoss(want), null, null, "held back", held);
                    _log.Event("ScoutHeldBack").Information("{Broker} {Symbol} {Tf}: {Side} Stop {Price} not placed: {Why}",
                        account.Broker, want.Sym.Name, want.Chart.Chart.Timeframe, Side(want.Dir), Num(want.Level, want.Sym), held);
                }
                continue;
            }
            if (state.Roles.TryGetValue(want.Key, out var heldRole) && heldRole.Result.StartsWith("held", StringComparison.Ordinal) && heldRole.PositionId is null)
            {
                state.Roles.Remove(want.Key); // the owner let it go again: placed once more if the setup is still valid
                changed = true;
            }
            if (state.Roles.ContainsKey(want.Key) || ourOrders.ContainsKey(want.Key) || ourPositions.ContainsKey(want.Key))
                continue;
            var sibling = ScoutState.Sibling(want.Key);
            if (ourPositions.ContainsKey(sibling) || state.Roles.GetValueOrDefault(sibling)?.PositionId is not null)
                continue;
            if (want.Chart.Chart.Mode == ScoutMode.Watch)
                Alert(want, now);
            else
            {
                budget ??= await BudgetAsync(session, rec, ourOrders.Values, margins);
                if (!await MarginFitsAsync(session, budget, want, margins, now))
                    continue; // not remembered: tried again at the next sync, while the setup lasts
                await PlaceAsync(session, want, now);
            }
            changed = true;
        }
        if (changed && !dryRun)
            state.Save(now);
    }

    // ── Margin (scout.md §2.5, issue-log.md #1): no new order the account could not carry once filled, mirrored and hedged ──
    private async Task<MarginBudget?> BudgetAsync(CTraderSession session, ProtoOAReconcileRes rec, IEnumerable<ProtoOAOrder> waiting,
        Dictionary<(long, long), ExpectedMargin> margins)
    {
        if (DateTime.UtcNow - _statusFailed < TimeSpan.FromMinutes(5))
            return null; // the broker did not answer a moment ago (e.g. a replay broker without it): no new orders meanwhile
        try
        {
            var status = await session.GetAccountStatusAsync(account.AccountId, rec);
            var held = 0.0;
            foreach (var o in waiting)
                held += (await MarginAsync(session, o.TradeData.SymbolId, o.TradeData.Volume, margins)).For(o.TradeData.TradeSide == ProtoOATradeSide.Buy ? 1 : -1);
            return new MarginBudget(status, config, held);
        }
        catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
        {
            _statusFailed = DateTime.UtcNow;
            _log.Event("ScoutNoAccountStatus").Warning("{Broker}: account status not available ({Message}); no new orders until it is", account.Broker, ex.Message);
            return null;
        }
    }

    private async Task<ExpectedMargin> MarginAsync(CTraderSession session, long symbolId, long volume, Dictionary<(long, long), ExpectedMargin> margins)
    {
        if (!margins.TryGetValue((symbolId, volume), out var m))
            margins[(symbolId, volume)] = m = (await session.GetExpectedMarginAsync(account.AccountId, symbolId, [volume])).Single();
        return m;
    }

    private async Task<bool> MarginFitsAsync(CTraderSession session, MarginBudget? budget, Want w, Dictionary<(long, long), ExpectedMargin> margins, DateTime now)
    {
        var volume = w.Sym.VolumeForLots(config.LotsFor(w.Sym.Name));
        string why;
        if (budget is null)
            why = "account status not available";
        else
        {
            double margin;
            try
            {
                margin = (await MarginAsync(session, w.Sym.SymbolId, volume, margins)).For(w.Dir);
            }
            catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
            {
                margin = double.PositiveInfinity;
                _log.Warning("{Broker} {Symbol}: expected margin not available ({Message})", account.Broker, w.Sym.Name, ex.Message);
            }
            if (budget.TryTake(margin))
            {
                _marginSkipped.Remove(w.Key);
                return true;
            }
            why = double.IsPositiveInfinity(margin) ? "expected margin not available" : budget.Explain(margin);
        }

        if (_marginSkipped.Add(w.Key))
        {
            OrderRow(now, w, "skip", 0, StopLoss(w), null, null, "no margin", why, volume);
            _log.Event("ScoutNoMargin").Warning("{Broker} {Symbol} {Tf}: {Side} Stop {Price} not placed: {Why}",
                account.Broker, w.Sym.Name, w.Chart.Chart.Timeframe, Side(w.Dir), Num(w.Level, w.Sym), why);
        }
        if (now - _marginAlerted >= TimeSpan.FromHours(1))
        {
            _marginAlerted = now;
            notify.Warning("ScoutNoMargin", $"Scout {account.Broker}: new orders wait, margin is short",
                $"{w.Sym.Name} {Side(w.Dir)} Stop {Num(w.Level, w.Sym)} not placed: {why}. Scout tries again while the setup lasts.", account.AccountId);
        }
        return false;
    }

    private static double? StopLoss(Want w)
    {
        var price = Math.Round(w.Level, w.Sym.Digits);
        return w.Far is { } f && (f - price) * w.Dir < 0 ? Math.Round(f, w.Sym.Digits) : null;
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
        var sl = StopLoss(w);
        var volume = s.VolumeForLots(config.LotsFor(s.Name));
        var expires = now + config.ExpiryCandles * w.Chart.Length;
        var leg = $"{w.Chart.Chart.Timeframe}-{w.PauseStart:yyyyMMddHHmm}-{(w.Reversal ? "R" : "M")}";
        var label = identity.Label(leg);
        var comment = $"{CTraderSession.SentComment(now)} Scout {s.Name} {w.Chart.Chart.Timeframe} {(w.Reversal ? "reversal" : "main")} {w.Chart.HigherTimeframe}:{WithHigher(w)}";
        if (dryRun)
        {
            OrderRow(now, w, "place", 0, sl, expires, null, "dry run", label, volume, DrawChart(w.Chart, Picture(w, 0, price, sl, now, expires)));
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
            record.Chart = DrawChart(w.Chart, Picture(w, order.OrderId, price, sl, now, expires));
            OrderRow(now, w, "place", order.OrderId, sl, expires, null, "ok", label, volume, record.Chart);
            _log.Event("ScoutOrder").Information("{Broker} {Symbol} {Tf}: placed {Side} Stop {Price} SL {Sl} {Lots} lot, expires {Expires:HH:mm}Z, order {OrderId} ({Label})",
                account.Broker, s.Name, w.Chart.Chart.Timeframe, Side(w.Dir), Num(price, s), sl is null ? "none" : Num(sl.Value, s), Lots(s, volume), expires, order.OrderId, label);
            notify.Info("OrderPlaced", $"Scout: {s.Name} {w.Chart.Chart.Timeframe} {Side(w.Dir)} Stop {Num(price, s)}",
                $"{(w.Reversal ? "Reversal stop; " : "")}SL {(sl is null ? "none" : Num(sl.Value, s))}, {Lots(s, volume)} lot; {w.Chart.HigherTimeframe} {WithHigher(w)}. TM takes it over.", account.AccountId);
        }
        catch (CTraderApiException ex)
        {
            record.Result = $"refused: {ex.ErrorCode}";
            OrderRow(now, w, "place", 0, sl, expires, null, "refused", $"{ex.ErrorCode}: {ex.Message}", volume);
            _log.Event("ScoutOrderRefused").Warning("{Broker} {Symbol}: {Side} Stop {Price} refused: {Error}", account.Broker, s.Name, Side(w.Dir), Num(price, s), ex.Message);
            notify.Warning("OrderRefused", $"Scout: {s.Name} {Side(w.Dir)} Stop {Num(price, s)} refused", ex.Message, account.AccountId);
        }
    }

    /// <returns>True when the broker cancelled it.</returns>
    private async Task<bool> CancelAsync(CTraderSession session, string key, ProtoOAOrder order, string reason)
    {
        if (!_symbols.TryGetValue(order.TradeData.SymbolId, out var s) || _cancelling.Contains(order.OrderId))
            return false;
        var parts = key.Split('|');
        var now = DateTime.UtcNow;
        if (dryRun)
        {
            _log.Information("{Broker} {Symbol}: would cancel order {OrderId} ({Reason})", account.Broker, s.Name, order.OrderId, reason);
            return false;
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
            Num(order.StopPrice, s), order.HasStopLoss ? Num(order.StopLoss, s) : "", "", order.OrderId.ToString(CultureInfo.InvariantCulture), "", "", "", "", "", result, reason, "",
        ]);
        return result == "ok";
    }

    /// <summary>The control entry that made Scout cancel this setup role's order: the symbol switched off, else the setup skipped.</summary>
    private ControlEntry? ControlEntryFor(string roleKey)
    {
        var symbol = roleKey.Split('|')[0];
        return _controls.Disabled.Contains(symbol)
            ? _controls.Entries.FirstOrDefault(e => e is { Scope: "symbol", Key: "enabled", Applied: true } && e.Target == symbol)
            : _controls.Entries.FirstOrDefault(e => e is { Scope: "setup", Applied: true } && e.Target == Controls.SetupOf(roleKey));
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
                    Num(deal.ExecutionPrice, ps), "", CsvFormat.Num(Money(close.GrossProfit), digits), CsvFormat.Num(net, digits), "ok", "", "",
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
            positionId?.ToString(CultureInfo.InvariantCulture) ?? "", fill, slip, gross, net, "ok", detail, "",
        ]);

    private void OrderRow(DateTime now, Want w, string action, long orderId, double? sl, DateTime? expires, long? positionId, string result, string detail, long? volume = null, string? chart = null) =>
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), w.Sym.Name, w.Chart.Chart.Timeframe, CsvFormat.Time(w.PauseStart), w.Reversal ? "R" : "M", action, Side(w.Dir),
            volume is { } v ? Lots(w.Sym, v) : "", Num(w.Level, w.Sym), sl is { } x ? Num(x, w.Sym) : "", expires is { } ex ? CsvFormat.Time(ex) : "",
            orderId == 0 ? "" : orderId.ToString(CultureInfo.InvariantCulture), positionId?.ToString(CultureInfo.InvariantCulture) ?? "", "", "", "", "", result, detail, chart ?? "",
        ]);

    // ── Order pictures (scout.md §3.1): never in the way of an order ────────────────────────────────────────────────
    private OrderPicture Picture(Want w, long orderId, double price, double? sl, DateTime placed, DateTime expires) =>
        new(w.Sym.Name, w.Chart.Chart.Timeframe, w.Sym.Digits, w.Sym.PipSize, account.Broker, account.AccountId, orderId, w.Dir, w.Reversal,
            price, sl, placed, expires, w.PauseStart, w.Chart.HigherTimeframe, WithHigher(w), History(w.Chart, placed));

    /// <summary>Scout's other setups on the chart in view of a picture, from the logs (scout.md §3.1); none when they cannot be read.</summary>
    private List<PastSetup> History(ChartTracker chart, DateTime placed)
    {
        try
        {
            var first = chart.Main.Candles.Where(c => c.OpenUtc + chart.Length <= placed).TakeLast(OrderChart.CandlesShown).FirstOrDefault();
            return first is null ? [] : OrderLog.ReadHistory(Path.GetDirectoryName(logFolder)!, chart.Chart.Symbol, chart.Chart.Timeframe, first.OpenUtc, placed);
        }
        catch (Exception ex)
        {
            _log.Event("ScoutChartError").Warning("{Broker} {Symbol}: picture without other setups, the logs could not be read: {Message}",
                account.Broker, chart.Chart.Symbol, ex.Message);
            return [];
        }
    }

    /// <summary>Writes the picture next to that day's logs; returns its path relative to ~/tradebot/logs, or "" when it could not be drawn.</summary>
    private string DrawChart(ChartTracker chart, OrderPicture p)
    {
        try
        {
            var dir = Path.Combine(logFolder, p.PlacedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "charts");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, OrderChart.FileName(p));
            File.WriteAllText(path, OrderChart.Svg(p, chart.Main.Candles));
            return Path.GetRelativePath(TradebotPaths.Logs, path).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            _log.Event("ScoutChartError").Warning("{Broker} {Symbol}: no picture for order {OrderId}: {Message}", account.Broker, p.Symbol, p.OrderId, ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Once after the history is loaded: pictures for remembered orders that have none (placed before pictures
    /// existed, or while drawing failed), from the same candles and the order's place row in the logs. Each gets a
    /// <c>chart</c> row in today's orders.csv, since earlier log files are never changed.
    /// </summary>
    private void DrawMissingCharts(Dictionary<string, SymbolInfo> symbols)
    {
        var missing = state.Roles.Where(r => r.Value.OrderId != 0 && string.IsNullOrEmpty(r.Value.Chart)).ToList();
        if (missing.Count == 0)
            return;
        Dictionary<long, PlacedOrder> placed;
        try
        {
            placed = OrderLog.ReadPlaced(Path.GetDirectoryName(logFolder)!);
        }
        catch (Exception ex)
        {
            _log.Event("ScoutChartError").Warning("{Broker}: could not read past orders for their pictures: {Message}", account.Broker, ex.Message);
            return;
        }
        var drawn = 0;
        foreach (var (key, record) in missing)
        {
            var parts = key.Split('|');
            var chart = _charts.FirstOrDefault(c => c.Chart.Symbol == parts[0] && c.Chart.Timeframe == parts[1]);
            if (chart is null || !symbols.TryGetValue(parts[0], out var s) || !placed.TryGetValue(record.OrderId, out var o))
            {
                _log.Event("ScoutChartSkipped").Information("{Broker}: no picture for order {OrderId} ({Key}): its chart or place row is missing", account.Broker, record.OrderId, key);
                continue;
            }
            var p = new OrderPicture(s.Name, o.Timeframe, s.Digits, s.PipSize, account.Broker, account.AccountId, o.OrderId, o.Dir, o.Reversal,
                o.Price, o.StopLoss, o.TimeUtc, o.ExpiresUtc ?? o.TimeUtc + config.ExpiryCandles * chart.Length, o.SetupUtc, chart.HigherTimeframe,
                o.WithHigher != "unknown" ? o.WithHigher : TaggedEvent.Compare(chart.HigherTrendAt(o.TimeUtc), o.Dir), History(chart, o.TimeUtc));
            if (DrawChart(chart, p) is not { Length: > 0 } path)
                continue;
            record.Chart = path;
            drawn++;
            var now = DateTime.UtcNow;
            ordersCsv.Write(now,
            [
                CsvFormat.Time(now), s.Name, o.Timeframe, CsvFormat.Time(o.SetupUtc), o.Reversal ? "R" : "M", "chart", Side(o.Dir), "", Num(o.Price, s),
                o.StopLoss is { } sl ? Num(sl, s) : "", o.ExpiresUtc is { } ex ? CsvFormat.Time(ex) : "", o.OrderId.ToString(CultureInfo.InvariantCulture),
                record.PositionId?.ToString(CultureInfo.InvariantCulture) ?? "", "", "", "", "", "ok", $"drawn later; placed {CsvFormat.Time(o.TimeUtc)}", path,
            ]);
        }
        _log.Event("ScoutCharts").Information("{Broker}: drew {Drawn} of {Missing} missing order picture(s)", account.Broker, drawn, missing.Count);
        if (drawn > 0 && !dryRun)
            state.Save(DateTime.UtcNow);
    }

    private static string WithHigher(Want w) => TaggedEvent.Compare(w.Chart.Higher?.Trend ?? 0, w.Dir);

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
