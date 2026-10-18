using Serilog;
using Trader.Core.Csv;
using Trader.Core.Logging;
using Trader.Core.MarketData;

namespace Trader.AuditBot;

/// <summary>A currency conversion resolved to a subscribed symbol.</summary>
public sealed record ConversionSymbol(SymbolInfo Symbol, bool Invert);

/// <summary>
/// Collects quotes per account and symbol and writes one spreads.csv row per symbol per interval
/// (plan 6.2.3, 6.3.1): every 2 min in main hours, every 5 min off-peak.
///
/// A row is skipped when there were no ticks and the market was closed during the interval (weekend,
/// daily break). No ticks while the market was open is written as a row with ticks = 0 — a feed gap.
/// Ticks are placed in intervals by the server's timestamp; each interval is written 1 s after it ends
/// so that late messages still count. Ticks stamped before the interval (the stale last price the server
/// sends on each subscribe) are ignored.
/// </summary>
public sealed class SpreadRecorder(ILogger log, PriceFeed feed, Func<DateTime>? utcNow = null)
{
    public static readonly string[] Header =
    [
        "time_utc", "interval_s", "session", "symbol", "ticks", "bid", "ask",
        "spread_min_pips", "spread_avg_pips", "spread_median_pips", "spread_p95_pips", "spread_max_pips",
        "spread_avg_usd", "spread_max_usd", "max_gap_s",
    ];

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Account> _accounts = new();
    private readonly TaskCompletionSource _firstConfigured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _rowsWritten;

    private sealed class Account(AuditConfig config, DailyCsvWriter writer)
    {
        public AuditConfig Config { get; } = config;
        public DailyCsvWriter Writer { get; } = writer;
        public Dictionary<long, SymbolInfo> Symbols { get; set; } = new();
        public Dictionary<string, ConversionSymbol> Conversions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<long, List<Tick>> Ticks { get; } = new();
    }

    public void AddAccount(AuditConfig config, DailyCsvWriter writer)
    {
        lock (_lock)
            _accounts[config.AccountId] = new Account(config, writer);
    }

    /// <summary>Sets the symbols to record for an account (after each connect, once they are resolved).</summary>
    public void Configure(long ctid, IEnumerable<SymbolInfo> symbols, IReadOnlyDictionary<string, ConversionSymbol> conversions)
    {
        lock (_lock)
        {
            var acc = _accounts[ctid];
            acc.Symbols = symbols.ToDictionary(s => s.SymbolId);
            acc.Conversions = new Dictionary<string, ConversionSymbol>(conversions, StringComparer.OrdinalIgnoreCase);
            foreach (var id in acc.Symbols.Keys)
                acc.Ticks.TryAdd(id, []);
        }
        _firstConfigured.TrySetResult();
    }

    /// <summary>Called for every quote (receive thread).</summary>
    public void OnQuote(Quote q)
    {
        lock (_lock)
        {
            if (_accounts.TryGetValue(q.CtidTraderAccountId, out var acc) && acc.Ticks.TryGetValue(q.SymbolId, out var list))
                list.Add(new Tick(q.TimeUtc, q.Bid, q.Ask));
        }
    }

    /// <summary>Writes rows at every interval boundary until cancelled. Starts at the first full interval after the first Configure.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await _firstConfigured.Task.WaitAsync(ct);
        var start = MarketHours.NextBoundary(_utcNow());
        await DelayUntil(start + Grace, ct);
        TakeTicksBefore(start); // drop the partial interval before the first boundary
        log.Event("SpreadsStarted").Information("Spread logging started: first row at {End:HH:mm}Z, then every {Main} min (main hours) / {Off} min (off-peak)",
            MarketHours.NextBoundary(start), MarketHours.MainInterval.TotalMinutes, MarketHours.OffPeakInterval.TotalMinutes);

