using System.Globalization;
using System.Text.Json;
using Trader.Core.Csv;

namespace Trader.AuditReport;

/// <summary>One spreads.csv row (plan 6.3.1); spreads in pips and USD per lot.</summary>
public sealed record SpreadRow(DateTime Time, int IntervalS, string Session, string Symbol, int Ticks,
    double? AvgPips, double? MaxPips, double? AvgUsd, double? MaxUsd);

/// <summary>One probes.csv row: one order (a probe's open or its close).</summary>
public sealed record ProbeRow(DateTime Time, string Window, string Action, string Symbol, string Side, string Result,
    double? SlipPips, double? SlipUsd, double? LatencyMs, double? CommissionUsd, string Label);

/// <summary>One events.csv row.</summary>
public sealed record EventRow(DateTime Time, string Level, string Event);

/// <summary>Everything the Audit Bot logged for one broker over the chosen days, merged across instance folders.</summary>
public sealed class BrokerLogs(string broker)
{
    public string Broker { get; } = broker;
    public List<SpreadRow> Spreads { get; } = [];
    public List<ProbeRow> Probes { get; } = [];
    public List<EventRow> Events { get; } = [];
    public SortedSet<string> Instances { get; } = new(StringComparer.Ordinal);
    public int BadLines { get; set; }
}

/// <summary>
/// Reads ~/tradebot/logs/&lt;broker&gt;/&lt;ctid&gt;/&lt;instanceId&gt;/&lt;yyyy-MM-dd&gt;/ (plan 6.3). Every restart gives a new
/// instance folder, so a day can be spread over several; they are merged here. When two instances ran at the
/// same time (a Primary and a Passive both write spreads.csv), each interval is counted once: the row with
/// more ticks is kept. Probes are sent only by the Primary; identical probe rows are counted once.
/// </summary>
public static class AuditLogs
{
    public static IReadOnlyList<BrokerLogs> Load(string logsRoot, DateOnly from, DateOnly to)
    {
        var result = new List<BrokerLogs>();
        if (!Directory.Exists(logsRoot))
            return result;

        foreach (var brokerDir in Directory.GetDirectories(logsRoot).Order(StringComparer.Ordinal))
        {
            var logs = new BrokerLogs(Path.GetFileName(brokerDir));
            var spreads = new List<SpreadRow>();
            var probes = new List<ProbeRow>();
            foreach (var (instance, dayDir) in DayFolders(brokerDir).Where(d => d.Day >= from && d.Day <= to).Select(d => (d.Instance, d.Path)))
            {
                logs.Instances.Add(instance);
                spreads.AddRange(ReadFile(Path.Combine(dayDir, "spreads.csv"), logs, ToSpread));
                probes.AddRange(ReadFile(Path.Combine(dayDir, "probes.csv"), logs, ToProbe));
                logs.Events.AddRange(ReadFile(Path.Combine(dayDir, "events.csv"), logs, ToEvent));
            }
            if (logs.Instances.Count == 0)
                continue;

            logs.Spreads.AddRange(spreads.GroupBy(s => (s.Time, s.Symbol)).Select(g => g.MaxBy(s => s.Ticks)!).OrderBy(s => s.Time));
            logs.Probes.AddRange(probes.DistinctBy(p => (p.Time, p.Action, p.Symbol, p.Side, p.Label)).OrderBy(p => p.Time));
            logs.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
            result.Add(logs);
        }
        return result;
    }

    /// <summary>The UTC dates that have log folders, for any broker.</summary>
    public static SortedSet<DateOnly> Days(string logsRoot) =>
        Directory.Exists(logsRoot)
            ? new SortedSet<DateOnly>(Directory.GetDirectories(logsRoot).SelectMany(DayFolders).Select(d => d.Day))
            : [];

    private static IEnumerable<(string Instance, DateOnly Day, string Path)> DayFolders(string brokerDir) =>
        from ctidDir in Directory.GetDirectories(brokerDir)
        from instanceDir in Directory.GetDirectories(ctidDir)
        from dayDir in Directory.GetDirectories(instanceDir)
        where DateOnly.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        select (Path.GetFileName(instanceDir), DateOnly.ParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", CultureInfo.InvariantCulture), dayDir);

    private static IEnumerable<T> ReadFile<T>(string path, BrokerLogs logs, Func<CsvRow, T?> map) where T : class
    {
        if (!File.Exists(path))
            return [];
        var table = CsvTable.Read(path);
        logs.BadLines += table.BadLines;
        var rows = new List<T>();
        foreach (var row in table.Rows)
        {
            if (map(row) is { } item)
                rows.Add(item);
            else
                logs.BadLines++;
        }
        return rows;
    }

    private static SpreadRow? ToSpread(CsvRow r) =>
        r.Time("time_utc") is { } t
            ? new SpreadRow(t, (int)(r.Num("interval_s") ?? 0), r["session"], r["symbol"], (int)(r.Num("ticks") ?? 0),
                r.Num("spread_avg_pips"), r.Num("spread_max_pips"), r.Num("spread_avg_usd"), r.Num("spread_max_usd"))
            : null;

    private static ProbeRow? ToProbe(CsvRow r) =>
        r.Time("time_utc") is { } t
            ? new ProbeRow(t, r["window"], r["action"], r["symbol"], r["side"], r["result"],
                r.Num("slip_pips"), r.Num("slip_usd"), r.Num("latency_ms"), r.Num("commission_usd"), r["label"])
            : null;

    private static EventRow? ToEvent(CsvRow r) =>
        r.Time("time_utc") is { } t ? new EventRow(t, r["level"], r["event"]) : null;

    /// <summary>Broker folder name → (broker's name, symbols in config order), from ~/tradebot/config/audit.*.json. Missing or unreadable files are skipped.</summary>
    public static IReadOnlyDictionary<string, (string Name, IReadOnlyList<string> Symbols)> LoadBrokerInfo(string configDir)
    {
        var result = new Dictionary<string, (string, IReadOnlyList<string>)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(configDir))
            return result;
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var file in Directory.GetFiles(configDir, "audit.*.json"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file), options);
                var root = json.RootElement;
                var symbols = root.TryGetProperty("symbols", out var s) ? s.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                result[root.GetProperty("broker").GetString()!] = (root.TryGetProperty("brokerName", out var n) ? n.GetString() ?? "" : "", symbols);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // a broken config only costs the broker's display name and symbol order
            }
        }
        return result;
    }
}
