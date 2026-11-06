using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trader.DashboardExport;

/// <summary>
/// scout-options.json (dashboard.md §3.4): the options catalogue (config/scout-controls.json) with each account's charts as
/// configured in scout.json, so the site draws its controls from the laptop's limits and can show "as configured".
/// </summary>
public static class ScoutOptions
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Null when there is no catalogue: the site then shows no controls.</summary>
    public static JsonObject? Build(string configDir)
    {
        var catalogueFile = Path.Combine(configDir, "scout-controls.json");
        if (!File.Exists(catalogueFile))
            return null;
        var catalogue = JsonNode.Parse(File.ReadAllText(catalogueFile), documentOptions: Lenient)!;

        var accounts = new JsonObject();
        var scoutFile = Path.Combine(configDir, "scout.json");
        if (File.Exists(scoutFile) && JsonNode.Parse(File.ReadAllText(scoutFile), documentOptions: Lenient)?["accounts"] is JsonArray list)
        {
            foreach (var a in list.OfType<JsonObject>())
            {
                if (a["broker"]?.GetValue<string>() is not { } broker || a["accountId"]?.GetValue<long>() is not { } id || a["charts"] is not JsonArray charts)
                    continue;
                var rows = new JsonArray();
                foreach (var c in charts.OfType<JsonObject>())
                    rows.Add(new JsonObject
                    {
                        ["symbol"] = c["symbol"]?.GetValue<string>(),
                        ["timeframe"] = c["timeframe"]?.GetValue<string>(),
                        ["mode"] = c["mode"]?.GetValue<string>() ?? "watch",
                    });
                accounts[$"{broker}-{id}"] = rows;
            }
        }
        return new JsonObject { ["catalogue"] = catalogue, ["accounts"] = accounts };
    }
}
