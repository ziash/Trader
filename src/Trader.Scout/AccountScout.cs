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
    bool dryRun,
    Func<DateTime>? utcNow = null,
    string? controlDir = null,
    Func<DateTime?>? tmHealthyUtc = null,
    string? tmStateFile = null,
    LockStep? lockStep = null)
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
    private static readonly TimeSpan UnknownGrace = TimeSpan.FromSeconds(30);       // a send without a reply is judged only after this, and two reconciles
    private const int MaxSendAttempts = 2;
    private static readonly TimeSpan[] CancelBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5)];
    private static readonly TimeSpan QuoteMaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RecoverEvery = TimeSpan.FromMinutes(5), RecoverInDoubtEvery = TimeSpan.FromSeconds(30), RecoverWindow = TimeSpan.FromDays(14);
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly string _controlDir = controlDir ?? Path.Combine(TradebotPaths.Root, "control");
    private readonly HashSet<string> _skipLogged = [];                // setup role + reason already logged as a skip
    private readonly HashSet<long> _duplicatesAlerted = [];
    private readonly Dictionary<string, string> _sizing = [];         // the size worked out for a setup role, for its log line
    private volatile IBrokerGateway? _gateway;
    private bool _recoverDue = true;
    private DateTime _lastRecover = DateTime.MinValue, _cancelRetryDue = DateTime.MaxValue;
    private string? _haltAnnounced;
    private static readonly TimeSpan StressCheckEvery = TimeSpan.FromMinutes(5);
    private DateTime _nextStressCheck = DateTime.MinValue, _stressAlerted = DateTime.MinValue;
    private static readonly TimeSpan TmGrace = TimeSpan.FromSeconds(60);            // a TM restart inside this does not stop new entries
    private readonly Func<DateTime?> _tmHealthyUtc = tmHealthyUtc ?? (() => registry.HealthyUtc(ScoutConfig.TmBotName, account.AccountId));
    private DateTime _tmLastHealthy = DateTime.MinValue;
    private readonly HashSet<string> _marginSkipped = [];             // setups logged as waiting for margin (logged once each)
    private DateTime _marginAlerted = DateTime.MinValue, _statusFailed = DateTime.MinValue, _riskAlerted = DateTime.MinValue;
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
    private readonly string _tmStateFile = tmStateFile ?? RiskReport.TmStateFile(account);
    private string ControlPath => Path.Combine(_controlDir, $"scout-{account.Broker}-{account.AccountId}.json");
    private string AckPath => Path.ChangeExtension(ControlPath, ".ack.json");

    public long AccountId => account.AccountId;

    private DateTime Now => _utcNow();

    /// <summary>After every (re)connect, once the symbols are resolved.</summary>
    public void SetSession(CTraderSession session, PriceFeed feed, IReadOnlyList<SymbolInfo> symbols, IReadOnlyDictionary<string, UsdConversion> conversions)
    {
        _session = session;
        UseGateway(new SessionGateway(session, feed, conversions), symbols);
    }

    internal void UseGateway(IBrokerGateway gateway, IReadOnlyList<SymbolInfo> symbols)
    {
        _symbols = symbols.ToDictionary(s => s.SymbolId);
        _gateway = gateway;
        _dirty = true;
        _recoverDue = true; // events may have been missed while there was no connection
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
            if (lockStep is { } step)
            {
                await step.NextTurnAsync(ct);
                await TurnAsync(step);
                continue;
            }
            await _wake.WaitAsync(Cycle, ct);
            while (_wake.CurrentCount > 0)
                await _wake.WaitAsync(ct);

            if (_session is not { } session || session.Closed.IsCompleted || _gateway is not { } gateway || registry.RoleOn(account.AccountId) != InstanceRole.Primary)
                continue;
            try
            {
                await RunOnceAsync(gateway);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
            {
                _log.Event("ScoutCycleError").Warning("{Broker}: {Message}; retrying", account.Broker, ex.Message);
                _dirty = true;
            }
        }
    }

    /// <summary>A replay's turn (open-code-pieces.md §3): Scout acts at the end of each 5-minute candle; other turns are passed at once.</summary>
    private async Task TurnAsync(LockStep step)
    {
        if (step.UtcNow.Ticks % TimeSpan.FromMinutes(5).Ticks == 0 && _gateway is { } gateway)
        {
            try
            {
                await RunOnceAsync(gateway);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
            {
                _log.Event("ScoutCycleError").Warning("{Broker}: {Message}; retrying", account.Broker, ex.Message);
                _dirty = true;
            }
        }
        await step.FinishTurnAsync();
    }

    internal async Task RunOnceAsync(IBrokerGateway session)
    {
        var symbols = _symbols.Values.ToDictionary(s => s.Name);
        await CheckControlsAsync(session, symbols);
        if (!_loaded)
        {
            await LoadHistoryAsync(session, symbols);
            _loaded = true;
            DrawMissingCharts(symbols);
        }

        if (HandleExecutions())
            Persist();

        var now = _utcNow();
        foreach (var chart in _charts)
        {
            if (!symbols.TryGetValue(chart.Chart.Symbol, out var sym) || now < _nextFetch.GetValueOrDefault(chart))
                continue;
            var before = chart.Main.LastCandleUtc;
            var main = await HistoryAsync(session, sym, chart.Chart.Timeframe, (before ?? now.AddDays(-3)) - 3 * chart.Length, now);
            var higher = chart.HigherTimeframe is { } h
                ? await HistoryAsync(session, sym, h, (chart.Higher!.LastCandleUtc ?? now.AddDays(-30)) - 2 * Timeframes.Length(h), now)
                : [];
            now = _utcNow();
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

        if (_dirty || now - _lastSync >= FullSync || now >= _cancelRetryDue)
        {
            _dirty = false;
            _lastSync = now;
            await SyncAsync(session, symbols);
            _cancelRetryDue = state.Roles.Values.Where(r => r is { State: RoleState.CancelRequested, NextCancelUtc: not null }).Select(r => r.NextCancelUtc!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
            if (_ackDue && !state.Roles.Values.Any(r => r.State == RoleState.CancelRequested)) // "applied" only once the broker has confirmed its cancels
            {
                _ackDue = false;
                WriteAck(_utcNow(), null);
            }
        }
    }

    // ── Controls from the dashboard (scout.md §2.6) ─────────────────────────────────────────────────────────────────
    private async Task CheckControlsAsync(IBrokerGateway session, Dictionary<string, SymbolInfo> symbols)
    {
        var now = _utcNow();
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
    private async Task ApplyTimeframesAsync(IBrokerGateway session, Dictionary<string, SymbolInfo> symbols)
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
    private async Task LoadHistoryAsync(IBrokerGateway session, Dictionary<string, SymbolInfo> symbols)
    {
        foreach (var chart in _charts)
            if (symbols.TryGetValue(chart.Chart.Symbol, out var sym))
                await LoadChartAsync(session, sym, chart);
    }

    private async Task LoadChartAsync(IBrokerGateway session, SymbolInfo sym, ChartTracker chart)
    {
        var now = _utcNow();
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
        _nextFetch[chart] = NextFetch(chart.Main.LastCandleUtc, chart.Length, sym.Schedule, 0, Offset(chart), _utcNow());
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task<IReadOnlyList<Candle>> HistoryAsync(IBrokerGateway session, SymbolInfo sym, string tf, DateTime from, DateTime to)
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
    private sealed record Want(ChartTracker Chart, SymbolInfo Sym, string Key, DateTime PauseStart, bool Reversal, int Dir, double Level, double? Far, DateTime SinceUtc);

    private async Task SyncAsync(IBrokerGateway session, Dictionary<string, SymbolInfo> symbols)
    {
        var now = Now;
        var rec = await session.ReconcileAsync(account.AccountId);
        var ourOrders = new Dictionary<string, ProtoOAOrder>();
        var duplicates = new List<(string Key, ProtoOAOrder Order)>();
        foreach (var o in rec.Order.OrderBy(o => o.OrderId))
            if (KeyOf(o.TradeData.Label, o.TradeData.SymbolId) is { } k && !ourOrders.TryAdd(k, o))
                duplicates.Add((k, o));
        var ourPositions = new Dictionary<string, ProtoOAPosition>();
        foreach (var p in rec.Position)
            if (KeyOf(p.TradeData.Label, p.TradeData.SymbolId) is { } k)
                ourPositions[k] = p;

        var changed = ResolveFromReconcile(ourOrders, ourPositions, now);
        changed |= await RecoverFromHistoryAsync(session, ourOrders, ourPositions, now);
        if (changed)
            Persist();
        foreach (var (key, order) in duplicates)
            await CancelDuplicateAsync(session, key, order);

        var status = await StatusAsync(session, rec);
        var halt = UpdateHalt(status, now);

        var wants = new Dictionary<string, Want>();
        foreach (var chart in _charts)
        {
            if (!symbols.TryGetValue(chart.Chart.Symbol, out var sym))
                continue;
            foreach (var i in OrderPlanner.Wanted(chart.Main, sym.Name, chart.Chart.Timeframe))
                wants[i.Key] = new Want(chart, sym, i.Key, i.PauseStartUtc, i.Reversal, i.Dir, i.Level, i.FarSide, i.SinceUtc);
        }

        // One cancels the other (Scout spec §4.4 I4), a halt (§2.7), the dashboard's controls (§2.6), and setups that ended: cancel
        // our unfilled orders. Orders from a chart Scout no longer reads (timeframe changed) stay until filled or expired.
        foreach (var (key, order) in ourOrders)
        {
            var siblingFilled = ourPositions.ContainsKey(ScoutState.Sibling(key)) || state.Roles.GetValueOrDefault(ScoutState.Sibling(key))?.PositionId is not null;
            var parts = key.Split('|');
            var chartActive = _charts.Any(c => c.Chart.Symbol == parts[0] && c.Chart.Timeframe == parts[1]);
            var reason = !siblingFilled && halt is not null
                ? halt
                : _controls.CancelReason(key, siblingFilled, chartActive, wants.ContainsKey(key), _endReason.GetValueOrDefault(key));
            if (reason is null)
            {
                if (state.Roles.TryGetValue(key, out var kept) && kept.State == RoleState.CancelRequested)
                {
                    (kept.State, kept.CancelAttempts, kept.NextCancelUtc, kept.CancelAlerted) = (RoleState.Working, 0, null, false); // the reason went away
                    Persist();
                }
                continue;
            }
            var held = !siblingFilled && (halt is not null || _controls.HoldBack(key) is not null);
            if (await CancelAsync(session, key, order, reason) && held)
            {
                if (state.Roles.TryGetValue(key, out var role))
                    (role.State, role.Result) = (RoleState.Held, $"held: {reason}"); // placed again when released (scout.md §2.6, §2.7)
                if (halt is null)
                    ControlEntryFor(key)?.Cancelled.Add(order.OrderId);
                Persist();
            }
        }

        var margins = new Dictionary<(long, long), ExpectedMargin>();
        var budget = await StressCheckAsync(session, status, rec, margins, now);
        RiskBook? risk = null;
        foreach (var want in wants.Values)
        {
            if (halt is not null)
            {
                if (!ourOrders.ContainsKey(want.Key) && !ourPositions.ContainsKey(want.Key) && !state.Roles.ContainsKey(want.Key))
                    SkipOnce(now, want, "halted", "ScoutHalted", halt);
                continue;
            }
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
                state.Roles.Remove(want.Key); // released again: placed once more if the setup is still valid
                Persist();
            }
            if (state.Roles.TryGetValue(want.Key, out var known) && known.State != RoleState.Intended || ourOrders.ContainsKey(want.Key) || ourPositions.ContainsKey(want.Key))
                continue;
            var sibling = ScoutState.Sibling(want.Key);
            if (ourPositions.ContainsKey(sibling) || state.Roles.GetValueOrDefault(sibling)?.PositionId is not null)
                continue;
            if (want.Chart.Chart.Mode == ScoutMode.Watch)
            {
                Alert(want, now);
                Persist();
                continue;
            }
            if (ReadyToPlace(session, want, status, now) is not { } volume)
                continue;
            double riskCash = 0;
            if (config.PortfolioInForce.Enabled)
            {
                risk ??= BuildRisk(session, status, rec);
                if (RiskAllows(session, risk, want, volume, now) is not { } allowed)
                    continue; // not remembered: tried again at the next sync, while the setup lasts
                (volume, riskCash) = allowed;
            }
            budget ??= await BudgetAsync(session, status, rec, margins);
            if (!await MarginFitsAsync(session, budget, want, volume, margins, now))
                continue; // not remembered: tried again at the next sync, while the setup lasts
            await PlaceAsync(session, want, volume, now);
            risk?.Take(want.Sym.Name, want.Dir, riskCash, RiskBook.PauseOf(want.Key));
        }
    }

    // ── Money at risk across the account (scout.md §2.9, review P-03): new risk only; what is on the account is never touched ──
    private RiskBook BuildRisk(IBrokerGateway session, AccountStatus? status, ProtoOAReconcileRes rec)
    {
        var book = RiskBook.Build(config.PortfolioInForce, status?.Equity, new RiskInputs(rec, _symbols,
            currency => session.UsdPer(account.AccountId, currency), id => session.LastQuote(account.AccountId, id)?.Spread ?? 0, KeyOf, config.SizingInForce, TmLinks.Load(_tmStateFile)));
        if (state.Roles.Where(x => x.Value.IsInDoubt).Select(x => x.Key).FirstOrDefault() is { } unknown)
            book.Fail($"what became of the order for {unknown} is not known yet");
        var now = Now;
        if (book.Problem is { } problem && now - _riskAlerted >= TimeSpan.FromHours(1))
        {
            _riskAlerted = now;
            notify.Warning("ScoutRiskUnknown", $"Scout {account.Broker}: new orders wait, the money at risk cannot be worked out", $"{problem}. Scout tries again while each setup lasts.", account.AccountId);
        }
        return book;
    }

    /// <summary>The volume the limits allow and its risk in USD; null, with one skip row, when the order is refused for now.</summary>
    private (long Volume, double Risk)? RiskAllows(IBrokerGateway session, RiskBook book, Want w, long volume, DateTime now)
    {
        var s = w.Sym;
        var entry = Math.Round(w.Level, s.Digits);
        var sizing = config.SizingInForce;
        if (session.UsdPer(account.AccountId, s.QuoteAsset) is not { } rate || rate <= 0)
        {
            SkipOnce(now, w, "portfolio limit", "ScoutRiskRefused", $"no price converts {s.QuoteAsset} to USD", volume);
            return null;
        }
        var distance = Math.Abs(entry - StopLoss(w)!.Value);
        var perLot = PositionSizer.LossPerLot(s, sizing, entry, distance, session.LastQuote(account.AccountId, s.SymbolId)?.Spread ?? 0, rate);
        var d = book.Decide(s, volume, perLot, sizing.Legs, RiskBook.PauseOf(w.Key), w.Dir);
        if (d.Volume is not { } allowed)
        {
            SkipOnce(now, w, "portfolio limit", "ScoutRiskRefused", d.Refusal!, volume);
            return null;
        }
        if (allowed != volume)
        {
            _sizing[w.Key] = $"{Lots(s, allowed)} lot, ${d.RiskCash.ToString("0.00", CultureInfo.InvariantCulture)} at the stop, all legs (reduced from {Lots(s, volume)} lot by the limit per trade)";
        }
        return (allowed, d.RiskCash);
    }

    private void Persist()
    {
        if (!dryRun)
            state.Save(Now);
    }

    /// <summary>One skip row and one log line for this setup role and reason; the role is not remembered, so it is tried again while the setup lasts.</summary>
    private void SkipOnce(DateTime now, Want w, string result, string logEvent, string why, long? volume = null)
    {
        if (!_skipLogged.Add($"{result}|{w.Key}"))
            return;
        OrderRow(now, w, "skip", 0, StopLoss(w), null, null, result, why, volume);
        _log.Event(logEvent).Information("{Broker} {Symbol} {Tf}: {Side} Stop {Price} not placed: {Why}",
            account.Broker, w.Sym.Name, w.Chart.Chart.Timeframe, Side(w.Dir), Num(w.Level, w.Sym), why);
    }

    /// <summary>The server expiry of a setup's order: <see cref="ScoutConfig.ExpiryCandles"/> candles after the candle that gave its level, however late it is placed.</summary>
    private DateTime ExpiryOf(Want w) => OrderPlanner.Expiry(w.SinceUtc, w.Chart.Length, config.ExpiryCandles);

    /// <summary>
    /// The checks before an order is sent (scout.md §2.3, review F-03, F-10, F-20): a size the broker takes without raising it, candles that are
    /// up to date, a setup still young enough, and a live price that has not already passed the level.
    /// </summary>
    /// <returns>The volume to send, or null when the order is not sent now.</returns>
    private long? ReadyToPlace(IBrokerGateway session, Want w, AccountStatus? status, DateTime now)
    {
        var s = w.Sym;
        if (StopLoss(w) is null)
        {
            SkipOnce(now, w, "no valid stop-loss", "ScoutNoStop", "the far side of the pause is not on the losing side of the level");
            return null;
        }
        if (w.Chart.Main.LastCandleUtc is { } last && now >= CandleDue(last, w.Chart.Length, s.Schedule, Offset(w.Chart)) + w.Chart.Length)
        {
            SkipOnce(now, w, "stale candles", "ScoutStaleCandles", $"last candle {CsvFormat.Time(last)} is more than a candle late");
            return null;
        }
        if (ExpiryOf(w) - now < w.Chart.Length)
        {
            SkipOnce(now, w, "setup too old", "ScoutSetupOld", $"its level appeared at {CsvFormat.Time(w.SinceUtc)} and its expiry would be under one candle away");
            return null;
        }
        if (session.LastQuote(account.AccountId, s.SymbolId) is not { } q || now - q.TimeUtc > QuoteMaxAge)
        {
            SkipOnce(now, w, "no live price", "ScoutNoQuote", "no fresh price from the broker (market closed or no ticks)");
            return null;
        }
        var price = Math.Round(w.Level, s.Digits);
        if (w.Dir == 1 ? q.Ask >= price : q.Bid <= price)
        {
            var seen = w.Dir == 1 ? $"ask {Num(q.Ask, s)}" : $"bid {Num(q.Bid, s)}";
            OrderRow(now, w, "place", 0, StopLoss(w), null, null, "refused", $"LEVEL_PASSED: {seen} is already past the level");
            _log.Event("ScoutLevelPassed").Information("{Broker} {Symbol}: {Side} Stop {Price} not placed: {Seen} is already past it", account.Broker, s.Name, Side(w.Dir), Num(price, s), seen);
            state.Roles[w.Key] = new RoleRecord { FirstSeenUtc = now, State = RoleState.Refused, Result = "refused: level passed" };
            Persist();
            return null;
        }
        return VolumeFor(session, w, q, status, now);
    }

    /// <summary>The order's size: fixed lots, or from the money at risk when sizing is on (scout.md §2.8). Never raised to the broker's minimum.</summary>
    private long? VolumeFor(IBrokerGateway session, Want w, Quote q, AccountStatus? status, DateTime now)
    {
        var s = w.Sym;
        var sizing = config.SizingInForce;
        if (!sizing.Enabled)
        {
            if (s.VolumeNotAbove(config.LotsFor(s.Name)) is { } fixedVolume)
                return fixedVolume;
            SkipOnce(now, w, "size below minimum", "ScoutSizeRefused",
                $"{config.LotsFor(s.Name).ToString("0.###", CultureInfo.InvariantCulture)} lot is below the broker's minimum {Lots(s, s.MinVolume)} lot, which would raise the risk");
            return null;
        }
        if (status is null)
        {
            SkipOnce(now, w, "no account status", "ScoutSizeRefused", "the size needs the account's equity");
            return null;
        }
        var d = PositionSizer.Decide(s, sizing, status.Equity, Math.Round(w.Level, s.Digits), StopLoss(w), q.Spread, session.UsdPer(account.AccountId, s.QuoteAsset));
        if (d.Volume is { } volume)
        {
            _sizing[w.Key] = $"{Lots(s, volume)} lot, ${d.RiskCash.ToString("0.00", CultureInfo.InvariantCulture)} at the stop, all legs";
            return volume;
        }
        SkipOnce(now, w, "size refused", "ScoutSizeRefused", d.Refusal!);
        return null;
    }

    // ── Broker state decides (review P-01): what the reconcile and the broker's history say about each role ─────────────
    private bool ResolveFromReconcile(Dictionary<string, ProtoOAOrder> orders, Dictionary<string, ProtoOAPosition> positions, DateTime now)
    {
        var changed = false;
        foreach (var (key, p) in positions)
        {
            if (!state.Roles.TryGetValue(key, out var r))
            {
                state.Roles[key] = new RoleRecord { FirstSeenUtc = now, PositionId = p.PositionId, State = RoleState.Filled, Result = "filled", PlaceLogged = true, FillLogged = true, LogFromUtc = now };
                notify.Warning("ScoutRoleRebuilt", $"Scout {account.Broker}: position {p.PositionId} was not in memory", $"{key}: rebuilt from its label", account.AccountId);
                changed = true;
            }
            else if (r.PositionId is null)
            {
                MarkFilled(key, r, p.PositionId, r.FillLogged ? null : p.Price, now, "recovered from the broker");
                changed = true;
            }
        }
        foreach (var (key, o) in orders)
        {
            if (!state.Roles.TryGetValue(key, out var r))
            {
                state.Roles[key] = new RoleRecord { FirstSeenUtc = now, OrderId = o.OrderId, State = RoleState.Working, Result = "placed", ExpiresUtc = ExpiryOf(o), PlaceLogged = true, LogFromUtc = now };
                _log.Event("ScoutRoleRebuilt").Warning("{Broker}: order {OrderId} was not in memory; {Key} rebuilt from its label", account.Broker, o.OrderId, key);
                changed = true;
            }
            else if (r.IsInDoubt || r.State == RoleState.Intended)
            {
                (r.OrderId, r.State, r.Result, r.ExpiresUtc) = (o.OrderId, RoleState.Working, "placed", ExpiryOf(o));
                if (!r.PlaceLogged && _symbols.TryGetValue(o.TradeData.SymbolId, out var s))
                {
                    ExecRow(now, s, key.Split('|'), "place", o, null, "", "", "", "", "recovered: no reply to the request, found on the broker");
                    r.PlaceLogged = true;
                }
                _log.Event("ScoutOrderRecovered").Information("{Broker}: order {OrderId} of {Key} found on the broker after a lost reply", account.Broker, o.OrderId, key);
                changed = true;
            }
        }
        foreach (var (key, r) in state.Roles.Where(x => x.Value.IsInDoubt && !orders.ContainsKey(x.Key) && !positions.ContainsKey(x.Key)))
            r.ReconcilesSinceSend++;
        return changed;
    }

    private static DateTime? ExpiryOf(ProtoOAOrder o) => o.HasExpirationTimestamp ? DateTimeOffset.FromUnixTimeMilliseconds(o.ExpirationTimestamp).UtcDateTime : null;

    /// <summary>
    /// Orders and positions that left the account while Scout was not told (a restart, a lost connection, a lost reply): their fate from the
    /// broker's order and deal history, written to orders.csv once. Runs after every (re)connect, and whenever a role is missing from the account.
    /// </summary>
    private async Task<bool> RecoverFromHistoryAsync(IBrokerGateway session, Dictionary<string, ProtoOAOrder> orders, Dictionary<string, ProtoOAPosition> positions, DateTime now)
    {
        var open = state.Roles.Where(x => x.Value.IsOpen).ToList();
        var missing = open.Where(x => !orders.ContainsKey(x.Key) && !positions.ContainsKey(x.Key)).ToList();
        var every = missing.Any(x => x.Value.IsInDoubt) ? RecoverInDoubtEvery : RecoverEvery;
        if (!_recoverDue && (missing.Count == 0 || now - _lastRecover < every))
            return false;
        var targets = _recoverDue ? open : missing;
        if (targets.Count == 0)
        {
            _recoverDue = false;
            return false;
        }

        var earliest = targets.Min(x => x.Value.FirstSeenUtc).AddHours(-1);
        var from = earliest > now - RecoverWindow ? earliest : now - RecoverWindow;
        IReadOnlyList<ProtoOAOrder> history;
        IReadOnlyList<ProtoOADeal> deals;
        try
        {
            history = await session.GetOrdersAsync(account.AccountId, from, now);
            deals = await session.GetDealsAsync(account.AccountId, from, now);
        }
        catch (CTraderApiException ex)
        {
            _lastRecover = now;
            _log.Event("ScoutHistoryRefused").Warning("{Broker}: order history not available ({Message}); asked again later", account.Broker, ex.Message);
            return false;
        }
        (_recoverDue, _lastRecover) = (false, now);

        foreach (var (key, r) in targets)
        {
            var onAccount = orders.ContainsKey(key) || positions.ContainsKey(key);
            var past = history.Where(o => r.OrderId != 0 ? o.OrderId == r.OrderId : KeyOf(o.TradeData.Label, o.TradeData.SymbolId) == key).MaxBy(o => o.OrderId);
            if (!onAccount && past is not null && r.State != RoleState.Filled)
                ApplyHistory(key, r, past, deals, now);

            if (r.IsInDoubt && !onAccount && now - (r.LastSendUtc ?? r.FirstSeenUtc) >= UnknownGrace && r.ReconcilesSinceSend >= 2)
            {
                r.State = r.Attempts < MaxSendAttempts ? RoleState.Intended : RoleState.Abandoned;
                r.Result = r.State == RoleState.Intended ? "not on the broker: may be sent again" : "abandoned: never reached the broker";
                _log.Event("ScoutSendLost").Warning("{Broker} {Key}: the request never reached the broker ({Result})", account.Broker, key, r.Result);
            }
            else if (r.State == RoleState.Working && !onAccount && r.ExpiresUtc is { } expires && expires < now)
                (r.State, r.Result) = (RoleState.Expired, "expired");

            if (r.PositionId is { } pid && _symbols.TryGetValue(SymbolIdOf(key), out var s))
            {
                var closes = deals.Where(d => d.PositionId == pid && d.ClosePositionDetail is not null).ToList();
                foreach (var d in closes)
                    LogClose(key, r, d, s, now, "recovered from the broker's history");
                if (!positions.ContainsKey(key) && (closes.Count > 0 || now - r.FirstSeenUtc > RecoverWindow))
                    r.State = RoleState.Closed;
            }
        }
        return true;
    }

    private long SymbolIdOf(string key) => _symbols.Values.FirstOrDefault(s => s.Name == key.Split('|')[0])?.SymbolId ?? -1;

    private static bool Loggable(RoleRecord r, long timestampMs) =>
        r.LogFromUtc is not { } from || DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime >= from;

    private void ApplyHistory(string key, RoleRecord r, ProtoOAOrder o, IReadOnlyList<ProtoOADeal> deals, DateTime now)
    {
        if (!_symbols.TryGetValue(o.TradeData.SymbolId, out var s))
            return;
        const string Detail = "recovered from the broker's history";
        var parts = key.Split('|');
        var log = Loggable(r, o.UtcLastUpdateTimestamp);
        r.OrderId = o.OrderId;
        switch (o.OrderStatus)
        {
            case ProtoOAOrderStatus.OrderStatusFilled:
                if (deals.Where(d => d.OrderId == o.OrderId && d.ClosePositionDetail is null).MinBy(d => d.ExecutionTimestamp) is { } fill)
                    MarkFilled(key, r, fill.PositionId, r.FillLogged || !Loggable(r, fill.ExecutionTimestamp) ? null : fill.ExecutionPrice, now, Detail, o);
                break;
            case ProtoOAOrderStatus.OrderStatusCancelled when r.State == RoleState.CancelRequested:
                (r.State, r.Result) = (RoleState.Cancelled, "cancelled");
                if (log)
                    ExecRow(now, s, parts, "cancel", o, null, "", "", "", "", Detail);
                break;
            case ProtoOAOrderStatus.OrderStatusCancelled:
                (r.State, r.Result) = (RoleState.CancelledExternally, "cancelled, not by Scout");
                if (log)
                    ExecRow(now, s, parts, "cancelled", o, null, "", "", "", "", $"not by Scout (owner or TM); {Detail}");
                break;
            case ProtoOAOrderStatus.OrderStatusExpired:
                (r.State, r.Result) = (RoleState.Expired, "expired");
                if (log)
                    ExecRow(now, s, parts, "expired", o, null, "", "", "", "", Detail);
                break;
            case ProtoOAOrderStatus.OrderStatusRejected:
                (r.State, r.Result) = (RoleState.Refused, "rejected");
                if (log)
                    ExecRow(now, s, parts, "rejected", o, null, "", "", "", "", Detail);
                break;
        }
    }

    /// <summary>The role has a position. <paramref name="fillPrice"/>: write its filled row, at that price; null when the row exists or must not be written.</summary>
    private void MarkFilled(string key, RoleRecord r, long positionId, double? fillPrice, DateTime now, string detail, ProtoOAOrder? order = null)
    {
        (r.PositionId, r.State, r.Result, r.CancelAttempts, r.NextCancelUtc) = (positionId, RoleState.Filled, "filled", 0, null);
        if (fillPrice is not { } price)
        {
            r.FillLogged = true;
            return;
        }
        if (_symbols.Values.FirstOrDefault(x => x.Name == key.Split('|')[0]) is not { } s)
            return;
        var parts = key.Split('|');
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), s.Name, parts[1], SetupTime(parts[2]), parts[3], "filled", order is null ? "" : SideOf(order.TradeData.TradeSide),
            order is null ? "" : Lots(s, order.TradeData.Volume), order is null ? "" : Num(order.StopPrice, s), order is { HasStopLoss: true } ? Num(order.StopLoss, s) : "", "",
            r.OrderId == 0 ? "" : r.OrderId.ToString(CultureInfo.InvariantCulture), positionId.ToString(CultureInfo.InvariantCulture), Num(price, s), "", "", "", "ok", detail, "",
        ]);
        r.FillLogged = true;
    }

    private async Task CancelDuplicateAsync(IBrokerGateway session, string key, ProtoOAOrder order)
    {
        if (_duplicatesAlerted.Add(order.OrderId))
            notify.Critical("ScoutDuplicateOrder", $"Scout {account.Broker}: two orders for one setup", $"{key}: order {order.OrderId} is the second; cancelling it", account.AccountId);
        if (dryRun)
            return;
        try
        {
            await session.CancelOrderAsync(account.AccountId, order.OrderId, OrderTimeout);
            if (_symbols.TryGetValue(order.TradeData.SymbolId, out var s))
                CancelRow(Now, s, key.Split('|'), order, "ok", "duplicate order of this setup");
        }
        catch (Exception ex) when (ex is TimeoutException or CTraderApiException)
        {
            _log.Event("ScoutCancelRefused").Warning("{Broker}: duplicate order {OrderId} not cancelled yet: {Message}", account.Broker, order.OrderId, ex.Message);
        }
    }

    /// <summary>Every few minutes, new order or not: margin and leverage change while orders wait (review F-24). Warns; it never closes or cancels anything.</summary>
    private async Task<MarginBudget?> StressCheckAsync(IBrokerGateway session, AccountStatus? status, ProtoOAReconcileRes rec, Dictionary<(long, long), ExpectedMargin> margins, DateTime now)
    {
        if (status is null || now < _nextStressCheck)
            return null;
        _nextStressCheck = now + StressCheckEvery;
        var budget = await BudgetAsync(session, status, rec, margins);
        if (budget is { StressBreached: true } && now - _stressAlerted >= TimeSpan.FromHours(1))
        {
            _stressAlerted = now;
            notify.Warning("ScoutMarginStress", $"Scout {account.Broker}: the account would not carry its orders under stress", $"{budget.Describe()}. New orders wait until it would.", account.AccountId);
        }
        return budget;
    }

    /// <summary>The account's money, read before anything that adds exposure; null when the broker did not answer a moment ago.</summary>
    private async Task<AccountStatus?> StatusAsync(IBrokerGateway session, ProtoOAReconcileRes rec)
    {
        if (Now - _statusFailed < TimeSpan.FromMinutes(5))
            return null; // the broker did not answer a moment ago (e.g. a replay broker without it): no new orders meanwhile
        try
        {
            return await session.GetAccountStatusAsync(account.AccountId, rec);
        }
        catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
        {
            _statusFailed = Now;
            _log.Event("ScoutNoAccountStatus").Warning("{Broker}: account status not available ({Message}); no new orders until it is", account.Broker, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// TM mirrors, hedges and protects what Scout places, so without it a fill is not the trade that was meant (scout.md §2.7). Its last report counts for
    /// <see cref="TmGrace"/>, so a restart passes unnoticed. Until TM has been seen once, it counts as down.
    /// </summary>
    private bool TmDown(DateTime now)
    {
        if (!config.RequireTm)
            return false;
        if (_tmHealthyUtc() is { } healthy && healthy > _tmLastHealthy)
            _tmLastHealthy = healthy;
        return now - _tmLastHealthy > TmGrace;
    }

    // ── Stopping new entries (scout.md §2.7): loss limits and the owner's control files. TM is never affected ──────────────
    private string? UpdateHalt(AccountStatus? status, DateTime now)
    {
        if (status is not null)
        {
            var marks = state.Equity ??= new EquityMarks();
            var changed = EntryHalt.Update(marks, status.Equity, now, config.RiskLimitsInForce);
            var reset = Path.Combine(_controlDir, $"scout-reset-{account.Broker}-{account.AccountId}");
            if (File.Exists(reset))
            {
                EntryHalt.Reset(marks, status.Equity, now);
                changed = true;
                try
                {
                    File.Delete(reset);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warning("{Broker}: reset file not removed: {Message}", account.Broker, ex.Message);
                }
                _log.Event("ScoutHaltReset").Warning("{Broker}: loss limits reset by the owner; they count from equity {Equity}", account.Broker, AccountStatus.Money(status.Equity));
            }
            if (changed)
                Persist();
        }

        var owner = File.Exists(Path.Combine(_controlDir, "scout-halt")) || File.Exists(Path.Combine(_controlDir, $"scout-halt-{account.Broker}-{account.AccountId}"));
        var reason = owner ? "stopped by the owner (control file)"
            : state.Equity is { Halt: not LossHalt.None } m ? m.HaltReason
            : TmDown(now) ? "TM is not working on this account"
            : null;
        if (reason != _haltAnnounced)
        {
            if (reason is not null)
                notify.Critical("ScoutHalted", $"Scout {account.Broker}: new entries stopped", $"{reason}. Its pending orders are cancelled; TM keeps managing open positions.", account.AccountId);
            else
                notify.Warning("ScoutResumed", $"Scout {account.Broker}: new entries allowed again", "The halt has ended.", account.AccountId);
            _haltAnnounced = reason;
        }
        return reason;
    }

    // ── Margin (scout.md §2.5, issue-log.md #1): no new order the account could not carry once filled, mirrored and hedged ──
    private async Task<MarginBudget?> BudgetAsync(IBrokerGateway session, AccountStatus? status, ProtoOAReconcileRes rec, Dictionary<(long, long), ExpectedMargin> margins)
    {
        if (status is null)
            return null;
        try
        {
            var stress = config.StressInForce;
            var legs = config.SizingInForce.Legs;
            var budget = new MarginBudget(status.MarginMode, status.Equity, config.MarginLimitInForce, stress, id => _symbols.TryGetValue(id, out var s) && stress.Applies(s.Name));
            foreach (var p in rec.Position)
            {
                budget.AddPosition(p.TradeData.SymbolId, DirOf(p.TradeData.TradeSide), AccountStatus.MarginOf(p));
                if (_symbols.TryGetValue(p.TradeData.SymbolId, out var s) && session.LastQuote(account.AccountId, s.SymbolId) is { } q && session.UsdPer(account.AccountId, s.QuoteAsset) is { } rate)
                    budget.AddStressLoss((stress.SpreadMultiple - 1) * q.Spread * (p.TradeData.Volume / 100.0) * rate);
            }
            foreach (var o in rec.Order)
            {
                var (id, dir, volume, label) = (o.TradeData.SymbolId, DirOf(o.TradeData.TradeSide), o.TradeData.Volume, o.TradeData.Label ?? "");
                if (KeyOf(label, id) is { } key)
                    budget.AddTrade(GroupOf(key), id, dir, (await MarginAsync(session, id, volume * legs, margins)).For(dir));
                else if (label.EndsWith("-HMAIN", StringComparison.Ordinal) || label.EndsWith("-HMIR", StringComparison.Ordinal))
                    budget.AddHedgeOrder(id, dir, (await MarginAsync(session, id, volume, margins)).For(dir));
                else if (!label.EndsWith("-MIR", StringComparison.Ordinal)) // a mirror order is part of its Scout or manual trade
                    budget.AddTrade($"order {o.OrderId}", id, dir, (await MarginAsync(session, id, volume * legs, margins)).For(dir));
            }
            return budget;
        }
        catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
        {
            _statusFailed = Now;
            _log.Event("ScoutNoAccountStatus").Warning("{Broker}: account status not available ({Message}); no new orders until it is", account.Broker, ex.Message);
            return null;
        }
    }

    private static int DirOf(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? 1 : -1;

    // The two orders of one pause share a group: only one can fill.
    private static string GroupOf(string key) => key[..^2];

    private async Task<ExpectedMargin> MarginAsync(IBrokerGateway session, long symbolId, long volume, Dictionary<(long, long), ExpectedMargin> margins)
    {
        if (!margins.TryGetValue((symbolId, volume), out var m))
            margins[(symbolId, volume)] = m = (await session.GetExpectedMarginAsync(account.AccountId, symbolId, [volume])).Single();
        return m;
    }

    private async Task<bool> MarginFitsAsync(IBrokerGateway session, MarginBudget? budget, Want w, long volume, Dictionary<(long, long), ExpectedMargin> margins, DateTime now)
    {
        string why;
        if (budget is null)
            why = "account status not available";
        else
        {
            double margin;
            try
            {
                margin = (await MarginAsync(session, w.Sym.SymbolId, volume * config.SizingInForce.Legs, margins)).For(w.Dir); // asked for the whole trade: leverage tiers fall as size grows
            }
            catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
            {
                margin = double.PositiveInfinity;
                _log.Warning("{Broker} {Symbol}: expected margin not available ({Message})", account.Broker, w.Sym.Name, ex.Message);
            }
            if (budget.TryTake(GroupOf(w.Key), w.Sym.SymbolId, w.Dir, margin))
            {
                _marginSkipped.Remove(w.Key);
                return true;
            }
            why = double.IsPositiveInfinity(margin) ? "expected margin not available" : budget.Explain(GroupOf(w.Key), w.Sym.SymbolId, w.Dir, margin);
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
        return OrderPlanner.StopLoss(w.Dir, Math.Round(w.Level, w.Sym.Digits), w.Far) is { } stop ? Math.Round(stop, w.Sym.Digits) : null;
    }

    private string? KeyOf(string? label, long symbolId) => KeyOf(label, symbolId, _symbols);

    /// <summary>The setup role of a Scout label; null for any other label.</summary>
    internal static string? KeyOf(string? label, long symbolId, IReadOnlyDictionary<long, SymbolInfo> symbols)
    {
        if (!BotIdentity.TryParseLabel(label, out var bot, out _, out var leg) || bot != ScoutConfig.BotName || leg is null)
            return null;
        var parts = leg.Split('-');
        if (parts.Length != 3 || !symbols.TryGetValue(symbolId, out var sym)
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
            state.Roles[w.Key] = new RoleRecord { FirstSeenUtc = now, Result = "alerted", State = RoleState.Alerted };
    }

    private async Task PlaceAsync(IBrokerGateway session, Want w, long volume, DateTime now)
    {
        var s = w.Sym;
        var price = Math.Round(w.Level, s.Digits);
        var sl = StopLoss(w);
        var expires = ExpiryOf(w);
        var leg = $"{w.Chart.Chart.Timeframe}-{w.PauseStart:yyyyMMddHHmm}-{(w.Reversal ? "R" : "M")}";
        var label = identity.Label(leg);
        var comment = $"{CTraderSession.SentComment(now)} Scout {s.Name} {w.Chart.Chart.Timeframe} {(w.Reversal ? "reversal" : "main")} {w.Chart.HigherTimeframe}:{WithHigher(w)}";
        if (dryRun)
        {
            OrderRow(now, w, "place", 0, sl, expires, null, "dry run", label, volume, DrawChart(w.Chart, Picture(w, 0, price, sl, now, expires)));
            _log.Information("{Broker} {Symbol}: would place {Side} Stop {Price} SL {Sl} ({Label})", account.Broker, s.Name, Side(w.Dir), Num(price, s), sl is null ? "none" : Num(sl.Value, s), label);
            return;
        }

        // Saved before it goes out: after a crash or a lost reply the broker's state decides what became of it, and it is never sent blindly again.
        var record = state.Roles.GetValueOrDefault(w.Key) ?? new RoleRecord { FirstSeenUtc = now };
        record.State = RoleState.Sending;
        record.Attempts++;
        (record.LastSendUtc, record.ReconcilesSinceSend, record.ExpiresUtc) = (now, 0, expires);
        state.Roles[w.Key] = record;
        Persist();
        try
        {
            var order = await session.PlaceStopOrderAsync(account.AccountId, s.SymbolId, w.Dir == 1 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell, volume, price, sl,
                label, ClientId(w.Key, record.Attempts), comment, OrderTimeout, sl is not null, expires);
            record.OrderId = order.OrderId;
            record.State = RoleState.Working;
            record.Result = "placed";
            record.PlaceLogged = true;
            record.Chart = DrawChart(w.Chart, Picture(w, order.OrderId, price, sl, now, expires));
            OrderRow(now, w, "place", order.OrderId, sl, expires, null, "ok", label, volume, record.Chart);
            Persist();
            if (_sizing.Remove(w.Key, out var sized))
                _log.Event("ScoutSizing").Information("{Broker} {Symbol}: {Sizing}", account.Broker, s.Name, sized);
            _log.Event("ScoutOrder").Information("{Broker} {Symbol} {Tf}: placed {Side} Stop {Price} SL {Sl} {Lots} lot, expires {Expires:HH:mm}Z, order {OrderId} ({Label})",
                account.Broker, s.Name, w.Chart.Chart.Timeframe, Side(w.Dir), Num(price, s), sl is null ? "none" : Num(sl.Value, s), Lots(s, volume), expires, order.OrderId, label);
            notify.Info("OrderPlaced", $"Scout: {s.Name} {w.Chart.Chart.Timeframe} {Side(w.Dir)} Stop {Num(price, s)}",
                $"{(w.Reversal ? "Reversal stop; " : "")}SL {(sl is null ? "none" : Num(sl.Value, s))}, {Lots(s, volume)} lot; {w.Chart.HigherTimeframe} {WithHigher(w)}. TM takes it over.", account.AccountId);
        }
        catch (CTraderApiException ex)
        {
            var again = IsTemporary(ex) && record.Attempts < MaxSendAttempts;
            record.State = again ? RoleState.Intended : RoleState.Refused;
            record.Result = $"refused: {ex.ErrorCode}";
            OrderRow(now, w, "place", 0, sl, expires, null, "refused", $"{ex.ErrorCode}: {ex.Message}", volume);
            Persist();
            _log.Event("ScoutOrderRefused").Warning("{Broker} {Symbol}: {Side} Stop {Price} refused: {Error}", account.Broker, s.Name, Side(w.Dir), Num(price, s), ex.Message);
            notify.Warning("OrderRefused", $"Scout: {s.Name} {Side(w.Dir)} Stop {Num(price, s)} refused", ex.Message, account.AccountId);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            record.State = RoleState.Unknown;
            record.Result = "unknown: no reply from the broker";
            OrderRow(now, w, "place", 0, sl, expires, null, "unknown", ex.Message, volume);
            Persist();
            notify.Warning("ScoutSendUnknown", $"Scout: no reply placing {s.Name} {Side(w.Dir)} Stop {Num(price, s)}", "Scout checks the broker before doing anything else with this setup.", account.AccountId);
            throw;
        }
    }

    // Refusals that say nothing about the order itself: it may be sent again, once. Lack of money comes back through the margin budget.
    private static bool IsTemporary(CTraderApiException ex) =>
        ex.IsTokenProblem || ex.ErrorCode is "CANT_ROUTE_REQUEST" or "UNKNOWN_ERROR" or "TIMEOUT_ERROR" or "REQUEST_FREQUENCY_EXCEEDED" or "NOT_ENOUGH_MONEY";

    /// <returns>True when the broker confirmed the cancel.</returns>
    private async Task<bool> CancelAsync(IBrokerGateway session, string key, ProtoOAOrder order, string reason)
    {
        if (!_symbols.TryGetValue(order.TradeData.SymbolId, out var s))
            return false;
        var parts = key.Split('|');
        var now = Now;
        if (dryRun)
        {
            _log.Information("{Broker} {Symbol}: would cancel order {OrderId} ({Reason})", account.Broker, s.Name, order.OrderId, reason);
            return false;
        }
        if (!state.Roles.TryGetValue(key, out var role))
            state.Roles[key] = role = new RoleRecord { FirstSeenUtc = now, OrderId = order.OrderId, State = RoleState.Working, Result = "placed", PlaceLogged = true, LogFromUtc = now };
        if (role.State == RoleState.CancelRequested && role.NextCancelUtc > now)
            return false;

        role.OrderId = order.OrderId;
        role.State = RoleState.CancelRequested;
        role.CancelAttempts++;
        role.NextCancelUtc = now + CancelBackoff[Math.Min(role.CancelAttempts, CancelBackoff.Length) - 1];
        Persist();
        try
        {
            await session.CancelOrderAsync(account.AccountId, order.OrderId, OrderTimeout);
            (role.State, role.Result, role.NextCancelUtc) = (RoleState.Cancelled, "cancelled", null);
            Persist();
            CancelRow(now, s, parts, order, "ok", reason);
            _log.Event("ScoutCancel").Information("{Broker} {Symbol}: cancelled order {OrderId} {Side} Stop {Price} — {Reason}",
                account.Broker, s.Name, order.OrderId, SideOf(order.TradeData.TradeSide), Num(order.StopPrice, s), reason);
            return true;
        }
        catch (CTraderApiException ex)
        {
            CancelRow(now, s, parts, order, $"refused: {ex.ErrorCode}", reason);
            _log.Event("ScoutCancelRefused").Warning("{Broker} {Symbol}: cancel of order {OrderId} refused: {Error}; trying again", account.Broker, s.Name, order.OrderId, ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            CancelRow(now, s, parts, order, "no reply", reason);
            _log.Event("ScoutCancelRefused").Warning("{Broker} {Symbol}: no reply cancelling order {OrderId}: {Error}; checking the broker", account.Broker, s.Name, order.OrderId, ex.Message);
            AlertCancelFailing(role, s, order, reason);
            Persist();
            if (ex is IOException)
                throw;
            return false;
        }
        AlertCancelFailing(role, s, order, reason);
        Persist();
        return false;
    }

    private void AlertCancelFailing(RoleRecord role, SymbolInfo s, ProtoOAOrder order, string reason)
    {
        if (role.CancelAttempts < 3 || role.CancelAlerted)
            return;
        role.CancelAlerted = true;
        notify.Critical("ScoutCancelFailing", $"Scout {account.Broker}: order {order.OrderId} on {s.Name} cannot be cancelled",
            $"{role.CancelAttempts} attempts failed ({reason}). It stays live until it fills or expires; Scout keeps trying.", account.AccountId);
    }

    private void CancelRow(DateTime now, SymbolInfo s, string[] parts, ProtoOAOrder order, string result, string reason) =>
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), s.Name, parts[1], SetupTime(parts[2]), parts[3], "cancel", SideOf(order.TradeData.TradeSide), Lots(s, order.TradeData.Volume),
            Num(order.StopPrice, s), order.HasStopLoss ? Num(order.StopLoss, s) : "", "", order.OrderId.ToString(CultureInfo.InvariantCulture), "", "", "", "", "", result, reason, "",
        ]);

    /// <summary>The control entry that made Scout cancel this setup role's order: the symbol switched off, else the setup skipped.</summary>
    private ControlEntry? ControlEntryFor(string roleKey)
    {
        var symbol = roleKey.Split('|')[0];
        return _controls.Disabled.Contains(symbol)
            ? _controls.Entries.FirstOrDefault(e => e is { Scope: "symbol", Key: "enabled", Applied: true } && e.Target == symbol)
            : _controls.Entries.FirstOrDefault(e => e is { Scope: "setup", Applied: true } && e.Target == Controls.SetupOf(roleKey));
    }

    // ── Execution events: fills, expiries and closes of what Scout started (for the review, Scout spec §4.6) ────────
    /// <returns>True when the memory changed.</returns>
    private bool HandleExecutions()
    {
        var changed = false;
        while (_executions.TryDequeue(out var e))
        {
            var now = Now;
            if (e.Order is { } o && KeyOf(o.TradeData.Label, o.TradeData.SymbolId) is { } key && _symbols.TryGetValue(o.TradeData.SymbolId, out var s))
            {
                var parts = key.Split('|');
                state.Roles.TryGetValue(key, out var r);
                var same = r is not null && (r.OrderId == 0 || r.OrderId == o.OrderId);
                switch (e.ExecutionType)
                {
                    case ProtoOAExecutionType.OrderFilled when o.OrderType == ProtoOAOrderType.Stop && e.Position is not null && e.Deal is not null:
                        if (r is { FillLogged: true } && r.PositionId == e.Position.PositionId)
                            break; // the same event again
                        r ??= state.Roles[key] = new RoleRecord { FirstSeenUtc = now, PlaceLogged = true, LogFromUtc = now };
                        var wasCancelling = r.State == RoleState.CancelRequested;
                        r.OrderId = o.OrderId;
                        (r.PositionId, r.State, r.Result, r.FillLogged, r.NextCancelUtc) = (e.Position.PositionId, RoleState.Filled, "filled", true, null);
                        changed = true;
                        var slip = (e.Deal.ExecutionPrice - o.StopPrice) * (o.TradeData.TradeSide == ProtoOATradeSide.Buy ? 1 : -1) / s.PipSize;
                        ExecRow(now, s, parts, "filled", o, e.Position.PositionId, Num(e.Deal.ExecutionPrice, s), CsvFormat.Num(slip, 1), "", "", "");
                        _log.Event("ScoutFilled").Information("{Broker} {Symbol}: order {OrderId} filled at {Price} (slip {Slip:F1} pips) → position {PositionId}",
                            account.Broker, s.Name, o.OrderId, Num(e.Deal.ExecutionPrice, s), slip, e.Position.PositionId);
                        if (wasCancelling)
                            notify.Warning("ScoutFilledDuringCancel", $"Scout {account.Broker}: order {o.OrderId} on {s.Name} filled while being cancelled", "TM manages the position.", account.AccountId);
                        _dirty = true; // the other order of the pair is cancelled in the next sync
                        break;
                    case ProtoOAExecutionType.OrderExpired:
                        if (same && r!.State == RoleState.Expired)
                            break;
                        ExecRow(now, s, parts, "expired", o, null, "", "", "", "", "");
                        if (same)
                        {
                            (r!.State, r.Result) = (RoleState.Expired, "expired");
                            changed = true;
                        }
                        break;
                    case ProtoOAExecutionType.OrderCancelled when o.OrderType == ProtoOAOrderType.Stop:
                        if (r is null)
                            ExecRow(now, s, parts, "cancelled", o, null, "", "", "", "", "not by Scout (owner or TM)");
                        else if (!same || r.State is RoleState.Cancelled or RoleState.Held or RoleState.CancelledExternally)
                            break; // Scout's own cancel, already written (or the second order of a duplicate)
                        else if (r.State == RoleState.CancelRequested)
                        {
                            (r.State, r.Result, r.NextCancelUtc) = (RoleState.Cancelled, "cancelled", null);
                            ExecRow(now, s, parts, "cancel", o, null, "", "", "", "", "confirmed by the broker after no reply");
                            changed = true;
                        }
                        else
                        {
                            (r.State, r.Result) = (RoleState.CancelledExternally, "cancelled, not by Scout");
                            ExecRow(now, s, parts, "cancelled", o, null, "", "", "", "", "not by Scout (owner or TM)");
                            changed = true;
                        }
                        break;
                    case ProtoOAExecutionType.OrderRejected:
                        if (same && r!.State == RoleState.Refused)
                            break; // the refusal PlaceAsync already wrote, or the same event again
                        ExecRow(now, s, parts, "rejected", o, null, "", "", "", "", e.ErrorCode ?? "");
                        if (same)
                        {
                            (r!.State, r.Result) = (RoleState.Refused, $"rejected: {e.ErrorCode}");
                            changed = true;
                        }
                        break;
                }
            }

            // A deal that closes (part of) a position that came from Scout's order: its result.
            if (e.Deal is { ClosePositionDetail: not null } deal && state.Roles.FirstOrDefault(x => x.Value.PositionId == deal.PositionId) is { Key: { } pk, Value: { } role }
                && _symbols.TryGetValue(deal.SymbolId, out var ps))
            {
                changed |= LogClose(pk, role, deal, ps, now, "");
                if (e.Position is { PositionStatus: ProtoOAPositionStatus.PositionStatusClosed })
                {
                    role.State = RoleState.Closed;
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>One orders.csv row per closing deal, once, however many times the deal is seen.</summary>
    /// <returns>True when a new row was written.</returns>
    private bool LogClose(string key, RoleRecord role, ProtoOADeal deal, SymbolInfo ps, DateTime now, string detail)
    {
        if (role.LoggedDeals.Contains(deal.DealId) || !Loggable(role, deal.ExecutionTimestamp) || deal.ClosePositionDetail is not { } close)
            return false;
        role.LoggedDeals.Add(deal.DealId);
        var digits = close.HasMoneyDigits ? (int)close.MoneyDigits : 2;
        double Money(long v) => v / Math.Pow(10, digits);
        var net = Money(close.GrossProfit) + Money(close.Swap) + Money(close.Commission);
        var parts = key.Split('|');
        ordersCsv.Write(now,
        [
            CsvFormat.Time(now), ps.Name, parts[1], SetupTime(parts[2]), parts[3], "closed", SideOf(deal.TradeSide == ProtoOATradeSide.Buy ? ProtoOATradeSide.Sell : ProtoOATradeSide.Buy),
            Lots(ps, deal.FilledVolume), "", "", "", deal.OrderId.ToString(CultureInfo.InvariantCulture), deal.PositionId.ToString(CultureInfo.InvariantCulture),
            Num(deal.ExecutionPrice, ps), "", CsvFormat.Num(Money(close.GrossProfit), digits), CsvFormat.Num(net, digits), "ok", detail, "",
        ]);
        _log.Event("ScoutClosed").Information("{Broker} {Symbol}: position {PositionId} closed {Lots} lot at {Price}, gross {Gross}, net {Net}",
            account.Broker, ps.Name, deal.PositionId, Lots(ps, deal.FilledVolume), Num(deal.ExecutionPrice, ps), Money(close.GrossProfit), net);
        return true;
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
            var now = _utcNow();
            ordersCsv.Write(now,
            [
                CsvFormat.Time(now), s.Name, o.Timeframe, CsvFormat.Time(o.SetupUtc), o.Reversal ? "R" : "M", "chart", Side(o.Dir), "", Num(o.Price, s),
                o.StopLoss is { } sl ? Num(sl, s) : "", o.ExpiresUtc is { } ex ? CsvFormat.Time(ex) : "", o.OrderId.ToString(CultureInfo.InvariantCulture),
                record.PositionId?.ToString(CultureInfo.InvariantCulture) ?? "", "", "", "", "", "ok", $"drawn later; placed {CsvFormat.Time(o.TimeUtc)}", path,
            ]);
        }
        _log.Event("ScoutCharts").Information("{Broker}: drew {Drawn} of {Missing} missing order picture(s)", account.Broker, drawn, missing.Count);
        if (drawn > 0 && !dryRun)
            state.Save(_utcNow());
    }

    private static string WithHigher(Want w) => TaggedEvent.Compare(w.Chart.Higher?.Trend ?? 0, w.Dir);

    private static string SetupTime(string compact) =>
        DateTime.TryParseExact(compact, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? CsvFormat.Time(t) : compact;

    // The same setup role and attempt always give the same ID: it links a request to its order. The label stays the key that prevents duplicates.
    private static string ClientId(string key, int attempt) =>
        $"s{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant()}-{attempt}";
    private static string Side(int dir) => dir == 1 ? "Buy" : dir == -1 ? "Sell" : "";
    private static string SideOf(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? "Buy" : "Sell";
    private static string TrendName(int t) => t == 1 ? "up" : t == -1 ? "down" : "unknown";
    private static string Num(double v, SymbolInfo s) => CsvFormat.Num(v, s.Digits);
    private static string Lots(SymbolInfo s, long volume) => CsvFormat.Num(s.LotsForVolume(volume), 2);
}
