using System.Text.Json;

namespace Trader.Scout;

/// <summary>What Scout did for one setup role (scout.md §2.3): the order it placed (0 = not placed: refused, or Watch mode) and its picture.</summary>
public sealed class RoleRecord
{
    public DateTime FirstSeenUtc { get; set; }
    public long OrderId { get; set; }
    public long? PositionId { get; set; }
    public string Result { get; set; } = "";

    /// <summary>The order's picture, relative to ~/tradebot/logs (scout.md §3.1); null until drawn.</summary>
    public string? Chart { get; set; }
}

/// <summary>
/// Scout's memory per account, ~/tradebot/run/scout/&lt;broker&gt;-&lt;ctid&gt;.json: which setups already got an order or an
/// alert, so a restart never places one twice, and which positions came from Scout's orders. Keys from <see cref="Key"/>.
/// </summary>
public sealed class ScoutState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(45);

    public Dictionary<string, RoleRecord> Roles { get; set; } = [];

    private string _path = "";

    /// <summary>"EURUSD|H1|202611031400|M": symbol, timeframe, the pause's first candle, M = main stop / R = reversal stop.</summary>
    public static string Key(string symbol, string timeframe, DateTime pauseStartUtc, bool reversal) =>
        $"{symbol}|{timeframe}|{pauseStartUtc:yyyyMMddHHmm}|{(reversal ? "R" : "M")}";

    /// <summary>The other order of the same pause (the pair of Scout spec §4.4 I4).</summary>
    public static string Sibling(string key) => key[..^1] + (key[^1] == 'M' ? "R" : "M");

    public static ScoutState Load(string path)
    {
        var state = File.Exists(path) ? JsonSerializer.Deserialize<ScoutState>(File.ReadAllText(path), Json) ?? new() : new ScoutState();
        state._path = path;
        return state;
    }

    public void Save(DateTime nowUtc)
    {
        foreach (var old in Roles.Where(r => nowUtc - r.Value.FirstSeenUtc > KeepFor).Select(r => r.Key).ToList())
            Roles.Remove(old);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
