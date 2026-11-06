using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Trader.Scout;

/// <summary>One option of the catalogue ~/tradebot/config/scout-controls.json (dashboard.md §3.4, scout.md §2.6).</summary>
public sealed record ControlOption(string Key, string Scope, string Type, IReadOnlyList<string>? Values);

/// <summary>What the dashboard may set: only these options, only to these values. Edited on the laptop only.</summary>
public sealed record ControlCatalogue(int Version, IReadOnlyList<ControlOption> Options)
{
    public const string FileName = "scout-controls.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public static ControlCatalogue? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ControlCatalogue>(File.ReadAllText(path), Json) : null;

    public ControlOption? Find(string scope, string key) => Options.FirstOrDefault(o => o.Scope == scope && o.Key == key);
}

/// <summary>The control file the site writes (dashboard.md §3.4): only what the owner changed; anything absent = as configured.</summary>
public sealed class ControlFile
{
    public long Version { get; set; }
    [JsonPropertyName("updated_utc")] public string? UpdatedUtc { get; set; }
    public string? Account { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>> Symbols { get; set; } = [];
    public Dictionary<string, SetupControl> Setups { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Null when the file is not there; throws <see cref="JsonException"/> when it cannot be read.</summary>
    public static ControlFile? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ControlFile>(File.ReadAllText(path), Json) ?? throw new JsonException("empty control file") : null;
}

public sealed class SetupControl
{
    public string? Decision { get; set; }
    public string? Utc { get; set; }
}

/// <summary>One entry of the control file and what Scout made of it, for the acknowledgement (scout.md §2.6).</summary>
public sealed record ControlEntry(string Scope, string Target, string Key, string Value, bool Applied, string? Reason)
{
    public List<long> Cancelled { get; } = [];
}

/// <summary>
/// The controls in force on one account (scout.md §2.6): symbols switched off, symbols on another timeframe, setups skipped.
/// <see cref="Evaluate"/> checks every entry of the control file against the catalogue and the account's charts; a bad entry
/// is refused on its own and the rest still apply.
/// </summary>
public sealed class Controls
{
    /// <summary>The options this Scout knows how to apply; any other catalogue entry is refused ("needs a newer Scout").</summary>
    public static readonly string[] Known = ["symbol.enabled", "symbol.timeframe", "setup.decision"];

    public static readonly TimeSpan SetupsKeptFor = TimeSpan.FromDays(7);

    private static readonly Regex SetupKey = new(@"^([A-Za-z0-9._]{1,32})-([A-Z][0-9])-(\d{12})$");

    public static Controls None { get; } = new();

    public long Version { get; private init; }
    public HashSet<string> Disabled { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Timeframes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);
    public List<ControlEntry> Entries { get; } = [];

    /// <summary>"EURUSD|H1|202611031400|M" (Scout's memory key) → "EURUSD-H1-202611031400" (the setup key the site uses).</summary>
    public static string SetupOf(string roleKey)
    {
        var p = roleKey.Split('|');
        return $"{p[0]}-{p[1]}-{p[2]}";
    }

    /// <summary>The timeframe Scout should read <paramref name="symbol"/> on: the control's, else the configured one.</summary>
    public string TimeframeFor(string symbol, string configured) => Timeframes.GetValueOrDefault(symbol, configured);

    /// <summary>Why an order for this setup role must not be placed now, or null.</summary>
    public string? HoldBack(string roleKey)
    {
        var symbol = roleKey.Split('|')[0];
        if (Disabled.Contains(symbol))
            return "symbol switched off on the dashboard";
        return Skipped.Contains(SetupOf(roleKey)) ? "skipped on the dashboard" : null;
    }

    /// <summary>
    /// Whether to cancel one of Scout's pending orders, and why (null = leave it). One cancels the other comes first; then the
    /// controls; an order from a chart Scout no longer reads (timeframe changed) stays until filled or expired (owner, 06-Nov-2026);
    /// otherwise it goes when its setup ended.
    /// </summary>
    public string? CancelReason(string roleKey, bool siblingFilled, bool chartActive, bool wanted, string? endReason)
    {
        if (siblingFilled)
            return "the other order of the pair filled";
        if (HoldBack(roleKey) is { } held)
            return held;
        if (!chartActive || wanted)
            return null;
        return endReason ?? "setup ended";
    }

    public static Controls Evaluate(ControlFile? file, ControlCatalogue? catalogue, ScoutAccount account, DateTime nowUtc)
    {
        if (file is null)
            return None;
        var c = new Controls { Version = file.Version };
        var accountId = $"{account.Broker}-{account.AccountId}";
        void Refuse(string scope, string target, string key, string value, string why) => c.Entries.Add(new(scope, target, key, value, false, why));

        foreach (var (symbol, options) in file.Symbols)
        foreach (var (key, raw) in options)
        {
            var value = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.ToString();
            var why = Check(file, catalogue, accountId, "symbol", key, value);
            var charts = account.Charts.Where(ch => ch.Symbol == symbol).ToList();
            if (why is null && charts.Count == 0)
                why = $"{symbol} is not in scout.json for this account";
            if (why is null && key == "timeframe" && charts.Count > 1)
                why = $"{symbol} has {charts.Count} charts in scout.json; a timeframe can be set only for a symbol with one";
            if (why is null && key == "enabled" && raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                why = "on / off expected";
            if (why is not null)
            {
                Refuse("symbol", symbol, key, value, why);
                continue;
            }
            if (key == "enabled" && !raw.GetBoolean())
                c.Disabled.Add(symbol);
            if (key == "timeframe" && value != charts[0].Timeframe)
                c.Timeframes[symbol] = value;
            c.Entries.Add(new("symbol", symbol, key, value, true, null));
        }

        foreach (var (setup, s) in file.Setups)
        {
            var value = s.Decision ?? "";
            if (DateTime.TryParseExact(s.Utc, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) && nowUtc - at > SetupsKeptFor)
                continue; // older than 7 days: dropped
            var why = Check(file, catalogue, accountId, "setup", "decision", value);
            if (why is null && SetupKey.Match(setup) is not { Success: true } m)
                why = "not a setup key (symbol-timeframe-yyyyMMddHHmm)";
            else if (why is null && !account.Charts.Any(ch => ch.Symbol == SetupKey.Match(setup).Groups[1].Value))
                why = $"{SetupKey.Match(setup).Groups[1].Value} is not in scout.json for this account";
            if (why is not null)
            {
                Refuse("setup", setup, "decision", value, why);
                continue;
            }
            if (value == "skip")
                c.Skipped.Add(setup);
            c.Entries.Add(new("setup", setup, "decision", value, true, null));
        }
        return c;
    }

    private static string? Check(ControlFile file, ControlCatalogue? catalogue, string accountId, string scope, string key, string value)
    {
        if (file.Account is { Length: > 0 } a && a != accountId)
            return $"the file is for {a}, not {accountId}";
        if (catalogue is null)
            return $"no catalogue ({ControlCatalogue.FileName}) on the laptop";
        if (catalogue.Find(scope, key) is not { } option)
            return $"{scope} option \"{key}\" is not in the catalogue";
        if (!Known.Contains($"{scope}.{key}"))
            return "needs a newer Scout";
        if (option.Type == "choice" && option.Values is { } values && !values.Contains(value))
            return $"\"{value}\" is not one of {string.Join(", ", values)}";
        return null;
    }
}

/// <summary>scout-&lt;broker&gt;-&lt;ctid&gt;.ack.json: what Scout made of the control file (scout.md §2.6), carried up by the sync.</summary>
public sealed record ControlAck(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("applied_utc")] string AppliedUtc,
    [property: JsonPropertyName("instance")] string Instance,
    [property: JsonPropertyName("knows")] IReadOnlyList<string> Knows,
    [property: JsonPropertyName("entries")] IReadOnlyList<AckEntry> Entries,
    [property: JsonPropertyName("error")] string? Error = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record AckEntry(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("cancelled")] IReadOnlyList<long> Cancelled);
