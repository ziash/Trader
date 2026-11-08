using System.Globalization;

namespace Trader.Core.OpenApi;

/// <summary>
/// An account's money at one moment (shared.md §4, issue-log.md #1): balance, equity (balance + the open positions' net
/// unrealized result), the margin the open positions hold, and what is left. In the account currency (USD on our demos).
/// The bots read it before they add exposure, so they see a full account coming instead of finding out from refusals.
/// </summary>
public sealed record AccountStatus(long AccountId, DateTime TimeUtc, double Balance, double Equity, double UsedMargin, int Positions, int Orders,
    ProtoOATotalMarginCalculationType MarginMode = ProtoOATotalMarginCalculationType.Sum)
{
    /// <summary>Equity not held as margin: what new positions (and hedges) can use.</summary>
    public double FreeMargin => Equity - UsedMargin;

    /// <summary>Equity ÷ used margin × 100; null with no open positions. The broker stops the account out at a low level (IC Markets: 50 %).</summary>
    public double? MarginLevel => UsedMargin > 0 ? Equity / UsedMargin * 100 : null;

    /// <summary>Open profit/loss of all positions together (net of commission and swap).</summary>
    public double Unrealized => Equity - Balance;

    /// <summary>"balance $3,810.94, equity $3,512.20, margin $2,950.00 used, $562.20 free, level 119 %".</summary>
    public string Describe() =>
        $"balance {Money(Balance)}, equity {Money(Equity)}, margin {Money(UsedMargin)} used, {Money(FreeMargin)} free, level "
        + (MarginLevel is { } l ? l.ToString("0", CultureInfo.InvariantCulture) + " %" : "– (no positions)");

    public static string Money(double v) => (v < 0 ? "-$" : "$") + Math.Abs(v).ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>
    /// From the three replies: the trader (balance), the reconcile (positions with the margin each holds) and the
    /// unrealized results. On a hedged account the broker sets how a buy and a sell on one symbol share margin
    /// (<see cref="ProtoOATrader.TotalMarginCalculationType"/>): SUM counts both, MAX only the bigger side, NET the difference.
    /// </summary>
    public static AccountStatus From(ProtoOATrader trader, ProtoOAReconcileRes reconcile, ProtoOAGetPositionUnrealizedPnLRes pnl, DateTime nowUtc)
    {
        var balance = Scale(trader.Balance, trader.HasMoneyDigits ? trader.MoneyDigits : 2);
        var unrealized = pnl.Positions.Sum(p => Scale(p.Net, pnl.MoneyDigits));
        var how = trader.HasTotalMarginCalculationType ? trader.TotalMarginCalculationType : ProtoOATotalMarginCalculationType.Sum;
        var used = reconcile.Position
            .GroupBy(p => p.TradeData.SymbolId)
            .Sum(g =>
            {
                double Side(ProtoOATradeSide side) => g.Where(p => p.TradeData.TradeSide == side).Sum(MarginOf);
                return Combine(how, Side(ProtoOATradeSide.Buy), Side(ProtoOATradeSide.Sell));
            });
        return new AccountStatus(trader.CtidTraderAccountId, nowUtc, balance, balance + unrealized, used, reconcile.Position.Count, reconcile.Order.Count, how);
    }

    /// <summary>The margin one open position holds, in the account currency.</summary>
    public static double MarginOf(ProtoOAPosition p) => Scale((long)p.UsedMargin, p.HasMoneyDigits ? p.MoneyDigits : 2);

    /// <summary>The margin of one symbol's buys and sells together, by the broker's rule: SUM counts both, MAX the larger side, NET the difference.</summary>
    public static double Combine(ProtoOATotalMarginCalculationType how, double buy, double sell) => how switch
    {
        ProtoOATotalMarginCalculationType.Max => Math.Max(buy, sell),
        ProtoOATotalMarginCalculationType.Net => Math.Abs(buy - sell),
        _ => buy + sell,
    };

    internal static double Scale(long value, uint digits) => value / Math.Pow(10, digits);
}

/// <summary>Margin a new position of <see cref="Volume"/> would hold, per side, in the account currency (ProtoOAExpectedMarginRes).</summary>
public sealed record ExpectedMargin(long Volume, double Buy, double Sell)
{
    public double For(int dir) => dir > 0 ? Buy : Sell;
}
