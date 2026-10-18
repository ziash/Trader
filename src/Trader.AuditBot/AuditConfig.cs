using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Trader.AuditBot;

/// <summary>
/// One broker's Audit Bot settings: ~/tradebot/config/audit.&lt;broker&gt;.json (plan 6.3). Example:
/// <code>
/// {
///   "broker": "brokerA",                 // folder name under ~/tradebot/logs
///   "brokerName": "IC Markets",
///   "accountId": 49123893,               // ctidTraderAccountId
///   "symbols": ["AUDJPY", "EURGBP", "EURUSD", "GBPJPY", "BTCUSD", "ETHUSD"],
///   "symbolNames": { "AVAXUSD": "AVXUSD" }   // only where the broker's name differs
/// }
/// </code>
/// </summary>
public sealed partial record AuditConfig(
    string Broker,
    string BrokerName,
    long AccountId,
    IReadOnlyList<string> Symbols,
    IReadOnlyDictionary<string, string>? SymbolNames = null)
{
    /// <summary>Our symbol name → the broker's name for it.</summary>
    public IReadOnlyDictionary<string, string> BrokerSymbolNames =>
        Symbols.ToDictionary(s => s, s => SymbolNames?.GetValueOrDefault(s) ?? s);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // catch typos in field names
    };

    /// <summary>Loads every audit.*.json in <paramref name="dir"/>; throws <see cref="AuditConfigException"/> on any problem.</summary>
    public static IReadOnlyList<AuditConfig> LoadAll(string dir)
    {
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "audit.*.json").Order().ToArray() : [];
        if (files.Length == 0)
            throw new AuditConfigException($"No audit.*.json config files in {dir} (examples: config/ in the repo)");

        var configs = files.Select(Load).ToList();
        foreach (var dup in configs.GroupBy(c => c.AccountId).Where(g => g.Count() > 1))
            throw new AuditConfigException($"Account {dup.Key} appears in more than one config file");
        foreach (var dup in configs.GroupBy(c => c.Broker, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            throw new AuditConfigException($"Broker folder name '{dup.Key}' appears in more than one config file");
        return configs;
    }

    public static AuditConfig Load(string path)
    {
        AuditConfig? c;
        try
        {
            c = JsonSerializer.Deserialize<AuditConfig>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new AuditConfigException($"{Path.GetFileName(path)}: {ex.Message}");
        }
        return (c ?? throw new AuditConfigException($"{Path.GetFileName(path)} is empty")).Validated(Path.GetFileName(path));
    }

    internal AuditConfig Validated(string file)
    {
        if (Broker is null || !FolderName().IsMatch(Broker))
            throw new AuditConfigException($"{file}: \"broker\" must be a folder name (letters, digits, '-', '_'), e.g. \"brokerA\"");
        if (string.IsNullOrWhiteSpace(BrokerName))
            throw new AuditConfigException($"{file}: \"brokerName\" is missing");
        if (AccountId <= 0)
            throw new AuditConfigException($"{file}: \"accountId\" must be the ctidTraderAccountId (see ListAccounts)");
        if (Symbols is null || Symbols.Count == 0 || Symbols.Any(string.IsNullOrWhiteSpace))
            throw new AuditConfigException($"{file}: \"symbols\" must list at least one symbol");
        if (Symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Symbols.Count)
            throw new AuditConfigException($"{file}: \"symbols\" contains a duplicate");
        return this;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex FolderName();
}

public sealed class AuditConfigException(string message) : Exception(message);
