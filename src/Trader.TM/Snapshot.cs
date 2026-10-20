using System.Text.RegularExpressions;
using Trader.Core.Hosting;

namespace Trader.TM;

/// <summary>An open position, as TM needs it. <see cref="Dir"/>: +1 buy (long), -1 sell (short).</summary>
internal sealed record Pos(long Id, long SymbolId, int Dir, long Volume, double Entry, double? StopLoss, double? TakeProfit, string Label, DateTime OpenUtc);

/// <summary>A pending stop order (TM's hedges are the only ones it manages).</summary>
internal sealed record Ord(long Id, long SymbolId, int Dir, long Volume, double StopPrice, string Label);

/// <summary>The account's positions and pending orders at one moment (from a reconcile).</summary>
internal sealed record Snapshot(IReadOnlyList<Pos> Positions, IReadOnlyList<Ord> Orders)
{
    public static Snapshot From(ProtoOAReconcileRes res) => new(
        res.Position.Select(p => new Pos(p.PositionId, p.TradeData.SymbolId, Dir(p.TradeData.TradeSide), p.TradeData.Volume, p.Price,
                p.HasStopLoss && p.StopLoss > 0 ? p.StopLoss : null, p.HasTakeProfit && p.TakeProfit > 0 ? p.TakeProfit : null,
                p.TradeData.Label ?? "", DateTimeOffset.FromUnixTimeMilliseconds(p.TradeData.OpenTimestamp).UtcDateTime))
            .ToList(),
        res.Order.Where(o => o.OrderType == ProtoOAOrderType.Stop && !o.ClosingOrder)
            .Select(o => new Ord(o.OrderId, o.TradeData.SymbolId, Dir(o.TradeData.TradeSide), o.TradeData.Volume, o.StopPrice, o.TradeData.Label ?? ""))
            .ToList());

    public static int Dir(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? 1 : -1;

    public static ProtoOATradeSide Side(int dir) => dir > 0 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell;
}

/// <summary>
/// TM's order labels (tm.md §7): instance ID + "P" + the main's positionId + the role, e.g.
/// <c>TM-3f9a1c-P123456789-MIR</c>. Roles: MIR (mirror), HMAIN (hedge of the main), HMIR (hedge of the mirror).
/// </summary>
internal static partial class TmLabel
{
    public const string Mirror = "MIR";
    public const string HedgeMain = "HMAIN";
    public const string HedgeMirror = "HMIR";

    public static string Leg(long mainId, string role) => $"P{mainId}-{role}";

    /// <summary>True for a label made by any TM instance; gives the main's positionId and the role.</summary>
    public static bool TryParse(string? label, string botName, out long mainId, out string role)
    {
        mainId = 0;
        role = "";
        if (!BotIdentity.TryParseLabel(label, out var bot, out _, out var leg) || bot != botName || leg is null)
            return false;
        var m = LegPattern().Match(leg);
        if (!m.Success)
            return false;
        mainId = long.Parse(m.Groups[1].Value);
        role = m.Groups[2].Value;
        return true;
    }

    [GeneratedRegex("^P([0-9]+)-(MIR|HMAIN|HMIR)$")]
    private static partial Regex LegPattern();
}
