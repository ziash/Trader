using Trader.Core.MarketData;
using Trader.Core.OpenApi;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>How the fake answers an order request (review P-12).</summary>
internal enum Reply
{
    Normal,
    LostRequest,   // times out and the broker never got it
    LostReply,     // times out but the broker did it
    Disconnect,    // the connection drops before the broker gets it
    Refused,
}

/// <summary>A small broker for Scout's tests, whose replies can be lost, late or refused on purpose.</summary>
internal sealed class FakeGateway(Func<DateTime> clock) : IBrokerGateway
{
    private long _nextId = 5000, _nextDeal = 9000;

    public Dictionary<long, IReadOnlyList<Candle>> Candles { get; } = [];
    public List<ProtoOAOrder> Orders { get; } = [];
    public List<ProtoOAPosition> Positions { get; } = [];
    public List<ProtoOAOrder> History { get; } = [];
    public List<ProtoOADeal> Deals { get; } = [];
    public Queue<Reply> PlaceReplies { get; } = new();
    public Queue<Reply> CancelReplies { get; } = new();
    public string RefuseCode { get; set; } = "TRADING_BAD_STOPS";
    public int PlaceCalls { get; private set; }
    public int CancelCalls { get; private set; }
    public bool Offline { get; set; }
    public double Equity { get; set; } = 5000;
    public double UsedMargin { get; set; }
    public double MarginPerOrder { get; set; } = 10;
    public double Bid { get; set; } = 158.0;
    public double Ask { get; set; } = 158.1;
    public bool HasQuote { get; set; } = true;
    public List<string> ClientIds { get; } = [];

    private void Online()
    {
        if (Offline)
            throw new IOException("Connection closed");
    }

    public Task<ProtoOAReconcileRes> ReconcileAsync(long accountId)
    {
        Online();
        var res = new ProtoOAReconcileRes();
        res.Order.AddRange(Orders);
        res.Position.AddRange(Positions);
        return Task.FromResult(res);
    }

    public Task<IReadOnlyList<Candle>> GetTrendbarsAsync(long accountId, long symbolId, ProtoOATrendbarPeriod period, DateTime fromUtc, DateTime toUtc)
    {
        Online();
        return Task.FromResult(period == ProtoOATrendbarPeriod.H1 && Candles.TryGetValue(symbolId, out var c) ? c : (IReadOnlyList<Candle>)[]);
    }

    public Task<AccountStatus> GetAccountStatusAsync(long accountId, ProtoOAReconcileRes reconcile) =>
        Task.FromResult(new AccountStatus(accountId, clock(), Equity, Equity, UsedMargin, Positions.Count, Orders.Count));

    public Task<IReadOnlyList<ExpectedMargin>> GetExpectedMarginAsync(long accountId, long symbolId, IEnumerable<long> volumes) =>
        Task.FromResult<IReadOnlyList<ExpectedMargin>>(volumes.Select(v => new ExpectedMargin(v, MarginPerOrder, MarginPerOrder)).ToList());

    public ProtoOAOrder AddOrder(long symbolId, ProtoOATradeSide side, long volume, double price, double? stopLoss, string label, DateTime? expiresUtc = null)
    {
        var o = new ProtoOAOrder
        {
            OrderId = _nextId++, OrderType = ProtoOAOrderType.Stop, OrderStatus = ProtoOAOrderStatus.OrderStatusAccepted, StopPrice = price,
            TradeData = new ProtoOATradeData { SymbolId = symbolId, Volume = volume, TradeSide = side, Label = label },
            UtcLastUpdateTimestamp = new DateTimeOffset(clock()).ToUnixTimeMilliseconds(),
        };
        if (stopLoss is { } sl)
            o.StopLoss = sl;
        if (expiresUtc is { } x)
            o.ExpirationTimestamp = new DateTimeOffset(x).ToUnixTimeMilliseconds();
        Orders.Add(o);
        return o;
    }

