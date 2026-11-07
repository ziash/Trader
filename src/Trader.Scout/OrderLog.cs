using System.Globalization;
using Trader.Core.Csv;

namespace Trader.Scout;

/// <summary>One order Scout placed, as its <c>place</c> row in orders.csv and its setup row in setups.csv recorded it.</summary>
public sealed record PlacedOrder(
    DateTime TimeUtc, string Symbol, string Timeframe, DateTime SetupUtc, bool Reversal, int Dir, double Price, double? StopLoss,
    DateTime? ExpiresUtc, long OrderId, string? HigherTimeframe, string WithHigher);

/// <summary>How the tag of another setup on an order picture is coloured (scout.md §3.1).</summary>
public enum OutcomeTone { Muted, Info, Warning, Loss, Profit }

/// <summary>
/// Another setup on the same chart and what had become of it by a given moment (scout.md §3.1, other Scout orders):
/// its level, when it filled, where its line ends (null = still going at that moment), and a short tag such as
/// "closed −$15.91" or "refused: not enough money". <see cref="Closed"/>: the whole position was closed (✕).
/// </summary>
public sealed record PastSetup(
    DateTime PauseStartUtc, bool Reversal, int Dir, double Level, DateTime? FilledUtc, DateTime? EndUtc, string Outcome, OutcomeTone Tone, bool Closed);

