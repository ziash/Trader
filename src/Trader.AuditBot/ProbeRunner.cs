using System.Collections.Concurrent;
using Serilog;
using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.AuditBot;

/// <summary>
/// Probe orders (audit-bot.md §2, §2.2, step 5): in each window, for each symbol, one 0.01-lot buy and one sell,
/// each opened at market and closed again at once; both the open and the close are written to probes.csv.
///
/// Safety (open-code-pieces.md §4, audit-bot.md §4): only while this instance is Primary on the account; one probe open at a time per
/// account; every probe carries the bot's label and a wide server-side backup stop-loss; positions are closed
/// only by their own positionId. Left-over AuditBot positions (e.g. after a crash) are closed on connect, on
/// becoming Primary, and before every window. Positions without the AuditBot label are never touched.
/// </summary>
public sealed class ProbeRunner(
    ILogger log,
    BotIdentity identity,
    InstanceRegistry registry,
    PriceFeed feed,
    SpreadRecorder market,
    ProbeSchedule schedule,
    IReadOnlyList<AuditConfig> configs,
    IReadOnlyDictionary<long, DailyCsvWriter> writers,
    Func<DateTime>? utcNow = null)
{
    public const double Lots = 0.01;
    public const double BackstopForexPips = 50;
    public const double BackstopOtherPercent = 2;

    public static readonly string[] Header =
    [
        "time_utc", "window", "action", "symbol", "side", "lots", "req_px", "fill_px",
        "slip_pips", "slip_usd", "latency_ms", "commission_usd", "result", "reason", "position_id", "label",
    ];

    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PauseBetweenProbes = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlotOffset = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxLateness = TimeSpan.FromSeconds(60);
    private const int CloseAttempts = 3;

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<long, int> _foreignCount = new();
    private volatile CTraderSession? _session;

    /// <summary>The connection to use; set after every (re)connect.</summary>
    public void SetSession(CTraderSession session) => _session = session;

    /// <summary>Closes left-over AuditBot positions on an account in the background (never throws).</summary>
    public void CleanupInBackground(long ctid, string reason) =>
        _ = Task.Run(async () =>
        {
            if (registry.RoleOn(ctid) != InstanceRole.Primary || _session is not { } session)
                return;
            var gate = Gate(ctid);
            await gate.WaitAsync();
            try
            {
                await CleanupAsync(session, ctid, reason);
            }
            catch (Exception ex)
            {
                log.Event("CleanupFailed").ForAccount(ctid).Warning("Clean-up of left-over probes failed ({Message}); will retry before the next window", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

    /// <summary>Runs the probe windows until cancelled. With <paramref name="probeNow"/>, first runs one "Test" window at once.</summary>
    public async Task RunAsync(CancellationToken ct, bool probeNow = false)
    {
        if (probeNow)
        {
            await WaitUntilPrimaryAndConnected(ct);
            await RunWindowAsync(new ProbeWindow("Test", _utcNow()), ct);
        }

        var after = _utcNow();
        while (true)
        {
            if (schedule.Next(after) is not { } window)
            {
                await Task.Delay(TimeSpan.FromHours(1), ct);
                after = _utcNow();
                continue;
            }

            log.Event("NextProbeWindow").Information("Next probe window: {Window}{Detail} at {Start:yyyy-MM-dd HH:mm:ss}Z",
                window.Name, window.Detail is null ? "" : $" ({window.Detail})", window.StartUtc);
            await DelayUntil(window.StartUtc, ct);

            if (_utcNow() - window.StartUtc > MaxLateness)
                log.Event("ProbeWindowMissed").Warning("Probe window {Window} at {Start:HH:mm:ss}Z missed (bot was busy or the computer slept)", window.Name, window.StartUtc);
            else
                await RunWindowAsync(window, ct);
            after = window.StartUtc;
        }
    }

    /// <summary>Runs one window on every account where this instance is Primary (accounts in parallel).</summary>
    public async Task RunWindowAsync(ProbeWindow window, CancellationToken ct)
    {
        log.Event("ProbeWindow").Information("Probe window {Window}{Detail} starting", window.Name, window.Detail is null ? "" : $" ({window.Detail})");
        await Task.WhenAll(configs.Select(c => ProbeAccountAsync(c, window, ct)));
    }

    private async Task ProbeAccountAsync(AuditConfig c, ProbeWindow window, CancellationToken ct)
    {
        var alog = log.ForAccount(c.AccountId);
        var view = registry.View(c.AccountId);
        if (view?.Role != InstanceRole.Primary)
        {
            alog.Event("ProbeSkipped").Information("{Broker} {Window}: this instance is Passive; the Primary sends the probes", c.Broker, window.Name);
            return;
        }
        if (_session is not { } session || session.Closed.IsCompleted)
        {
            alog.Event("ProbeSkipped").Warning("{Broker} {Window}: not connected; window skipped", c.Broker, window.Name);
            return;
        }

        await Task.Delay(SlotOffset * view.Slot, ct); // stagger instances (audit-bot.md §4)
        var gate = Gate(c.AccountId);
        await gate.WaitAsync(ct);
        var results = new List<ProbeResult>();
        try
        {
            await CleanupAsync(session, c.AccountId, "before window");
            foreach (var symbol in market.SymbolsOf(c.AccountId))
            {
                if (!symbol.Schedule.IsOpen(_utcNow()))
                {
                    alog.Debug("{Broker} {Window}: {Symbol} market closed; skipped", c.Broker, window.Name, symbol.Name);
                    continue;
                }
                foreach (var side in new[] { ProtoOATradeSide.Buy, ProtoOATradeSide.Sell })
                {
                    ct.ThrowIfCancellationRequested();
                    results.AddRange(await ProbeOnceAsync(session, c, symbol, side, window));
                    await Task.Delay(PauseBetweenProbes, ct);
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
        {
            alog.Event("ProbeWindowError").Warning("{Broker} {Window}: stopped early ({Message})", c.Broker, window.Name, ex.Message);
        }
        finally
        {
            gate.Release();
        }

        var filled = results.Where(r => r.Result == "filled").ToList();
        alog.Event("ProbeWindowDone").Information(
            "{Broker} {Window}: {Filled} filled, {Failed} not filled; latency avg {Latency:0} ms, max {MaxLatency:0} ms",
            c.Broker, window.Name, filled.Count, results.Count - filled.Count,
            filled.Count > 0 ? filled.Average(r => r.LatencyMs) : 0, filled.Count > 0 ? filled.Max(r => r.LatencyMs) : 0);
        foreach (var line in SlippageSummary(filled))
            alog.Event("ProbeSlippage").Information("{Broker} {Window} {Line}", c.Broker, window.Name, line);
    }

    /// <summary>
    /// One result per order sent. <see cref="Slip"/> is in the symbol's alert unit: pips for forex, USD per lot
    /// for everything else (a crypto, metal or index "pip" can differ by broker, so pips cannot be compared or averaged across symbols).
    /// </summary>
    internal sealed record ProbeResult(string Result, double LatencyMs, string Symbol, string Action, string Side, Limit Limit, double? Slip)
    {
        public bool OverLimit => Slip < -Limit.Slip;
    }

    /// <summary>
    /// One line for forex (pips) and one for everything else (USD per lot): average, the worst fill by name,
    /// and how many fills were worse than their alert limit. Averages alone would hide one bad fill.
    /// </summary>
    internal static IEnumerable<string> SlippageSummary(IReadOnlyList<ProbeResult> filled)
    {
        foreach (var group in filled.Where(r => r.Slip is not null).GroupBy(r => r.Limit.InPips).OrderByDescending(g => g.Key))
        {
            var unit = group.First().Limit;
            var worst = group.MinBy(r => r.Slip!.Value)!;
            var worstText = worst.Slip < 0
                ? $"worst {unit.ShowSigned(worst.Slip!.Value)} ({worst.Symbol} {worst.Action} {worst.Side})"
                : "no fill worse than requested";
            yield return $"{(group.Key ? "forex" : "non-forex")}: slippage avg {unit.ShowSigned(group.Average(r => r.Slip!.Value))}, {worstText}; " +
                         $"{group.Count(r => r.OverLimit)} of {group.Count()} fills over the alert limit";
        }
    }

    /// <summary>One probe: open at market, then close by positionId. Returns one result per order sent.</summary>
    private async Task<List<ProbeResult>> ProbeOnceAsync(CTraderSession session, AuditConfig c, SymbolInfo symbol, ProtoOATradeSide side, ProbeWindow window)
    {
        var results = new List<ProbeResult>();
        if (feed.Last(c.AccountId, symbol.SymbolId) is not { } quote)
        {
            log.ForAccount(c.AccountId).Event("ProbeSkipped").Warning("{Broker} {Window}: no price yet for {Symbol}; skipped", c.Broker, window.Name, symbol.Name);
            return results;
        }

        var label = identity.Label();
        var volume = symbol.VolumeForLots(Lots);
        var requested = side == ProtoOATradeSide.Buy ? quote.Ask : quote.Bid;
        var sent = _utcNow();
        OrderFill open;
        try
        {
            open = await session.PlaceMarketOrderAsync(c.AccountId, symbol.SymbolId, side, volume, label, NewClientOrderId(),
                Backstop(symbol, requested), OrderTimeout, CTraderSession.SentComment(sent));
        }
        catch (CTraderApiException ex)
        {
            results.Add(Write(c, window, "open", symbol, side, volume, requested, null, sent, ex.ErrorCode));
            return results;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            results.Add(Write(c, window, "open", symbol, side, volume, requested, null, sent, ex is TimeoutException ? "TIMEOUT" : "DISCONNECTED"));
            throw; // the order may exist: stop this window; clean-up runs on reconnect / before the next window
        }
        results.Add(Write(c, window, "open", symbol, side, volume, requested, open, sent, null));

        // Close by positionId only (ownership rule). Closing a buy is a sell at the bid, and vice versa.
        var closeSide = side == ProtoOATradeSide.Buy ? ProtoOATradeSide.Sell : ProtoOATradeSide.Buy;
        for (var attempt = 1; attempt <= CloseAttempts; attempt++)
        {
            var now = feed.Last(c.AccountId, symbol.SymbolId) ?? quote;
            var closeRequested = closeSide == ProtoOATradeSide.Buy ? now.Ask : now.Bid;
            var closeSent = _utcNow();
            try
            {
                var close = await session.ClosePositionAsync(c.AccountId, open.PositionId, open.FilledVolume, OrderTimeout);
                results.Add(Write(c, window, "close", symbol, closeSide, open.FilledVolume, closeRequested, close, closeSent, null, open.PositionId, open.Label));
                return results;
            }
            catch (CTraderApiException ex)
            {
                results.Add(Write(c, window, "close", symbol, closeSide, open.FilledVolume, closeRequested, null, closeSent, ex.ErrorCode, open.PositionId, open.Label));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                results.Add(Write(c, window, "close", symbol, closeSide, open.FilledVolume, closeRequested, null, closeSent, ex is TimeoutException ? "TIMEOUT" : "DISCONNECTED", open.PositionId, open.Label));
                if (ex is IOException)
                    break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        log.ForAccount(c.AccountId).Event("ProbeCloseFailed").Error(
            "{Broker}: could not close probe position {Position} ({Symbol}); its backup stop-loss stays on the server; clean-up will retry",
            c.Broker, open.PositionId, symbol.Name);
        return results;
    }

    /// <summary>Closes every open position on the account whose label says it was opened by this bot (any instance).</summary>
    private async Task CleanupAsync(CTraderSession session, long ctid, string reason)
    {
        var alog = log.ForAccount(ctid);
        var state = await session.ReconcileAsync(ctid);
        var foreign = 0;
        foreach (var p in state.Position)
        {
            var label = p.TradeData?.Label;
            if (!BotIdentity.TryParseLabel(label, out var bot, out _, out _) || bot != identity.BotName)
            {
                foreign++;
                continue;
            }

            try
            {
                await session.ClosePositionAsync(ctid, p.PositionId, p.TradeData!.Volume, OrderTimeout);
                alog.Event("OrphanClosed").Warning("Closed left-over probe position {Position} (label {Label}, symbol {SymbolId}) — {Reason}",
                    p.PositionId, label, p.TradeData.SymbolId, reason);
            }
            catch (Exception ex) when (ex is CTraderApiException or TimeoutException)
            {
                alog.Event("OrphanCloseFailed").Error("Could not close left-over probe position {Position} ({Message})", p.PositionId, ex.Message);
            }
        }

        if (_foreignCount.GetValueOrDefault(ctid, -1) != foreign)
        {
            _foreignCount[ctid] = foreign;
            alog.Event("ForeignPositions").Information("Account {Ctid}: {Count} open position(s) not opened by {BotName} — left untouched (ownership rule)",
                ctid, foreign, identity.BotName);
        }
    }

    private ProbeResult Write(AuditConfig c, ProbeWindow window, string action, SymbolInfo symbol, ProtoOATradeSide side, long volume,
        double requested, OrderFill? fill, DateTime sent, string? error, long? positionId = null, string? label = null)
    {
        double? slipPrice = fill is null ? null : Slippage(side, requested, fill.Price);
        var usd = market.UsdPerPriceUnit(c.AccountId, symbol) * symbol.LotUnits;
        var result = fill is not null ? "filled" : error is "TIMEOUT" or "DISCONNECTED" ? "error" : "rejected";
        var sideText = side == ProtoOATradeSide.Buy ? "buy" : "sell";

        writers[c.AccountId].Write(sent,
        [
            CsvFormat.Time(sent), window.Name, action, symbol.Name, sideText,
            CsvFormat.Num(symbol.LotsForVolume(volume), 2),
            CsvFormat.Num(requested, symbol.Digits), CsvFormat.Num(fill?.Price, symbol.Digits),
            CsvFormat.Num(slipPrice / symbol.PipSize, 2), CsvFormat.Num(slipPrice * usd, 2),
            CsvFormat.Num(fill?.Latency.TotalMilliseconds, 0), CsvFormat.Num(fill is null ? null : -fill.Commission, 2),
            result, error ?? "", fill is null ? (positionId is { } p ? CsvFormat.Int(p) : "") : CsvFormat.Int(fill.PositionId),
            fill?.Label ?? label ?? identity.Label(),
        ]);

        var limit = c.Limits.For(symbol);
        var probe = new ProbeResult(result, fill?.Latency.TotalMilliseconds ?? 0, symbol.Name, action, sideText, limit,
            limit.InPips ? slipPrice / symbol.PipSize : slipPrice * usd);
        if (probe.OverLimit)
            log.ForAccount(c.AccountId).Event("SlippageHigh").Warning(
                "{Broker} {Window}: {Symbol} {Action} {Side} filled {Slip} worse than requested (limit {Limit}); requested {Requested}, filled {Filled}",
                c.Broker, window.Name, symbol.Name, action, sideText, limit.Show(-probe.Slip!.Value), limit.Show(limit.Slip),
                CsvFormat.Num(requested, symbol.Digits), CsvFormat.Num(fill!.Price, symbol.Digits));
        return probe;
    }

    /// <summary>Requested minus filled price, signed so that positive = better for us, negative = worse.</summary>
    public static double Slippage(ProtoOATradeSide side, double requested, double filled) =>
        side == ProtoOATradeSide.Buy ? requested - filled : filled - requested;

    /// <summary>Backup stop-loss distance (1/100,000 price units): 50 pips for forex, 2 % of the price otherwise.</summary>
    public static long Backstop(SymbolInfo symbol, double price) =>
        symbol.RelativeDistance(symbol.IsForex
            ? BackstopForexPips * symbol.PipSize
            : price * BackstopOtherPercent / 100);

    private static string NewClientOrderId() => "probe-" + Guid.NewGuid().ToString("N")[..12];

    private SemaphoreSlim Gate(long ctid) => _gates.GetOrAdd(ctid, _ => new SemaphoreSlim(1, 1));

    private async Task WaitUntilPrimaryAndConnected(CancellationToken ct)
    {
        // Ready = connected, Primary somewhere, and every account where we are Primary has its symbols and prices.
        bool Ready() =>
            _session is not null &&
            configs.Any(c => registry.RoleOn(c.AccountId) == InstanceRole.Primary) &&
            configs.Where(c => registry.RoleOn(c.AccountId) == InstanceRole.Primary)
                .All(c => market.SymbolsOf(c.AccountId) is { Count: > 0 } symbols &&
                          symbols.Any(s => feed.Last(c.AccountId, s.SymbolId) is not null));

        var deadline = _utcNow().AddSeconds(90);
        while (_utcNow() < deadline && !Ready())
            await Task.Delay(500, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct); // let first prices arrive
    }

    private async Task DelayUntil(DateTime utc, CancellationToken ct)
    {
        var wait = utc - _utcNow();
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }
}
