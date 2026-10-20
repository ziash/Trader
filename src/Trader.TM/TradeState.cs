using System.Text.Json;
using Trader.Core.OpenApi;

namespace Trader.TM;

/// <summary>
/// What TM remembers about one trade (a main and its mirror) beyond what the server shows: the hedge level, each
/// leg's R, hedge results so far, and which steps are done. Saved to ~/tradebot/run/tm/ after every change, so a
/// restart carries on where it stopped. Everything else (positions, orders, prices) is read from the server.
/// </summary>
internal sealed class TradeState
{
    public long MainId { get; set; }
    public long SymbolId { get; set; }
    public string Symbol { get; set; } = "";

    /// <summary>+1 long, -1 short (the main's side; the mirror has the same, hedges the opposite).</summary>
    public int Dir { get; set; }

    /// <summary>Hedge level H: price of both hedge orders.</summary>
    public double H { get; set; }

    public DateTime StartedUtc { get; set; }

    /// <summary>Found after it was opened (TM was down): hedges only, no mirror (tm.md §6).</summary>
    public bool NoMirror { get; set; }

    public bool MirrorRequested { get; set; }

    public LegState Main { get; set; } = new();
    public LegState? Mirror { get; set; }

    public IEnumerable<(LegState Leg, string Role, string HedgeRole)> Legs()
    {
        yield return (Main, "MAIN", TmLabel.HedgeMain);
        if (Mirror is not null)
            yield return (Mirror, TmLabel.Mirror, TmLabel.HedgeMirror);
    }

    public bool Done => Main.Done && (Mirror?.Done ?? true) && (NoMirror || Mirror is not null || !MirrorRequested);
}

/// <summary>One short or long leg (the main or the mirror) and its hedge.</summary>
internal sealed class LegState
{
    public long PositionId { get; set; }
    public double Entry { get; set; }
    public long Volume { get; set; }

    /// <summary>1R in price: |H − entry|.</summary>
    public double R { get; set; }

    /// <summary>Stop-loss at break-even placed at 1R; the hedge is gone for good.</summary>
    public bool Protected { get; set; }

    /// <summary>The leg's original stop-loss has been removed (it is hedged).</summary>
    public bool StopCleared { get; set; }

    public bool Warned { get; set; }

    /// <summary>Result of this leg's hedges closed so far, in price per unit (positive = gain).</summary>
    public double Realized { get; set; }

    /// <summary>The hedge position while it is open, and its fill price.</summary>
    public long? HedgePositionId { get; set; }
    public double HedgeFill { get; set; }
    public bool Armed { get; set; }

    /// <summary>A hedge of this leg has filled before (re-hedging then never opens at market).</summary>
    public bool EverHedged { get; set; }

    /// <summary>The pending hedge sits 1R beyond the price (after a +1 pip exit) and moves to H when it can.</summary>
    public bool Raised { get; set; }

    public bool Done { get; set; }
}

/// <summary>All trades of one account, kept in a JSON file.</summary>
internal sealed class TradeBook(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string _saved = "";

    public List<TradeState> Trades { get; private set; } = [];

    public static TradeBook Load(string path)
    {
        var book = new TradeBook(path);
        if (File.Exists(path))
        {
            book._saved = File.ReadAllText(path);
            book.Trades = JsonSerializer.Deserialize<List<TradeState>>(book._saved) ?? [];
        }
        return book;
    }

    public TradeState? Find(long mainId) => Trades.FirstOrDefault(t => t.MainId == mainId);

    /// <summary>Writes the file if anything changed (temp file, then rename).</summary>
    public void Save()
    {
        var text = JsonSerializer.Serialize(Trades, Json);
        if (text == _saved || string.IsNullOrEmpty(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
        _saved = text;
    }
}

/// <summary>Something TM wants done on the server, or a message to the owner. Executed in order by <see cref="AccountManager"/>.</summary>
internal abstract record TmAction(TradeState? Trade, string Role)
{
    /// <summary>Called after the server confirmed it (with the fill for market orders).</summary>
    public Action<OrderFill?>? Done { get; init; }

    /// <summary>Called when the server refused it.</summary>
    public Action<string>? Failed { get; init; }
}

internal sealed record OpenMirror(TradeState Trade, int Dir, long Volume, double StopLoss) : TmAction(Trade, TmLabel.Mirror);

/// <param name="Price">Stop price; null = at market (price already beyond H when no hedge existed yet).</param>
internal sealed record PlaceHedge(TradeState Trade, string Role, int Dir, long Volume, double? Price) : TmAction(Trade, Role);

internal sealed record MoveOrder(TradeState Trade, string Role, long OrderId, double Price) : TmAction(Trade, Role);

internal sealed record CancelOrder(TradeState Trade, string Role, long OrderId) : TmAction(Trade, Role);

/// <summary>Sets a position's stop-loss and take-profit (null removes it).</summary>
internal sealed record SetProtection(TradeState Trade, string Role, long PositionId, double? StopLoss, double? TakeProfit) : TmAction(Trade, Role);

internal sealed record ClosePosition(TradeState Trade, string Role, long PositionId, long Volume) : TmAction(Trade, Role);

internal sealed record Notify(TradeState? Trade, string Role, Trader.Core.Notifications.NotificationLevel Level, string Event, string Title, string Message)
    : TmAction(Trade, Role);

/// <summary>The default hedge distance for this symbol is needed (candle history to fetch).</summary>
internal sealed record NeedDistance(long SymbolId) : TmAction(null, "");
