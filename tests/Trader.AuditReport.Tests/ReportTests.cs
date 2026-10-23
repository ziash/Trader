using Trader.AuditReport;
using Trader.Core.Csv;

namespace Trader.AuditReport.Tests;

public sealed class ReportTests : IDisposable
{
    private const string SpreadsHeader = "time_utc,interval_s,session,symbol,ticks,bid,ask,spread_min_pips,spread_avg_pips,spread_median_pips,spread_p95_pips,spread_max_pips,spread_avg_usd,spread_max_usd,max_gap_s";
    private const string ProbesHeader = "time_utc,window,action,symbol,side,lots,req_px,fill_px,slip_pips,slip_usd,latency_ms,commission_usd,result,reason,position_id,label";
    private const string EventsHeader = "time_utc,level,event,detail";
    private static readonly DateOnly Day = new(2026, 10, 19);

    private readonly string _dir = Directory.CreateTempSubdirectory("trader-report-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Logs => Path.Combine(_dir, "logs");

    private void File(string broker, string instance, string day, string name, string header, params string[] rows)
    {
        var folder = Path.Combine(Logs, broker, "1", instance, day);
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(Path.Combine(folder, name), header + "\n" + string.Join("", rows.Select(r => r + "\n")));
    }

    private static string Spread(string time, string session, string symbol, int ticks, double avgPips, double maxPips, double avgUsd, double maxUsd, int interval = 120) =>
        $"2026-10-19 {time},{interval},{session},{symbol},{ticks},1.1,1.1,0,{N(avgPips)},0,0,{N(maxPips)},{N(avgUsd)},{N(maxUsd)},1.0";

    private static string N(double v) => CsvFormat.Num(v, 2); // NaN → empty cell, as the bot writes a row without ticks

    private static string Probe(string time, string window, string action, string symbol, string side, double slipPips, double slipUsd, int latency, string result = "filled") =>
        $"2026-10-19 {time},{window},{action},{symbol},{side},0.01,1.1,1.1,{slipPips},{slipUsd},{latency},0.03,{result},,123,AuditBot-aaaaaa";

    [Fact]
    public void Csv_reader_handles_quotes_commas_and_cut_off_lines()
    {
        var table = CsvTable.Parse("a,b,c\n1,\"x, \"\"y\"\"\",3\n4,5\n6,7,8\n");
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("x, \"y\"", table.Rows[0]["b"]);
        Assert.Equal(8, table.Rows[1].Num("c"));
        Assert.Equal(1, table.BadLines); // "4,5" has too few fields
        Assert.Null(table.Rows[0].Num("b"));
    }

    [Fact]
    public void Weighted_quantile_uses_nearest_rank_by_weight()
    {
        Assert.Null(DailyReport.WeightedQuantile([], 0.5));
        Assert.Equal(2, DailyReport.WeightedQuantile([(3, 1), (1, 1), (2, 1)], 0.5));
        // A 5-minute row counts as much as 2.5 two-minute rows.
        Assert.Equal(9, DailyReport.WeightedQuantile([(1, 120), (9, 300)], 0.5));
        Assert.Equal(9, DailyReport.WeightedQuantile([(1, 120), (1, 120), (9, 120)], 0.95));
    }

    [Fact]
    public void Instances_of_one_day_are_merged_and_overlaps_counted_once()
    {
        // Instance a ran until a restart; instance b after it. A Passive c ran alongside b with fewer ticks.
        File("brokerA", "AuditBot-aaaaaa", "2026-10-19", "spreads.csv", SpreadsHeader, Spread("09:02:00", "main", "EURUSD", 50, 0.1, 0.3, 1, 3));
        File("brokerA", "AuditBot-bbbbbb", "2026-10-19", "spreads.csv", SpreadsHeader, Spread("09:04:00", "main", "EURUSD", 60, 0.2, 0.5, 2, 5));
        File("brokerA", "AuditBot-cccccc", "2026-10-19", "spreads.csv", SpreadsHeader, Spread("09:04:00", "main", "EURUSD", 40, 0.9, 0.9, 9, 9));
        File("brokerA", "AuditBot-aaaaaa", "2026-10-19", "probes.csv", ProbesHeader, Probe("07:00:01", "LondonOpen", "open", "EURUSD", "buy", -0.1, -1, 500));
        File("brokerA", "AuditBot-aaaaaa", "2026-10-18", "spreads.csv", SpreadsHeader, Spread("23:58:00", "offpeak", "EURUSD", 9, 5, 5, 50, 50)); // other day
        File("brokerA", "TM-tttttt", "2026-10-19", "events.csv", EventsHeader, "2026-10-19 09:00:00,Warning,LossWarning,x");                  // another bot

        var logs = Assert.Single(AuditLogs.Load(Logs, Day, Day));
        Assert.Equal(["AuditBot-aaaaaa", "AuditBot-bbbbbb", "AuditBot-cccccc"], logs.Instances); // TM's folder is not read
        Assert.Empty(logs.Events);
        Assert.Equal([50, 60], logs.Spreads.Select(s => s.Ticks)); // 09:04 from b (more ticks), not c
        Assert.Single(logs.Probes);
        Assert.Equal([new DateOnly(2026, 10, 18), Day], AuditLogs.Days(Logs));
    }

    [Fact]
    public void Report_puts_both_brokers_side_by_side_per_symbol_and_period()
    {
        foreach (var (broker, pips) in new[] { ("brokerB", 0.1), ("brokerA", 0.3) })
        {
            File(broker, "AuditBot-aaaaaa", "2026-10-19", "spreads.csv", SpreadsHeader,
                Spread("06:55:00", "offpeak", "BTCUSD", 100, 500, 900, 5, 9, 300),
                Spread("09:02:00", "main", "EURUSD", 10, pips, 1.0, pips * 10, 10),
                Spread("09:04:00", "main", "EURUSD", 0, double.NaN, double.NaN, double.NaN, double.NaN)); // feed gap: empty cells
            File(broker, "AuditBot-aaaaaa", "2026-10-19", "probes.csv", ProbesHeader,
                Probe("07:00:01", "LondonOpen", "open", "EURUSD", "buy", 0.2, 2, 400),
                Probe("07:00:02", "LondonOpen", "close", "EURUSD", "sell", -0.4, -4, 600),
                Probe("07:00:05", "LondonOpen", "open", "BTCUSD", "buy", -691, -6.91, 500),
                Probe("07:00:06", "LondonOpen", "close", "BTCUSD", "sell", 0, 0, 0, "rejected"));
            File(broker, "AuditBot-aaaaaa", "2026-10-19", "events.csv", EventsHeader,
                "2026-10-19 09:17:53,Warning,ConnectionLost,\"Connection lost (no data for 44 s, no reply)\"",
                "2026-10-19 21:00:01,Warning,SpreadWide,brokerA EURUSD: spread wide",
                "2026-10-19 21:10:01,Information,SpreadNormal,back");
        }
        var info = new Dictionary<string, (string, IReadOnlyList<string>)>
        {
            ["brokerA"] = ("IC Markets", ["EURUSD", "BTCUSD"]),
            ["brokerB"] = ("Pepperstone", ["EURUSD", "BTCUSD"]),
        };

        var rows = DailyReport.Build(Day, Day, AuditLogs.Load(Logs, Day, Day), info);
        string Cell(string[] row, string column) => row[Array.IndexOf(DailyReport.Header, column)];
        string[] Find(string broker, string symbol, string period) => rows.Single(r => r[2] == broker && r[4] == symbol && r[5] == period);

        // Config symbol order, then period, then broker — IC and Pepperstone adjacent.
        Assert.Equal(
            [
                "EURUSD day brokerA", "EURUSD day brokerB", "EURUSD main brokerA", "EURUSD main brokerB",
                "EURUSD LondonOpen brokerA", "EURUSD LondonOpen brokerB",
                "BTCUSD day brokerA", "BTCUSD day brokerB", "BTCUSD offpeak brokerA", "BTCUSD offpeak brokerB",
                "BTCUSD LondonOpen brokerA", "BTCUSD LondonOpen brokerB",
                "ALL day brokerA", "ALL day brokerB",
            ],
            rows.Select(r => $"{r[4]} {r[5]} {r[2]}"));
        Assert.All(rows, r => Assert.Equal(DailyReport.Header.Length, r.Length));

        var eur = Find("brokerA", "EURUSD", "day");
        Assert.Equal("IC Markets", Cell(eur, "broker_name"));
        Assert.Equal(("2", "4", "1"), (Cell(eur, "spread_rows"), Cell(eur, "logged_min"), Cell(eur, "gap_rows")));
        Assert.Equal(("0.30", "1.00", "3.00"), (Cell(eur, "spread_median_pips"), Cell(eur, "spread_max_pips"), Cell(eur, "spread_median_usd")));
        Assert.Equal(("2", "2", "-0.10", "-0.40", "-1.00", "-4.00"),
            (Cell(eur, "orders"), Cell(eur, "filled"), Cell(eur, "slip_avg_pips"), Cell(eur, "slip_worst_pips"), Cell(eur, "slip_avg_usd"), Cell(eur, "slip_worst_usd")));
        Assert.Equal(("500", "500", "600", "0.06"),
            (Cell(eur, "latency_avg_ms"), Cell(eur, "latency_median_ms"), Cell(eur, "latency_max_ms"), Cell(eur, "commission_usd")));
        Assert.Equal("", Cell(eur, "instances")); // reliability only on the ALL row

        var btcWindow = Find("brokerB", "BTCUSD", "LondonOpen");
        Assert.Equal(("2", "1", "1", ""), (Cell(btcWindow, "orders"), Cell(btcWindow, "filled"), Cell(btcWindow, "rejected"), Cell(btcWindow, "spread_rows")));

        var all = Find("brokerA", "ALL", "day");
        Assert.Equal(("4", "3", "1"), (Cell(all, "orders"), Cell(all, "filled"), Cell(all, "rejected")));
        Assert.Equal("", Cell(all, "slip_avg_pips"));              // would mix forex and crypto pips
        Assert.Equal("", Cell(all, "spread_median_pips"));
        Assert.Equal(("3", "9"), (Cell(all, "spread_rows"), Cell(all, "logged_min"))); // 06:55 (5 min) + 09:02 + 09:04, each once
        Assert.Equal(("1", "1", "1", "0", "0", "2", "0"),
            (Cell(all, "instances"), Cell(all, "connection_drops"), Cell(all, "spread_alerts"), Cell(all, "slippage_alerts"),
             Cell(all, "missed_windows"), Cell(all, "log_warnings"), Cell(all, "log_errors")));
    }

    [Fact]
    public void Report_file_is_written_atomically_with_the_header()
    {
        var path = Path.Combine(_dir, "reports", "daily-compare-2026-10-19.csv");
        DailyReport.Write(path, [["x"]]);
        var lines = System.IO.File.ReadAllLines(path);
        Assert.Equal(string.Join(",", DailyReport.Header), lines[0]);
        Assert.False(System.IO.File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Broker_names_and_symbol_order_come_from_the_config_files()
    {
        var repoConfig = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config");
        var info = AuditLogs.LoadBrokerInfo(repoConfig);
        Assert.Equal("IC Markets", info["brokerA"].Name);
        Assert.Equal("Pepperstone", info["brokerB"].Name);
        Assert.Equal(
            ["AUDJPY", "EURGBP", "EURUSD", "GBPJPY", "USDJPY", "GBPUSD", "BTCUSD", "ETHUSD", "SOLUSD", "XAUUSD", "XAGUSD", "XPTUSD", "XTIUSD", "XBRUSD", "XNGUSD", "US500", "USTEC", "DE40"],
            info["brokerA"].Symbols);
        Assert.Empty(AuditLogs.LoadBrokerInfo(Path.Combine(_dir, "missing")));
    }
}
