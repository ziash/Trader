using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary>Everything Scout asks of the broker, so its order handling can be tested against a broker that loses replies (review P-12).</summary>
internal interface IBrokerGateway
{
    Task<ProtoOAReconcileRes> ReconcileAsync(long accountId);
    Task<IReadOnlyList<Candle>> GetTrendbarsAsync(long accountId, long symbolId, ProtoOATrendbarPeriod period, DateTime fromUtc, DateTime toUtc);
    Task<AccountStatus> GetAccountStatusAsync(long accountId, ProtoOAReconcileRes reconcile);
    Task<IReadOnlyList<ExpectedMargin>> GetExpectedMarginAsync(long accountId, long symbolId, IEnumerable<long> volumes);
    Task<ProtoOAOrder> PlaceStopOrderAsync(long accountId, long symbolId, ProtoOATradeSide side, long volume, double price, double? stopLoss,
        string label, string clientOrderId, string comment, TimeSpan timeout, bool trailingStopLoss, DateTime expiresUtc);
    Task CancelOrderAsync(long accountId, long orderId, TimeSpan timeout);
    Task<IReadOnlyList<ProtoOAOrder>> GetOrdersAsync(long accountId, DateTime fromUtc, DateTime toUtc);
    Task<IReadOnlyList<ProtoOADeal>> GetDealsAsync(long accountId, DateTime fromUtc, DateTime toUtc);

    /// <summary>The latest bid and ask of a symbol; null before the first price.</summary>
    Quote? LastQuote(long accountId, long symbolId);
}

/// <summary>The real broker: one Open API session and the price feed of that connection.</summary>
internal sealed class SessionGateway(CTraderSession session, PriceFeed feed) : IBrokerGateway
{
    public Task<ProtoOAReconcileRes> ReconcileAsync(long accountId) => session.ReconcileAsync(accountId);

    public Task<IReadOnlyList<Candle>> GetTrendbarsAsync(long accountId, long symbolId, ProtoOATrendbarPeriod period, DateTime fromUtc, DateTime toUtc) =>
        session.GetTrendbarsAsync(accountId, symbolId, period, fromUtc, toUtc);

    public Task<AccountStatus> GetAccountStatusAsync(long accountId, ProtoOAReconcileRes reconcile) => session.GetAccountStatusAsync(accountId, reconcile);

    public Task<IReadOnlyList<ExpectedMargin>> GetExpectedMarginAsync(long accountId, long symbolId, IEnumerable<long> volumes) =>
        session.GetExpectedMarginAsync(accountId, symbolId, volumes);

    public Task<ProtoOAOrder> PlaceStopOrderAsync(long accountId, long symbolId, ProtoOATradeSide side, long volume, double price, double? stopLoss,
        string label, string clientOrderId, string comment, TimeSpan timeout, bool trailingStopLoss, DateTime expiresUtc) =>
        session.PlacePendingOrderAsync(accountId, symbolId, ProtoOAOrderType.Stop, side, volume, price, stopLoss, null, label, clientOrderId, comment, timeout,
            trailingStopLoss: trailingStopLoss, expiresUtc: expiresUtc);

    public Task CancelOrderAsync(long accountId, long orderId, TimeSpan timeout) => session.CancelOrderAsync(accountId, orderId, timeout);

    public Task<IReadOnlyList<ProtoOAOrder>> GetOrdersAsync(long accountId, DateTime fromUtc, DateTime toUtc) => session.GetOrdersAsync(accountId, fromUtc, toUtc);

    public Task<IReadOnlyList<ProtoOADeal>> GetDealsAsync(long accountId, DateTime fromUtc, DateTime toUtc) => session.GetDealsAsync(accountId, fromUtc, toUtc);

    public Quote? LastQuote(long accountId, long symbolId) => feed.Last(accountId, symbolId);
}