    public Task<ProtoOAOrder> PlaceStopOrderAsync(long accountId, long symbolId, ProtoOATradeSide side, long volume, double price, double? stopLoss,
        string label, string clientOrderId, string comment, TimeSpan timeout, bool trailingStopLoss, DateTime expiresUtc)
    {
        Online();
        PlaceCalls++;
        ClientIds.Add(clientOrderId);
        switch (PlaceReplies.Count > 0 ? PlaceReplies.Dequeue() : Reply.Normal)
        {
            case Reply.LostRequest:
                throw new TimeoutException("No reply within 10 s");
            case Reply.Disconnect:
                throw new IOException("Connection closed");
            case Reply.Refused:
                throw new CTraderApiException(RefuseCode, "refused");
            case Reply.LostReply:
                AddOrder(symbolId, side, volume, price, stopLoss, label, expiresUtc);
                throw new TimeoutException("No reply within 10 s");
        }
        return Task.FromResult(AddOrder(symbolId, side, volume, price, stopLoss, label, expiresUtc));
    }

    public Task CancelOrderAsync(long accountId, long orderId, TimeSpan timeout)
    {
        Online();
        CancelCalls++;
        var o = Orders.FirstOrDefault(x => x.OrderId == orderId) ?? throw new CTraderApiException("ORDER_NOT_FOUND", "no such order");
        switch (CancelReplies.Count > 0 ? CancelReplies.Dequeue() : Reply.Normal)
        {
            case Reply.LostRequest:
                throw new TimeoutException("No reply within 10 s");
            case Reply.Disconnect:
                throw new IOException("Connection closed");
            case Reply.Refused:
                throw new CTraderApiException("CANCEL_REFUSED", "refused");
            case Reply.LostReply:
                Remove(o, ProtoOAOrderStatus.OrderStatusCancelled);
                throw new TimeoutException("No reply within 10 s");
        }
        Remove(o, ProtoOAOrderStatus.OrderStatusCancelled);
        return Task.CompletedTask;
    }

    /// <summary>The order leaves the account and goes to the history with this outcome.</summary>
    public void Remove(ProtoOAOrder o, ProtoOAOrderStatus status)
    {
        Orders.Remove(o);
        o.OrderStatus = status;
        o.UtcLastUpdateTimestamp = new DateTimeOffset(clock()).ToUnixTimeMilliseconds();
        History.Add(o);
    }

    public ProtoOAPosition Fill(ProtoOAOrder o, double price)
    {
        Remove(o, ProtoOAOrderStatus.OrderStatusFilled);
        var p = new ProtoOAPosition { PositionId = _nextId++, Price = price, PositionStatus = ProtoOAPositionStatus.PositionStatusOpen, TradeData = o.TradeData.Clone() };
        Positions.Add(p);
        Deals.Add(new ProtoOADeal
        {
            DealId = _nextDeal++, OrderId = o.OrderId, PositionId = p.PositionId, SymbolId = o.TradeData.SymbolId, FilledVolume = o.TradeData.Volume, Volume = o.TradeData.Volume,
            TradeSide = o.TradeData.TradeSide, ExecutionPrice = price, ExecutionTimestamp = new DateTimeOffset(clock()).ToUnixTimeMilliseconds(),
        });
        return p;
    }

    public ProtoOADeal CloseDeal(ProtoOAPosition p, long volume, double price, long grossCents)
    {
        var d = new ProtoOADeal
        {
            DealId = _nextDeal++, OrderId = _nextId++, PositionId = p.PositionId, SymbolId = p.TradeData.SymbolId, FilledVolume = volume, Volume = volume,
            TradeSide = p.TradeData.TradeSide == ProtoOATradeSide.Buy ? ProtoOATradeSide.Sell : ProtoOATradeSide.Buy, ExecutionPrice = price,
            ExecutionTimestamp = new DateTimeOffset(clock()).ToUnixTimeMilliseconds(),
            ClosePositionDetail = new ProtoOAClosePositionDetail { GrossProfit = grossCents, MoneyDigits = 2 },
        };
        Deals.Add(d);
        return d;
    }

    public Task<IReadOnlyList<ProtoOAOrder>> GetOrdersAsync(long accountId, DateTime fromUtc, DateTime toUtc)
    {
        Online();
        return Task.FromResult<IReadOnlyList<ProtoOAOrder>>(History.ToList());
    }

    public Task<IReadOnlyList<ProtoOADeal>> GetDealsAsync(long accountId, DateTime fromUtc, DateTime toUtc)
    {
        Online();
        return Task.FromResult<IReadOnlyList<ProtoOADeal>>(Deals.ToList());
    }

    public Quote? LastQuote(long accountId, long symbolId) => HasQuote ? new Quote(accountId, symbolId, Bid, Ask, clock()) : null;
}
