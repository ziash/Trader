using Trader.Core.Csv;

namespace Trader.AuditReport;

/// <summary>
/// The broker comparison report (audit-bot.md §5): one row per broker × symbol × period, sorted so that the rows of
/// both brokers for the same symbol and period are next to each other. Periods:
/// <list type="bullet">
/// <item><c>day</c> — the whole range: spreads and all probes of the symbol;</item>
/// <item><c>main</c>, <c>offpeak</c> — spreads in main hours / off-peak (audit-bot.md §2.3);</item>
/// <item>each probe window (<c>QuietHour</c>, <c>LondonOpen</c>, …) — the probes of that window.</item>
/// </list>
/// Then one row per broker with symbol <c>ALL</c>: order totals, latency over all symbols, and reliability
/// (instances, connection drops, alerts, missed windows, warnings, errors).
///
/// Spread figures come from the spreads.csv rows: "median" and "p95" are the median and 95th percentile of the
/// rows' average spread, each row weighted by its length (so: half of the time the spread averaged at or below
/// the median; 95 % of the time at or below p95); "max" is the largest single spread seen. Crypto pips differ
/// by broker — compare crypto by the USD columns.
/// </summary>
public static class DailyReport
{
    public static readonly string[] Header =
    [
        "date_from", "date_to", "broker", "broker_name", "symbol", "period",
        "spread_rows", "logged_min", "ticks", "gap_rows",
        "spread_median_pips", "spread_p95_pips", "spread_max_pips",
        "spread_median_usd", "spread_p95_usd", "spread_max_usd",
        "orders", "filled", "rejected", "order_errors",
        "slip_avg_pips", "slip_worst_pips", "slip_avg_usd", "slip_worst_usd",
        "latency_avg_ms", "latency_median_ms", "latency_max_ms", "commission_usd",
        "instances", "connection_drops", "spread_alerts", "slippage_alerts", "missed_windows", "log_warnings", "log_errors",
    ];

    public const string AllSymbols = "ALL";

    private static readonly string[] PeriodOrder =
        ["day", "main", "offpeak", "QuietHour", "LondonOpen", "NewYorkOpen", "NewsRelease-2m", "NewsRelease+5s", "NewsRelease+5m"];

    private static readonly HashSet<string> DropEvents = ["ConnectionLost", "ServerDisconnect", "AccountDisconnected"];

    public static List<string[]> Build(DateOnly from, DateOnly to, IReadOnlyList<BrokerLogs> brokers,
        IReadOnlyDictionary<string, (string Name, IReadOnlyList<string> Symbols)> info)
    {
        var rows = new List<(string Symbol, string Period, string Broker, string[] Values)>();
        foreach (var b in brokers)
        {
            var name = info.TryGetValue(b.Broker, out var i) ? i.Name : "";
            string[] Row(string symbol, string period, IEnumerable<string> spreads, IReadOnlyList<ProbeRow>? probes, bool slippage, BrokerLogs? reliability) =>
            [
                Date(from), Date(to), b.Broker, name, symbol, period,
                .. spreads, .. ProbeColumns(probes, slippage), .. ReliabilityColumns(reliability),
            ];

            foreach (var symbol in b.Spreads.Select(s => s.Symbol).Union(b.Probes.Select(p => p.Symbol)).Distinct())
            {
                var spreads = b.Spreads.Where(s => s.Symbol == symbol).ToList();
                var probes = b.Probes.Where(p => p.Symbol == symbol).ToList();
                rows.Add((symbol, "day", b.Broker, Row(symbol, "day", SpreadColumns(spreads), probes, true, null)));
                foreach (var session in new[] { "main", "offpeak" })
                    if (spreads.Where(s => s.Session == session).ToList() is { Count: > 0 } part)
                        rows.Add((symbol, session, b.Broker, Row(symbol, session, SpreadColumns(part), null, true, null)));
                foreach (var window in probes.GroupBy(p => p.Window))
                    rows.Add((symbol, window.Key, b.Broker, Row(symbol, window.Key, SpreadColumns(null), window.ToList(), true, null)));
            }
            rows.Add((AllSymbols, "day", b.Broker, Row(AllSymbols, "day", AllSpreadColumns(b.Spreads), b.Probes, false, b)));
        }

        var symbolOrder = info.Values.SelectMany(v => v.Symbols).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int SymbolRank(string s) => s == AllSymbols ? int.MaxValue : symbolOrder.IndexOf(s) is var k and >= 0 ? k : symbolOrder.Count;
        int PeriodRank(string p) => Array.IndexOf(PeriodOrder, p) is var k and >= 0 ? k : PeriodOrder.Length;

        return rows
            .OrderBy(r => SymbolRank(r.Symbol)).ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .ThenBy(r => PeriodRank(r.Period)).ThenBy(r => r.Period, StringComparer.Ordinal)
            .ThenBy(r => r.Broker, StringComparer.Ordinal)
            .Select(r => r.Values)
            .ToList();
    }

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static string Minutes(IEnumerable<SpreadRow> rows) => CsvFormat.Num(rows.Sum(r => r.IntervalS) / 60.0, 0);