/// <summary>
/// Reads back the orders Scout placed on one account from the logs of all its instances
/// (~/tradebot/logs/&lt;broker&gt;/&lt;ctid&gt;/Scout-*/&lt;date&gt;/), for pictures drawn later (scout.md §3.1). Read-only.
/// </summary>
public static class OrderLog
{
    /// <summary>
    /// Scout's setups on one chart whose pause started from <paramref name="fromUtc"/> on, with what had become of each
    /// by <paramref name="untilUtc"/> (scout.md §3.1): only rows written before that moment count, so a picture drawn
    /// later is the same picture. Reads setups.csv and orders.csv of the day folders in that range, of every Scout instance.
    /// </summary>
    public static List<PastSetup> ReadHistory(string accountFolder, string symbol, string timeframe, DateTime fromUtc, DateTime untilUtc)
    {
        var history = new List<PastSetup>();
        if (!Directory.Exists(accountFolder))
            return history;
        var days = new HashSet<string>();
        for (var d = fromUtc.Date; d <= untilUtc.Date; d = d.AddDays(1))
            days.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var folders = Directory.GetDirectories(accountFolder, ScoutConfig.BotName + "-*")
            .SelectMany(Directory.GetDirectories).Where(d => days.Contains(Path.GetFileName(d))).ToList();

        // The side and level of each setup role from the event that found it, and every orders.csv row of the role.
        var found = new Dictionary<string, (int Dir, double Level)>();
        var rows = new Dictionary<string, List<CsvRow>>();
        foreach (var file in folders.Select(d => Path.Combine(d, "setups.csv")).Where(File.Exists))
            foreach (var row in CsvTable.Read(file).Rows)
                if (row["event"] is "NewStop" or "NewReversal" && row["symbol"] == symbol && row["timeframe"] == timeframe
                    && row.Time("time_utc") < untilUtc && row.Time("setup_utc") is { } setup && setup >= fromUtc && row.Num("level") is { } level)
                    found.TryAdd(ScoutState.Key(symbol, timeframe, setup, row["event"] == "NewReversal"), (row["side"] == "Buy" ? 1 : -1, level));
        foreach (var file in folders.Select(d => Path.Combine(d, "orders.csv")).Where(File.Exists))
            foreach (var row in CsvTable.Read(file).Rows)
                if (row["symbol"] == symbol && row["timeframe"] == timeframe && row.Time("time_utc") < untilUtc
                    && row.Time("setup_utc") is { } setup && setup >= fromUtc && row["role"] is "M" or "R")
                {
                    var key = ScoutState.Key(symbol, timeframe, setup, row["role"] == "R");
                    if (!rows.TryGetValue(key, out var list))
                        rows[key] = list = [];
                    list.Add(row);
                }

        foreach (var key in found.Keys.Union(rows.Keys))
        {
            var list = rows.GetValueOrDefault(key, []).OrderBy(r => r.Time("time_utc")).ToList();
            var parts = key.Split('|');
            var pauseStart = DateTime.ParseExact(parts[2], "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var (dir, level) = found.GetValueOrDefault(key);
            if (list.FirstOrDefault(r => r["action"] is "place" or "skip" or "alert" && r.Num("price_req") is not null) is { } first)
                (dir, level) = (first["side"] == "Buy" ? 1 : -1, first.Num("price_req")!.Value);
            if (dir == 0 || level == 0)
                continue;
            var (outcome, tone, end, closed) = Outcome(list);
            history.Add(new PastSetup(pauseStart, parts[3] == "R", dir, level, list.FirstOrDefault(r => r["action"] == "filled")?.Time("time_utc"),
                end, outcome, tone, closed));
        }
        return history.OrderBy(h => h.PauseStartUtc).ThenBy(h => h.Reversal).ToList();
    }

    // The latest thing that happened to one setup role, from its orders.csv rows (oldest first); a close outranks
    // a fill, a fill outranks the order's earlier states.
    private static (string Outcome, OutcomeTone Tone, DateTime? End, bool Closed) Outcome(List<CsvRow> rows)
    {
        CsvRow? Last(string action, string? result = null) => rows.LastOrDefault(r => r["action"] == action && (result is null || r["result"] == result));
        var closes = rows.Where(r => r["action"] == "closed").ToList();
        if (closes.Count > 0)
        {
            var net = closes.Sum(r => r.Num("net") ?? 0);
            var opened = rows.Where(r => r["action"] == "filled").Sum(r => r.Num("lots") ?? 0);
            var whole = opened <= 0 || closes.Sum(r => r.Num("lots") ?? 0) >= opened - 1e-9;
            var money = $"{(net < 0 ? "−" : "+")}${Math.Abs(net).ToString("0.00", CultureInfo.InvariantCulture)}";
            return ($"{(whole ? "closed" : "part closed")} {money}", net < 0 ? OutcomeTone.Loss : OutcomeTone.Profit, whole ? closes[^1].Time("time_utc") : null, whole);
        }
        if (Last("filled") is { } fill)
            return ("filled, with TM", OutcomeTone.Info, fill.Time("time_utc"), false);
        if (Last("rejected") is { } rejected)
            return ($"refused: {Plain(rejected["detail"])}", OutcomeTone.Warning, rejected.Time("time_utc"), false);
        if (Last("expired") is { } expired)
            return ("expired", OutcomeTone.Muted, expired.Time("time_utc"), false);
        if (Last("cancel", "ok") is { } cancel)
            return ($"cancelled: {cancel["detail"]}", OutcomeTone.Muted, cancel.Time("time_utc"), false);
        if (Last("cancelled") is { } cancelled)
            return ("cancelled by owner or TM", OutcomeTone.Muted, cancelled.Time("time_utc"), false);
        if (Last("place", "ok") is not null)
            return ("waiting", OutcomeTone.Info, null, false);
        if (Last("place", "refused") is { } refused)
            return ($"place refused: {Plain(refused["detail"].Split(':')[0])}", OutcomeTone.Warning, refused.Time("time_utc"), false);
        if (Last("place", "dry run") is not null)
            return ("dry run", OutcomeTone.Muted, null, false);
        if (Last("skip") is { } skip)
            return (skip["result"] == "no margin" ? "not placed: no margin" : skip["result"], OutcomeTone.Warning, skip.Time("time_utc"), false);
        if (Last("alert") is { } alert)
            return ("alert only", OutcomeTone.Muted, alert.Time("time_utc"), false);
        return ("no order", OutcomeTone.Muted, null, false);
    }

    // A broker code in plain words: "NOT_ENOUGH_MONEY" → "not enough money".
    private static string Plain(string code) => code.Trim().Replace('_', ' ').ToLowerInvariant();

    public static Dictionary<long, PlacedOrder> ReadPlaced(string accountFolder)
    {
        var tags = new Dictionary<string, (string? Tf, string With)>();
        var placed = new Dictionary<long, PlacedOrder>();
        if (!Directory.Exists(accountFolder))
            return placed;
        var instances = Directory.GetDirectories(accountFolder, ScoutConfig.BotName + "-*");

        // The higher-timeframe tag of each setup role, from the event that gave it its level.
        foreach (var file in instances.SelectMany(d => Directory.EnumerateFiles(d, "setups.csv", SearchOption.AllDirectories)))
            foreach (var row in CsvTable.Read(file).Rows)
                if (row["event"] is "NewStop" or "NewReversal" && row.Time("setup_utc") is { } setup)
                    tags.TryAdd(ScoutState.Key(row["symbol"], row["timeframe"], setup, row["event"] == "NewReversal"), (Blank(row["higher_tf"]), row["with_higher"]));

        foreach (var file in instances.SelectMany(d => Directory.EnumerateFiles(d, "orders.csv", SearchOption.AllDirectories)))
            foreach (var row in CsvTable.Read(file).Rows)
            {
                if (row["action"] != "place" || row["result"] != "ok" || !long.TryParse(row["order_id"], out var id)
                    || row.Time("time_utc") is not { } time || row.Time("setup_utc") is not { } setup || row.Num("price_req") is not { } price)
                    continue;
                var reversal = row["role"] == "R";
                var tag = tags.GetValueOrDefault(ScoutState.Key(row["symbol"], row["timeframe"], setup, reversal), (null, "unknown"));
                placed.TryAdd(id, new PlacedOrder(time, row["symbol"], row["timeframe"], setup, reversal, row["side"] == "Buy" ? 1 : -1, price,
                    row.Num("stop_loss"), row.Time("expires_utc"), id, tag.Tf, tag.With));
            }
        return placed;
    }

    private static string? Blank(string s) => s.Length == 0 ? null : s;
}
