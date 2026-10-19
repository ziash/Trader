// Audit report (plan 6.5, Phase 1 step 6). Reads the Audit Bot's CSV logs and writes the broker comparison:
//   ~/tradebot/reports/daily-compare-<yyyy-MM-dd>.csv          one UTC day
//   ~/tradebot/reports/summary-<from>_<to>.csv                 a range (the final 2-week summary)
// Read-only: never connects to cTrader and never touches the logs, so it is safe while the bot runs.
// Run daily by the systemd timer deploy/trader-auditreport.timer (with no options = --missing).

using System.Globalization;
using Trader.AuditReport;
using Trader.Core.Hosting;

const string Usage = """
    Usage: Trader.AuditReport [--missing | --date yyyy-MM-dd | --from yyyy-MM-dd --to yyyy-MM-dd]
                              [--logs <dir>] [--config-dir <dir>] [--reports <dir>]

      --missing    (default) a daily report for every complete UTC day in the logs that has none yet
      --date D     the daily report for day D; rebuilt if it already exists
      --from/--to  one summary over the days from..to (both included)
      --logs, --config-dir, --reports   folders (default ~/tradebot/logs, ~/tradebot/config, ~/tradebot/reports)
    """;

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
DateOnly? DateOption(string name) =>
    Option(name) is { } v && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Usage);
    return 0;
}
string[] known = ["--missing", "--date", "--from", "--to", "--logs", "--config-dir", "--reports"];
var unknown = args.Where((a, i) => a.StartsWith("--") && !known.Contains(a)).ToList();
var date = DateOption("--date");
var from = DateOption("--from");
var to = DateOption("--to");
if (unknown.Count > 0 || (args.Contains("--date") && date is null) || (args.Contains("--from") || args.Contains("--to")) && (from is null || to is null || from > to))
{
    Console.Error.WriteLine(unknown.Count > 0 ? $"Unknown option {unknown[0]}" : "Dates must look like 2026-10-19 (and --from not after --to)");
    Console.Error.WriteLine(Usage);
    return 2;
}

var logsDir = Option("--logs") ?? TradebotPaths.Logs;
var configDir = Option("--config-dir") ?? TradebotPaths.Config;
var reportsDir = Option("--reports") ?? TradebotPaths.Reports;
var info = AuditLogs.LoadBrokerInfo(configDir);

void Make(DateOnly first, DateOnly last, string file)
{
    var brokers = AuditLogs.Load(logsDir, first, last);
    var path = Path.Combine(reportsDir, file);
    var rows = DailyReport.Build(first, last, brokers, info);
    DailyReport.Write(path, rows);
    var detail = string.Join("; ", brokers.Select(b =>
        $"{b.Broker}: {b.Instances.Count} instance(s), {b.Spreads.Count} spread rows, {b.Probes.Count} orders" +
        (b.BadLines > 0 ? $", {b.BadLines} unreadable line(s) skipped" : "")));
    Console.WriteLine($"Wrote {path} ({rows.Count} rows) — {(detail.Length > 0 ? detail : "no logs for these days")}");
}

if (from is { } f && to is { } t)
{
    Make(f, t, $"summary-{f:yyyy-MM-dd}_{t:yyyy-MM-dd}.csv");
    return 0;
}
if (date is { } d)
{
    if (d >= DateOnly.FromDateTime(DateTime.UtcNow))
        Console.WriteLine($"Note: {d:yyyy-MM-dd} is not over yet (UTC); this report is partial.");
    Make(d, d, $"daily-compare-{d:yyyy-MM-dd}.csv");
    return 0;
}

var today = DateOnly.FromDateTime(DateTime.UtcNow);
var missing = AuditLogs.Days(logsDir).Where(day => day < today && !File.Exists(Path.Combine(reportsDir, $"daily-compare-{day:yyyy-MM-dd}.csv"))).ToList();
foreach (var day in missing)
    Make(day, day, $"daily-compare-{day:yyyy-MM-dd}.csv");
if (missing.Count == 0)
    Console.WriteLine("All daily reports are up to date.");
return 0;