    private static IEnumerable<string> SpreadColumns(IReadOnlyList<SpreadRow>? rows)
    {
        if (rows is null || rows.Count == 0)
            return Enumerable.Repeat("", 10);

        var pips = rows.Where(r => r.AvgPips is not null).Select(r => (r.AvgPips!.Value, (double)r.IntervalS)).ToList();
        var usd = rows.Where(r => r.AvgUsd is not null).Select(r => (r.AvgUsd!.Value, (double)r.IntervalS)).ToList();
        return
        [
            CsvFormat.Int(rows.Count), Minutes(rows), CsvFormat.Int(rows.Sum(r => r.Ticks)), CsvFormat.Int(rows.Count(r => r.Ticks == 0)),
            CsvFormat.Num(WeightedQuantile(pips, 0.5), 2), CsvFormat.Num(WeightedQuantile(pips, 0.95), 2), CsvFormat.Num(rows.Max(r => r.MaxPips), 2),
            CsvFormat.Num(WeightedQuantile(usd, 0.5), 2), CsvFormat.Num(WeightedQuantile(usd, 0.95), 2), CsvFormat.Num(rows.Max(r => r.MaxUsd), 2),
        ];
    }

    /// <summary>Counts only: spreads of different symbols cannot be combined. Logged time counts each interval once (symbols run in parallel).</summary>
    private static IEnumerable<string> AllSpreadColumns(IReadOnlyList<SpreadRow> rows) =>
        rows.Count == 0
            ? Enumerable.Repeat("", 10)
            : [CsvFormat.Int(rows.Count), Minutes(rows.DistinctBy(r => r.Time)), CsvFormat.Int(rows.Sum(r => r.Ticks)),
               CsvFormat.Int(rows.Count(r => r.Ticks == 0)), "", "", "", "", "", ""];

    private static IEnumerable<string> ProbeColumns(IReadOnlyList<ProbeRow>? rows, bool slippage)
    {
        if (rows is null || rows.Count == 0)
            return Enumerable.Repeat("", 12);

        var filled = rows.Where(r => r.Result == "filled").ToList();
        var latency = filled.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).Order().ToList();
        double? Avg(Func<ProbeRow, double?> f) => filled.Select(f).Where(v => v is not null).DefaultIfEmpty().Average();
        double? Worst(Func<ProbeRow, double?> f) => filled.Select(f).Where(v => v is not null).DefaultIfEmpty().Min();
        return
        [
            CsvFormat.Int(rows.Count), CsvFormat.Int(filled.Count),
            CsvFormat.Int(rows.Count(r => r.Result == "rejected")), CsvFormat.Int(rows.Count(r => r.Result == "error")),
            // Slippage over several symbols would mix units (forex vs crypto pips), so the ALL row leaves it out.
            slippage ? CsvFormat.Num(Avg(r => r.SlipPips), 2) : "", slippage ? CsvFormat.Num(Worst(r => r.SlipPips), 2) : "",
            slippage ? CsvFormat.Num(Avg(r => r.SlipUsd), 2) : "", slippage ? CsvFormat.Num(Worst(r => r.SlipUsd), 2) : "",
            CsvFormat.Num(latency.Count > 0 ? latency.Average() : null, 0),
            CsvFormat.Num(latency.Count > 0 ? Median(latency) : null, 0),
            CsvFormat.Num(latency.Count > 0 ? latency[^1] : null, 0),
            CsvFormat.Num(filled.Sum(r => r.CommissionUsd ?? 0), 2),
        ];
    }

    private static IEnumerable<string> ReliabilityColumns(BrokerLogs? b)
    {
        if (b is null)
            return Enumerable.Repeat("", 7);
        int Count(Func<EventRow, bool> f) => b.Events.Count(f);
        return
        [
            CsvFormat.Int(b.Instances.Count),
            CsvFormat.Int(Count(e => DropEvents.Contains(e.Event))),
            CsvFormat.Int(Count(e => e.Event == "SpreadWide")),
            CsvFormat.Int(Count(e => e.Event == "SlippageHigh")),
            CsvFormat.Int(Count(e => e.Event == "ProbeWindowMissed")),
            CsvFormat.Int(Count(e => e.Level == "Warning")),
            CsvFormat.Int(Count(e => e.Level is "Error" or "Fatal")),
        ];
    }

    /// <summary>The smallest value at or below which a share <paramref name="q"/> of the total weight lies (nearest rank, weighted).</summary>
    public static double? WeightedQuantile(IReadOnlyList<(double Value, double Weight)> items, double q)
    {
        if (items.Count == 0)
            return null;
        var sorted = items.OrderBy(i => i.Value).ToList();
        var target = q * sorted.Sum(i => i.Weight);
        var cumulative = 0.0;
        foreach (var (value, weight) in sorted)
        {
            cumulative += weight;
            if (cumulative >= target - 1e-9)
                return value;
        }
        return sorted[^1].Value;
    }

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>Writes the report atomically (temp file, then rename), in the CSV format of audit-bot.md §3.1.</summary>
    public static void Write(string path, IEnumerable<string[]> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, CsvFormat.Line(Header) + string.Concat(rows.Select(r => CsvFormat.Line(r))), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
