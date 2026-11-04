using Trader.Core.Csv;

namespace Trader.Scout;

/// <summary>One order Scout placed, as its <c>place</c> row in orders.csv and its setup row in setups.csv recorded it.</summary>
public sealed record PlacedOrder(
    DateTime TimeUtc, string Symbol, string Timeframe, DateTime SetupUtc, bool Reversal, int Dir, double Price, double? StopLoss,
    DateTime? ExpiresUtc, long OrderId, string? HigherTimeframe, string WithHigher);

/// <summary>
/// Reads back the orders Scout placed on one account from the logs of all its instances
/// (~/tradebot/logs/&lt;broker&gt;/&lt;ctid&gt;/Scout-*/&lt;date&gt;/), for pictures drawn later (scout.md §3.1). Read-only.
/// </summary>
public static class OrderLog
{
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
