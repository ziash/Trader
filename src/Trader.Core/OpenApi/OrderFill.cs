namespace Trader.Core.OpenApi;

/// <summary>A filled order, from its ORDER_FILLED execution event.</summary>
/// <param name="FilledVolume">In Open API units (hundredths of a unit).</param>
/// <param name="Commission">In the account's deposit currency; negative = charged (as the server reports it).</param>
/// <param name="Latency">Request sent → fill message received, measured on this machine.</param>
public sealed record OrderFill(
    long OrderId,
    long PositionId,
    long DealId,
    double Price,
    long FilledVolume,
    DateTime ExecutionTimeUtc,
    double Commission,
    string Label,
    TimeSpan Latency)
{
    public static OrderFill From(ProtoOAExecutionEvent e, TimeSpan latency)
    {
        var deal = e.Deal;
        var digits = deal.HasMoneyDigits ? (int)deal.MoneyDigits : 2;
        return new OrderFill(
            deal.OrderId,
            deal.PositionId,
            deal.DealId,
            deal.ExecutionPrice,
            deal.FilledVolume,
            DateTimeOffset.FromUnixTimeMilliseconds(deal.ExecutionTimestamp).UtcDateTime,
            deal.Commission / Math.Pow(10, digits),
            e.Order?.TradeData?.Label ?? e.Position?.TradeData?.Label ?? "",
            latency);
    }
}
