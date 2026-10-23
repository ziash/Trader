using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.TM;

/// <summary>
/// ~/tradebot/config/tm.json (tm.md). Example and defaults: config/tm.json in the repo.
/// Rule values are in R (the distance from a leg's entry to the hedge level H) unless stated otherwise.
/// </summary>
public sealed record TmConfig(
    IReadOnlyList<TmAccount> Accounts,
    double RangePercent = 15,          // default hedge distance = this % of the average daily range ...
    int RangeDays = 3,                 // ... over the last N completed daily candles (tm.md §3)
    double SpreadFloor = 5,            // ... but at least this many times the current spread
    double ArmR = 1,                   // A: hedge exit armed this far beyond the hedge fill (tm.md §4)
    double HedgeStopR = 0.5,           // B: hedge stop this far behind its fill before arming
    double WarnR = 2,                  // loss warning (tm.md §5)
    double CloseR = 3,                 // loss limit: leg and its hedge closed
    int MirrorWithinSeconds = 60,      // a main older than this when first seen gets hedges only, no mirror (tm.md §6)
    int AlertMinutesBefore = 10,       // pop-up this long before each NFP/CPI release and the Friday close (tm.md §6)
    int DisconnectAlertSeconds = 60,   // pop-up when the connection has been down this long (tm.md §6)
    IReadOnlyDictionary<string, double>? FallbackDistance = null, // per symbol, in price, when the candle history is unavailable
    IReadOnlyList<string>? TakeOver = null) // labels whose positions and pending orders TM takes over as mains (tm.md §2)
{
    public const string FileName = "tm.json";

    /// <summary>TM's own bot name, the start of all its labels (tm.md §7).</summary>
    public const string BotName = "TM";

    /// <summary>The take-over list; [""] (the owner's unlabelled trades only) when the file has none.</summary>
    public IReadOnlyList<string> TakeOverLabels => TakeOver ?? [""];

    /// <summary>
    /// True if a position or pending order with this label is a main for TM (tm.md §2): "" in the list matches
    /// no label; any other entry matches labels that start with it, e.g. "AuditBot-" matches "AuditBot-77fbd8".
    /// </summary>
    public bool TakesOver(string label) =>
        TakeOverLabels.Any(t => t.Length == 0 ? label.Length == 0 : label.StartsWith(t, StringComparison.Ordinal));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static TmConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new TmConfigException($"{path} not found (example: config/{FileName} in the repo)");
        TmConfig? c;
        try
        {
            c = JsonSerializer.Deserialize<TmConfig>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new TmConfigException($"{FileName}: {ex.Message}");
        }
        return (c ?? throw new TmConfigException($"{FileName} is empty")).Validated();
    }

    internal TmConfig Validated()
    {
        if (Accounts is null || Accounts.Count == 0)
            throw new TmConfigException($"{FileName}: \"accounts\" must list at least one account");
        foreach (var a in Accounts)
        {
            if (string.IsNullOrWhiteSpace(a.Broker) || a.AccountId <= 0 || a.Symbols is null || a.Symbols.Count == 0)
                throw new TmConfigException($"{FileName}: every account needs \"broker\", \"accountId\" and \"symbols\"");
        }
        if (Accounts.GroupBy(a => a.AccountId).Any(g => g.Count() > 1))
            throw new TmConfigException($"{FileName}: an account appears twice");
        if (RangePercent <= 0 || RangeDays < 1 || SpreadFloor < 0 || ArmR <= 0 || HedgeStopR <= 0 || WarnR <= 0 || CloseR <= WarnR)
            throw new TmConfigException($"{FileName}: rule values must be above 0, and closeR above warnR");
        if (AlertMinutesBefore < 1 || DisconnectAlertSeconds < 1)
            throw new TmConfigException($"{FileName}: alertMinutesBefore and disconnectAlertSeconds must be at least 1");
        if (TakeOver is { Count: 0 } || TakeOver?.Any(t => t is null) == true)
            throw new TmConfigException($"{FileName}: \"takeOver\" must list at least one label (\"\" = no label, the owner's trades)");
        // TM's own orders (TM-<6hex>-…) must never be taken over as mains, or TM would mirror its own mirrors and hedges.
        foreach (var t in TakeOverLabels.Where(t => t.Length > 0))
            if ($"{BotName}-".StartsWith(t, StringComparison.Ordinal) || t.StartsWith($"{BotName}-", StringComparison.Ordinal))
                throw new TmConfigException($"{FileName}: \"takeOver\" entry \"{t}\" would match TM's own labels ({BotName}-…)");
        return this;
    }

    public double? Fallback(string symbol) => FallbackDistance?.GetValueOrDefault(symbol);
}

/// <param name="Broker">Folder name under ~/tradebot/logs, e.g. "brokerA".</param>
/// <param name="Symbols">Mains on these symbols are taken over; everything else stays the owner's.</param>
/// <param name="SymbolNames">Only where the broker's symbol name differs from ours.</param>
public sealed record TmAccount(string Broker, long AccountId, IReadOnlyList<string> Symbols, IReadOnlyDictionary<string, string>? SymbolNames = null)
{
    public IReadOnlyDictionary<string, string> BrokerSymbolNames => Symbols.ToDictionary(s => s, s => SymbolNames?.GetValueOrDefault(s) ?? s);
}

public sealed class TmConfigException(string message) : Exception(message);
