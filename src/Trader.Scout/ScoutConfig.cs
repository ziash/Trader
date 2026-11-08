using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.Scout;

/// <summary>Watch: alerts only. Trade: Scout places the first order and TM manages it (scout.md §2.1).</summary>
public enum ScoutMode { Watch, Trade }

/// <summary>
/// ~/tradebot/config/scout.json (scout.md §2). Example and defaults: config/scout.json in the repo.
/// The setup rules themselves are Scout's (github.com/ziash/Scout, docs/spec.md §3.2).
/// </summary>
public sealed record ScoutConfig(
    IReadOnlyList<ScoutAccount> Accounts,
    double Lots = 0.1,              // per Scout order on symbols not in LotsBySymbol; TM's mirror adds the same (Scout spec §4.6)
    int ExpiryCandles = 16,         // server expiry of an unfilled order, in candles of its timeframe (Scout spec §4.2)
    int HistoryDays = 30,           // candles read at start to rebuild the detectors' state
    DetectorSettings? Setup = null,
    IReadOnlyDictionary<string, double>? LotsBySymbol = null, // own size per symbol, e.g. { "XAUUSD": 0.01 } (scout.md §2)
    double MarginUsePercent = 50,   // Scout places an order only while the margin in use, with its own orders filled, stays within this % of equity (scout.md §2.5)
    double MarginFactor = 4,        // margin an order of Scout's needs once filled, × its own: TM's mirror doubles it, the two hedges double it again
    RiskLimits? Limits = null,      // loss limits that stop new entries (scout.md §2.7); left out = the defaults of RiskLimits
    SizingSettings? Sizing = null,  // size from the money at risk instead of fixed lots (scout.md §2.8); left out = off
    bool RequireTm = true)          // no new entries unless TM is working on the account (scout.md §2.7)
{
    public const string FileName = "scout.json";
    public const string BotName = "Scout";
    public const string TmBotName = "TM";
    public const double MaxLots = 100;

    /// <summary>Lots asked for on <paramref name="symbol"/>; the broker's minimum and step may change it (SymbolInfo.VolumeForLots).</summary>
    public double LotsFor(string symbol) => LotsBySymbol is not null && LotsBySymbol.TryGetValue(symbol, out var lots) ? lots : Lots;

    public DetectorSettings SetupSettings => Setup ?? new DetectorSettings();

    public RiskLimits RiskLimitsInForce => Limits ?? new RiskLimits();

    public SizingSettings SizingInForce => Sizing ?? new SizingSettings();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ScoutConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ScoutConfigException($"{path} not found (example: config/{FileName} in the repo)");
        ScoutConfig? c;
        try
        {
            c = JsonSerializer.Deserialize<ScoutConfig>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new ScoutConfigException($"{FileName}: {ex.Message}");
        }
        return (c ?? throw new ScoutConfigException($"{FileName} is empty")).Validated();
    }

    internal ScoutConfig Validated()
    {
        if (Accounts is null || Accounts.Count == 0)
            throw new ScoutConfigException($"{FileName}: \"accounts\" must list at least one account");
        foreach (var a in Accounts)
        {
            if (string.IsNullOrWhiteSpace(a.Broker) || a.AccountId <= 0 || a.Charts is null || a.Charts.Count == 0)
                throw new ScoutConfigException($"{FileName}: every account needs \"broker\", \"accountId\" and \"charts\"");
            foreach (var c in a.Charts)
            {
                if (string.IsNullOrWhiteSpace(c.Symbol) || !Timeframes.IsKnown(c.Timeframe))
                    throw new ScoutConfigException($"{FileName}: chart \"{c.Symbol} {c.Timeframe}\": symbol and a timeframe of {string.Join(", ", Timeframes.Names)} needed");
            }
            if (a.Charts.GroupBy(c => (c.Symbol, c.Timeframe)).Any(g => g.Count() > 1))
                throw new ScoutConfigException($"{FileName}: account {a.AccountId} lists a chart twice");
        }
        if (Accounts.GroupBy(a => a.AccountId).Any(g => g.Count() > 1))
            throw new ScoutConfigException($"{FileName}: an account appears twice");
        if (Lots <= 0 || Lots > MaxLots)
            throw new ScoutConfigException($"{FileName}: \"lots\" must be above 0 and at most {MaxLots}");
        foreach (var (symbol, lots) in LotsBySymbol ?? new Dictionary<string, double>())
        {
            if (lots <= 0 || lots > MaxLots)
                throw new ScoutConfigException($"{FileName}: \"lotsBySymbol\" {symbol} must be above 0 and at most {MaxLots}");
            if (!Accounts.Any(a => a.Charts.Any(c => c.Symbol == symbol)))
                throw new ScoutConfigException($"{FileName}: \"lotsBySymbol\" {symbol} is not on any chart");
        }
        if (MarginUsePercent is <= 0 or > 100 || MarginFactor < 1)
            throw new ScoutConfigException($"{FileName}: \"marginUsePercent\" above 0 and at most 100, \"marginFactor\" at least 1");
        if (RiskLimitsInForce is { DailyLossPercent: <= 0 or > 100 } or { WeeklyLossPercent: <= 0 or > 100 } or { PeakDrawdownPercent: <= 0 or > 100 })
            throw new ScoutConfigException($"{FileName}: \"limits\" percentages must be above 0 and at most 100");
        if (SizingInForce is { RiskPercent: <= 0 or > 5 } or { Legs: < 1 or > 2 } or { SlippageSpreads: < 0 } or { MinStopSpreads: < 0 } or { LimitR: < 1 } or { MaxLossPercent: <= 0 or > 100 })
            throw new ScoutConfigException($"{FileName}: \"sizing\": riskPercent above 0 and at most 5, legs 1 or 2, slippageSpreads and minStopSpreads not negative, limitR at least 1, maxLossPercent above 0 and at most 100");
        if (ExpiryCandles < 1 || HistoryDays < 5)
            throw new ScoutConfigException($"{FileName}: \"expiryCandles\" at least 1, \"historyDays\" at least 5");
        return this;
    }
}

