using System.Text.RegularExpressions;
using Trader.Core.Hosting;

namespace Trader.TM;

/// <summary>An open position, as TM needs it. <see cref="Dir"/>: +1 buy (long), -1 sell (short); <see cref="Trailing"/>: its stop-loss trails.</summary>
internal sealed record Pos(long Id, long SymbolId, int Dir, long Volume, double Entry, double? StopLoss, double? TakeProfit, string Label, DateTime OpenUtc,
    bool Trailing = false);

/// <summary>
/// A pending order: stop (TM's hedges; the owner's breakout entries) or limit (<see cref="Limit"/>), at <see cref="Price"/>,
/// with the stop-loss, take-profit and trailing switch its position gets when it fills. The owner's own (unlabelled)
/// ones are mirrored by TM (tm.md §3).
/// </summary>
internal sealed record Ord(long Id, long SymbolId, int Dir, long Volume, double Price, string Label, bool Limit = false,
    double? StopLoss = null, double? TakeProfit = null, bool Trailing = false)
{
    public ProtoOAOrderType Type => Limit ? ProtoOAOrderType.Limit : ProtoOAOrderType.Stop;

    /// <summary>"sell-stop", "buy-limit" …</summary>
    public string Name => $"{(Dir > 0 ? "buy" : "sell")}-{(Limit ? "limit" : "stop")}";
}

/// <summary>The account's positions and pending orders at one moment (from a reconcile).</summary>
internal sealed record Snapshot(IReadOnlyList<Pos> Positions, IReadOnlyList<Ord> Orders)
{
    public static Snapshot From(ProtoOAReconcileRes res) => new(
        res.Position.Select(p => new Pos(p.PositionId, p.TradeData.SymbolId, Dir(p.TradeData.TradeSide), p.TradeData.Volume, p.Price,
                p.HasStopLoss && p.StopLoss > 0 ? p.StopLoss : null, p.HasTakeProfit && p.TakeProfit > 0 ? p.TakeProfit : null,
                p.TradeData.Label ?? "", DateTimeOffset.FromUnixTimeMilliseconds(p.TradeData.OpenTimestamp).UtcDateTime,
                p.HasStopLoss && p.StopLoss > 0 && p.TrailingStopLoss))
            .ToList(),
        res.Order.Where(o => o.OrderType is ProtoOAOrderType.Stop or ProtoOAOrderType.Limit && !o.ClosingOrder)
            .Select(o => new Ord(o.OrderId, o.TradeData.SymbolId, Dir(o.TradeData.TradeSide), o.TradeData.Volume,
                o.OrderType == ProtoOAOrderType.Limit ? o.LimitPrice : o.StopPrice, o.TradeData.Label ?? "", o.OrderType == ProtoOAOrderType.Limit,
                o.HasStopLoss && o.StopLoss > 0 ? o.StopLoss : null, o.HasTakeProfit && o.TakeProfit > 0 ? o.TakeProfit : null,
                o.HasStopLoss && o.StopLoss > 0 && o.TrailingStopLoss))
            .ToList());

    public static int Dir(ProtoOATradeSide side) => side == ProtoOATradeSide.Buy ? 1 : -1;

    public static ProtoOATradeSide Side(int dir) => dir > 0 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell;
}

/// <summary>
/// TM's order labels (tm.md §7): instance ID + "P" + the main's positionId + the role, e.g.
/// <c>TM-3f9a1c-P123456789-MIR</c>. Roles: MIR (mirror), HMAIN (hedge of the main), HMIR (hedge of the mirror).
/// A mirror order of the owner's pending order carries "O" + the owner's orderId instead (its position keeps that
/// label after the fill): <c>TM-3f9a1c-O123456789-MIR</c>.
/// </summary>
internal static partial class TmLabel
{
    public const string Mirror = "MIR";
    public const string HedgeMain = "HMAIN";
    public const string HedgeMirror = "HMIR";

    public static string Leg(long mainId, string role) => $"P{mainId}-{role}";

    /// <summary>The mirror order of the owner's pending order <paramref name="mainOrderId"/>.</summary>
    public static string OrderLeg(long mainOrderId) => $"O{mainOrderId}-{Mirror}";

    /// <summary>True for a label made by any TM instance; gives the main's positionId and the role.</summary>
    public static bool TryParse(string? label, string botName, out long mainId, out string role) =>
        TryParse(label, botName, out mainId, out role, out var byOrder) && !byOrder;

    /// <summary>As above; <paramref name="byOrder"/> = linked by the owner's orderId ("O…", a mirror order), not a positionId.</summary>
    public static bool TryParse(string? label, string botName, out long id, out string role, out bool byOrder)
    {
        id = 0;
        role = "";
        byOrder = false;
        if (!BotIdentity.TryParseLabel(label, out var bot, out _, out var leg) || bot != botName || leg is null)
            return false;
        var m = LegPattern().Match(leg);
        if (!m.Success || m.Groups[1].Value == "O" && m.Groups[3].Value != Mirror)
            return false;
        byOrder = m.Groups[1].Value == "O";
        id = long.Parse(m.Groups[2].Value);
        role = m.Groups[3].Value;
        return true;
    }

    [GeneratedRegex("^(P|O)([0-9]+)-(MIR|HMAIN|HMIR)$")]
    private static partial Regex LegPattern();
}
