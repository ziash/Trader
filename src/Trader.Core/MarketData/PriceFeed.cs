using System.Collections.Concurrent;
using Google.Protobuf;

namespace Trader.Core.MarketData;

/// <summary>A bid/ask price at one moment. Time is the server's timestamp when it sent one, otherwise our receive time.</summary>
public readonly record struct Quote(long CtidTraderAccountId, long SymbolId, double Bid, double Ask, DateTime TimeUtc)
{
    public double Spread => Ask - Bid;
    public double Mid => (Bid + Ask) / 2;
}

/// <summary>
/// Turns ProtoOASpotEvent messages into complete quotes. A spot event carries only the side(s) that changed,
/// so the last bid and ask are kept per account + symbol. Feed it from ConnectionManager.MessageReceived;
/// call <see cref="Reset"/> after each reconnect (the server re-sends both sides on subscribe).
/// </summary>
public sealed class PriceFeed
{
    private readonly ConcurrentDictionary<(long Ctid, long SymbolId), (ulong Bid, ulong Ask)> _raw = new();
    private readonly ConcurrentDictionary<(long Ctid, long SymbolId), Quote> _last = new();
    private readonly Func<DateTime> _utcNow;

    public PriceFeed(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Every complete quote. Runs on the receive thread: keep handlers short.</summary>
    public event Action<Quote>? QuoteReceived;

    /// <summary>Live trendbars (candles) that arrive inside spot events, after SubscribeLiveTrendbarAsync.</summary>
    public event Action<long, long, ProtoOATrendbar>? TrendbarReceived;

    public Quote? Last(long ctid, long symbolId) => _last.TryGetValue((ctid, symbolId), out var q) ? q : null;

    public void Reset()
    {
        _raw.Clear();
        _last.Clear();
    }

    public void Handle(IMessage message)
    {
        if (message is not ProtoOASpotEvent spot)
            return;

        var key = (spot.CtidTraderAccountId, spot.SymbolId);
        var (bid, ask) = _raw.GetValueOrDefault(key);
        if (spot.HasBid) bid = spot.Bid;
        if (spot.HasAsk) ask = spot.Ask;
        _raw[key] = (bid, ask);

        foreach (var bar in spot.Trendbar)
            TrendbarReceived?.Invoke(spot.CtidTraderAccountId, spot.SymbolId, bar);

        if (bid == 0 || ask == 0 || !(spot.HasBid || spot.HasAsk))
            return;

        var time = spot.HasTimestamp ? DateTimeOffset.FromUnixTimeMilliseconds(spot.Timestamp).UtcDateTime : _utcNow();
        var quote = new Quote(spot.CtidTraderAccountId, spot.SymbolId, SymbolInfo.PriceFromRaw(bid), SymbolInfo.PriceFromRaw(ask), time);
        _last[key] = quote;
        QuoteReceived?.Invoke(quote);
    }
}