        var nextStatus = start.AddHours(1);
        var rowsAtStatus = 0L;
        while (true)
        {
            var end = MarketHours.NextBoundary(start);
            await DelayUntil(end + Grace, ct);
            FlushInterval(start, end);
            start = end;

            if (end >= nextStatus)
            {
                var rows = Interlocked.Read(ref _rowsWritten);
                log.Event("SpreadsStatus").Information("Spread logging: {Rows} rows written in the last hour", rows - rowsAtStatus);
                rowsAtStatus = rows;
                nextStatus = end.AddHours(1);
            }
        }
    }

    /// <summary>Writes the rows of [start, end) for every account and symbol. Returns the number of rows written.</summary>
    public int FlushInterval(DateTime start, DateTime end)
    {
        var written = 0;
        var session = MarketHours.IsMainHours(start) ? "main" : "offpeak";
        var intervalSeconds = (int)(end - start).TotalSeconds;

        foreach (var (acc, symbol, taken) in TakeTicksBefore(end))
        {
            // Ticks stamped before the interval are stale: the last price the server re-sends on every
            // subscribe (e.g. Friday's close during the weekend), or messages more than 1 s late.
            var ticks = taken.Where(t => t.TimeUtc >= start).ToList();
            if (ticks.Count == 0 && !symbol.Schedule.IsOpenThroughout(start, end))
                continue; // market closed: no row

            var s = SpreadStats.Summarize(ticks, start, end);
            var pip = symbol.PipSize;
            var usdPerLot = UsdPerPriceUnit(acc, symbol) * symbol.LotUnits;

            acc.Writer.Write(end,
            [
                CsvFormat.Time(end), CsvFormat.Int(intervalSeconds), session, symbol.Name, CsvFormat.Int(s.Ticks),
                CsvFormat.Num(s.Bid, symbol.Digits), CsvFormat.Num(s.Ask, symbol.Digits),
                CsvFormat.Num(s.Min / pip, 2), CsvFormat.Num(s.Avg / pip, 2), CsvFormat.Num(s.Median / pip, 2),
                CsvFormat.Num(s.P95 / pip, 2), CsvFormat.Num(s.Max / pip, 2),
                CsvFormat.Num(s.Avg * usdPerLot, 2), CsvFormat.Num(s.Max * usdPerLot, 2),
                CsvFormat.Num(s.MaxGapSeconds, 1),
            ]);
            written++;
        }

        Interlocked.Add(ref _rowsWritten, written);
        return written;
    }

    /// <summary>The account's symbols in config order (after the last Configure); empty before that.</summary>
    public IReadOnlyList<SymbolInfo> SymbolsOf(long ctid)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(ctid, out var acc))
                return [];
            var order = acc.Config.Symbols.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
            return acc.Symbols.Values.OrderBy(s => order.GetValueOrDefault(s.Name, int.MaxValue)).ToList();
        }
    }

    /// <summary>USD value of a price move of 1.0 in the symbol's quote currency on this account; null if not known yet.</summary>
    public double? UsdPerPriceUnit(long ctid, SymbolInfo symbol)
    {
        lock (_lock)
            return _accounts.TryGetValue(ctid, out var acc) ? UsdPerPriceUnit(acc, symbol) : null;
    }

    private double? UsdPerPriceUnit(Account acc, SymbolInfo symbol)
    {
        if (symbol.QuoteAsset.Equals("USD", StringComparison.OrdinalIgnoreCase))
            return 1.0;
        if (!acc.Conversions.TryGetValue(symbol.QuoteAsset, out var conv) ||
            feed.Last(acc.Config.AccountId, conv.Symbol.SymbolId) is not { } q)
            return null;
        return conv.Invert ? 1.0 / q.Mid : q.Mid;
    }

    private List<(Account, SymbolInfo, List<Tick>)> TakeTicksBefore(DateTime boundary)
    {
        var result = new List<(Account, SymbolInfo, List<Tick>)>();
        lock (_lock)
        {
            foreach (var acc in _accounts.Values)
            foreach (var (id, symbol) in acc.Symbols.OrderBy(s => s.Value.Name))
            {
                var list = acc.Ticks[id];
                var taken = list.Where(t => t.TimeUtc < boundary).ToList();
                list.RemoveAll(t => t.TimeUtc < boundary);
                result.Add((acc, symbol, taken));
            }
        }
        return result;
    }

    private async Task DelayUntil(DateTime utc, CancellationToken ct)
    {
        var wait = utc - _utcNow();
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }
}