/// <param name="Broker">Folder name under ~/tradebot/logs, e.g. "brokerA".</param>
/// <param name="SymbolNames">Only where the broker's symbol name differs from ours.</param>
public sealed record ScoutAccount(string Broker, long AccountId, IReadOnlyList<ScoutChart> Charts, IReadOnlyDictionary<string, string>? SymbolNames = null)
{
    public IReadOnlyDictionary<string, string> BrokerSymbolNames =>
        Charts.Select(c => c.Symbol).Distinct().ToDictionary(s => s, s => SymbolNames?.GetValueOrDefault(s) ?? s);
}

/// <summary>One symbol on one timeframe ("H1", "H4", "D1").</summary>
public sealed record ScoutChart(string Symbol, string Timeframe, ScoutMode Mode = ScoutMode.Watch);

public sealed class ScoutConfigException(string message) : Exception(message);

/// <summary>The timeframes Scout runs on, and the next one up for the higher-timeframe tag (Scout spec §4.4 I1).</summary>
public static class Timeframes
{
    private static readonly Dictionary<string, (ProtoOATrendbarPeriod Period, TimeSpan Length, string? Higher)> Known = new()
    {
        ["H1"] = (ProtoOATrendbarPeriod.H1, TimeSpan.FromHours(1), "H4"),
        ["H4"] = (ProtoOATrendbarPeriod.H4, TimeSpan.FromHours(4), "D1"),
        ["D1"] = (ProtoOATrendbarPeriod.D1, TimeSpan.FromDays(1), "W1"),
        ["W1"] = (ProtoOATrendbarPeriod.W1, TimeSpan.FromDays(7), null),
    };

    public static IEnumerable<string> Names => Known.Keys.Where(k => k != "W1");
    public static bool IsKnown(string? name) => name is not null && name != "W1" && Known.ContainsKey(name);
    public static ProtoOATrendbarPeriod Period(string name) => Known[name].Period;
    public static TimeSpan Length(string name) => Known[name].Length;
    public static string? Higher(string name) => Known[name].Higher;
}
