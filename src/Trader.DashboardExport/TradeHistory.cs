using System.Globalization;
using System.Text.RegularExpressions;
using Trader.Core.Csv;

namespace Trader.DashboardExport;

/// <summary>One row of TM's actions.csv (tm.md §7) that the trade pictures use.</summary>
public sealed record TmAction(DateTime Utc, string Trade, string Role, string Action, string PriceReq, string Result);

/// <summary>The opening order of a trade's main: where it was meant to enter, its first stop-loss and when it was placed.</summary>
public sealed record MainOrder(long OrderId, DateTime PlacedUtc, double? Level, double? StopLoss);

/// <summary>Everything known about one trade (dashboard.md §2.3): its fills, TM's actions and the legs still open.</summary>
public sealed record TradeFacts(
    string Trade, string Owner, string Symbol, long SymbolId, IReadOnlyList<DealRow> Deals, IReadOnlyList<SnapshotPosition> OpenLegs,
    MainOrder? Main, IReadOnlyList<TmAction> Actions)
{
    public bool Open => OpenLegs.Count > 0;

    /// <summary>Net of every fill (closes: profit + swap + commission; opens: their commission) plus the open legs' result.</summary>
    public double Result => Deals.Sum(d => d.Net) + OpenLegs.Sum(p => p.OpenResultUsd);

    public DateTime StartUtc => new[] { Main?.PlacedUtc, Deals.Select(d => (DateTime?)d.TimeUtc).Min(), Actions.Select(a => (DateTime?)a.Utc).Min() }
        .Where(t => t is not null).Min() ?? DateTime.UtcNow;

    public DateTime? ClosedUtc => Open || Deals.Count == 0 ? null : Deals.Max(d => d.TimeUtc);

    /// <summary>The newest thing that happened to the trade: a fill or a TM action.</summary>
    public DateTime LastUtc => Deals.Select(d => d.TimeUtc).Concat(Actions.Select(a => a.Utc)).DefaultIfEmpty(StartUtc).Max();

    /// <summary>The main's stop-loss over time: the order's own, then TM's changes (SetProtection on MAIN, "SL 1.16300 TP none").</summary>
    public List<Step> StopLossSteps()
    {
        var steps = new List<Step>();
        if (Main is not null)
            steps.Add(new Step(Main.PlacedUtc, Main.StopLoss));
        foreach (var a in Actions.Where(a => a is { Role: "MAIN", Action: "SetProtection", Result: "ok" }))
            if (Regex.Match(a.PriceReq, @"^SL (\S+)") is { Success: true } m)
                steps.Add(new Step(a.Utc, m.Groups[1].Value == "none" ? null : TryNum(m.Groups[1].Value)));
        return TradeChart.Compact(steps);
    }

    /// <summary>The hedge level H over time: where TM placed or moved the main's hedge order.</summary>
    public List<Step> HedgeSteps() => TradeChart.Compact(Actions
        .Where(a => a is { Role: "HMAIN", Action: "PlaceHedge" or "MoveOrder", Result: "ok" } && TryNum(a.PriceReq) is not null)
        .Select(a => new Step(a.Utc, TryNum(a.PriceReq))));

    private static double? TryNum(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public static class TradeHistory
{
    /// <summary>TM's actions of the account from <paramref name="first"/> on, from every TM instance's daily actions.csv.</summary>
    public static List<TmAction> ReadActions(string accountDir, DateOnly first)
    {
        var rows = new List<TmAction>();
        if (!Directory.Exists(accountDir))
            return rows;
        foreach (var instance in Directory.EnumerateDirectories(accountDir, "TM-*"))
        foreach (var dayDir in Directory.EnumerateDirectories(instance))
        {
            var file = Path.Combine(dayDir, "actions.csv");
            if (!DateOnly.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", out var d) || d < first || !File.Exists(file))
                continue;
            var t = CsvTable.Read(file);
            if (!t.Has("trade") || !t.Has("price_req"))
                continue;
            foreach (var r in t.Rows)
                if (r.Time("time_utc") is { } utc)
                    rows.Add(new TmAction(utc, r["trade"], r["role"], r["action"], r["price_req"], r["result"]));
        }
        return rows.OrderBy(a => a.Utc).ToList();
    }

    /// <summary>
    /// Groups the fills and open legs into trades. A TM trade also gathers TM's actions logged under its pending-order name
    /// O&lt;orderId&gt; before the main filled (tm.md §7).
    /// </summary>
    public static List<TradeFacts> Build(IEnumerable<DealRow> deals, SnapshotFile snapshot, IReadOnlyDictionary<long, MainOrder> mainOrders,
        IReadOnlyDictionary<string, long> symbolIds, IReadOnlyList<TmAction> actions)
    {
        var byTrade = deals.GroupBy(d => d.Link.Trade).ToDictionary(g => g.Key, g => g.OrderBy(d => d.TimeUtc).ThenBy(d => d.DealId).ToList());
        var open = snapshot.Positions.GroupBy(p => p.Trade).ToDictionary(g => g.Key, g => g.ToList());
        var actionsByTrade = actions.GroupBy(a => a.Trade).ToDictionary(g => g.Key, g => g.ToList());

        var trades = new List<TradeFacts>();
        foreach (var key in byTrade.Keys.Union(open.Keys).Where(k => k.StartsWith('P')))
        {
            var ds = byTrade.GetValueOrDefault(key) ?? [];
            var legs = open.GetValueOrDefault(key) ?? [];
            var mainId = long.Parse(key[1..], CultureInfo.InvariantCulture);
            var main = mainOrders.GetValueOrDefault(mainId);
            var owner = ds.FirstOrDefault(d => d.Link.Role == "MAIN")?.Link.Owner ?? legs.FirstOrDefault(p => p.Role == "MAIN")?.Owner
                ?? ds.FirstOrDefault()?.Link.Owner ?? legs[0].Owner;
            var symbol = ds.FirstOrDefault()?.Symbol ?? legs[0].Symbol;
            var acts = (actionsByTrade.GetValueOrDefault(key) ?? [])
                .Concat(main is null ? [] : actionsByTrade.GetValueOrDefault($"O{main.OrderId}") ?? [])
                .OrderBy(a => a.Utc).ToList();
            trades.Add(new TradeFacts(key, owner, symbol, symbolIds.GetValueOrDefault(symbol), ds, legs, main, acts));
        }
        return trades.OrderBy(t => t.StartUtc).ToList();
    }

    /// <summary>Trades that get a picture: those of Scout, of the owner (manual) and TM's; not other bots' (hundreds of Audit Bot probes a day).</summary>
    public static bool Pictured(TradeFacts t) => t.Owner is "Scout" or "manual" or "TM";
}
