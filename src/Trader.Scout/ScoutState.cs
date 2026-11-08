using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.Scout;

/// <summary>
/// Where one setup role's order stands (scout.md §2.3, review P-01). Saved before and after every broker call, so a timeout or a
/// restart never leaves it to a guess: the broker's own state decides.
/// </summary>
public enum RoleState
{
    Alerted,              // Watch mode: alert sent, no order
    Intended,             // may be sent (again): a send that never reached the broker, or a temporary refusal
    Sending,              // the request is going out (found in this state after a restart: treated as Unknown)
    Unknown,              // timeout or lost connection: the broker may or may not have the order
    Working,              // on the broker, waiting
    Refused,              // the broker refused it, or its level had already passed
    Abandoned,            // never reached the broker and may not be sent again
    Filled,               // has a position
    Closed,               // its position is fully closed
    Expired,
    CancelRequested,      // Scout wants it gone; the broker has not confirmed yet (retried with backoff)
    Cancelled,            // cancelled by Scout, confirmed by the broker
    Held,                 // cancelled by Scout for a control or a halt; placed again when released
    CancelledExternally,  // cancelled by the owner or TM
}

/// <summary>What Scout did for one setup role (scout.md §2.3): the order it placed (0 = not placed: refused, or Watch mode) and its picture.</summary>
public sealed class RoleRecord
{
    public DateTime FirstSeenUtc { get; set; }
    public long OrderId { get; set; }
    public long? PositionId { get; set; }
    public string Result { get; set; } = "";

    /// <summary>The order's picture, relative to ~/tradebot/logs (scout.md §3.1); null until drawn.</summary>
    public string? Chart { get; set; }

    public RoleState? State { get; set; }
    public int Attempts { get; set; }                  // sends so far
    public DateTime? LastSendUtc { get; set; }
    public int ReconcilesSinceSend { get; set; }       // reconciles that did not show the order after a send
    public DateTime? ExpiresUtc { get; set; }
    public int CancelAttempts { get; set; }
    public DateTime? NextCancelUtc { get; set; }
    public bool CancelAlerted { get; set; }
    public bool PlaceLogged { get; set; }              // orders.csv has its place row
    public bool FillLogged { get; set; }               // orders.csv has its filled row
    public List<long> LoggedDeals { get; set; } = [];  // closing deals already in orders.csv

    /// <summary>Only events from this time on are written when recovering from the broker's history; set on records older than this feature.</summary>
    public DateTime? LogFromUtc { get; set; }

    /// <summary>The broker can still change this role: it is checked against the broker's state and history.</summary>
    [JsonIgnore]
    public bool IsOpen => State is RoleState.Sending or RoleState.Unknown or RoleState.Working or RoleState.CancelRequested or RoleState.Filled;

    /// <summary>A send may be in the broker's hands with no answer yet.</summary>
    [JsonIgnore]
    public bool IsInDoubt => State is RoleState.Sending or RoleState.Unknown;
}

/// <summary>
/// Scout's memory per account, ~/tradebot/run/scout/&lt;broker&gt;-&lt;ctid&gt;.json: which setups already got an order or an
/// alert, so a restart never places one twice, and which positions came from Scout's orders. Keys from <see cref="Key"/>.
/// </summary>
public sealed class ScoutState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(45);

    public Dictionary<string, RoleRecord> Roles { get; set; } = [];

    /// <summary>The equity marks behind the loss limits (scout.md §2.7); null until the first account status.</summary>
    public EquityMarks? Equity { get; set; }

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
        state.Migrate(DateTime.UtcNow);
        return state;
    }

    // Records from before the order states: the state follows from what was written; a blank result is a send that timed out.
    private void Migrate(DateTime nowUtc)
    {
        foreach (var r in Roles.Values.Where(r => r.State is null))
        {
            r.State = r.PositionId is not null ? RoleState.Filled
                : r.Result == "placed" ? RoleState.Working
                : r.Result == "alerted" ? RoleState.Alerted
                : r.Result.StartsWith("held", StringComparison.Ordinal) ? RoleState.Held
                : r.Result.StartsWith("refused", StringComparison.Ordinal) ? RoleState.Refused
                : r.OrderId != 0 ? RoleState.Working
                : RoleState.Unknown;
            if (r.State == RoleState.Unknown)
                r.LastSendUtc = r.FirstSeenUtc;
            r.PlaceLogged = r.OrderId != 0;
            r.FillLogged = r.PositionId is not null;
            r.LogFromUtc = nowUtc;
        }
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
