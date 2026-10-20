using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;
using Serilog;
using Trader.Core.Config;
using Trader.Core.Logging;
using Trader.Core.MarketData;

namespace Trader.Core.OpenApi;

/// <summary>An error reply from the Open API (ProtoOAErrorRes / ProtoErrorRes).</summary>
public sealed class CTraderApiException(string errorCode, string? description)
    : Exception($"{errorCode}: {description}")
{
    public string ErrorCode { get; } = errorCode;

    /// <summary>The access token is expired or invalid; a token refresh should fix it.</summary>
    public bool IsTokenProblem =>
        ErrorCode is "CH_ACCESS_TOKEN_INVALID" or "OA_AUTH_TOKEN_EXPIRED" or "ACCESS_TOKEN_EXPIRED" ||
        ErrorCode.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One TLS connection to the cTrader Open API (demo host only), on top of Spotware's OpenAPI.Net client.
///
/// - Requests carry a unique clientMsgId; replies with the same ID go to that request, so several
///   requests can be in flight at once. Orders get several replies (accepted, filled, …) under one ID.
/// - Every decoded incoming message (replies and server events such as spots or fills) is also
///   raised on <see cref="MessageReceived"/>.
/// - <see cref="Closed"/> completes once when the connection ends, for any reason.
///
/// A session is used once: after it closes, create a new one (the library disposes itself on errors).
/// </summary>
public sealed class CTraderSession : IAsyncDisposable
{
    // The library sends a heartbeat when nothing was sent for this long, checking at the same interval,
    // so the gap between our messages is at most 2 × 5 = 10 s — Spotware's required rate.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly OpenClient _client;
    private readonly ILogger _log;
    private readonly IDisposable _subscription;
    // Requests waiting for replies, by clientMsgId. OnMessage returns true when the request needs no more replies.
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    private sealed record Pending(Func<IMessage, bool> OnMessage, Action<Exception> Fail);
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastReceivedTicks = DateTime.UtcNow.Ticks;
    private volatile bool _disposing;

    private CTraderSession(OpenClient client, ILogger log)
    {
        _client = client;
        _log = log;
        _subscription = client.Subscribe(OnMessage, OnError, () => Close(null));
    }

    /// <summary>Raised for every decoded incoming message. Handlers run on the receive thread: keep them short.</summary>
    public event Action<IMessage>? MessageReceived;

    /// <summary>Completes when the connection ends: null after a normal close, otherwise the error.</summary>
    public Task<Exception?> Closed => _closed.Task;

    public DateTime LastReceivedUtc => new(Interlocked.Read(ref _lastReceivedTicks), DateTimeKind.Utc);

    public string Host => _client.Host;

    public static async Task<CTraderSession> ConnectAsync(ApiEnvironment env, ILogger log)
    {
        var host = env switch
        {
            ApiEnvironment.Demo => ApiInfo.DemoHost,
            _ => throw new InvalidOperationException($"Environment {env} is not allowed"),
        };

        // maxRequestPerSecond: Spotware allows 50/s per connection; stay well under it.
        var client = new OpenClient(host, ApiInfo.Port, Heartbeat, maxRequestPerSecond: 40, useWebSocket: false);
        var session = new CTraderSession(client, log);
        log.Event("Connecting").Information("Connecting to {Host}:{Port} (TLS)", host, ApiInfo.Port);
        try
        {
            await client.Connect();
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        log.Event("Connected").Information("Connected");
        return session;
    }

    /// <summary>Authenticates the application (client ID + secret). Must be the first request.</summary>
    public Task AuthenticateApplicationAsync(TraderSecrets secrets) =>
        RequestAsync<ProtoOAApplicationAuthRes>(new ProtoOAApplicationAuthReq
        {
            ClientId = secrets.ClientId,
            ClientSecret = secrets.ClientSecret,
        });

    /// <summary>Lists every trading account the access token was granted for.</summary>
    public async Task<IReadOnlyList<ProtoOACtidTraderAccount>> GetAccountsAsync(TraderSecrets secrets)
    {
        var res = await RequestAsync<ProtoOAGetAccountListByAccessTokenRes>(
            new ProtoOAGetAccountListByAccessTokenReq { AccessToken = secrets.AccessToken });
        return res.CtidTraderAccount;
    }

    /// <summary>Authenticates one trading account on this connection.</summary>
    public Task AuthenticateAccountAsync(long ctidTraderAccountId, TraderSecrets secrets) =>
        RequestAsync<ProtoOAAccountAuthRes>(new ProtoOAAccountAuthReq
        {
            CtidTraderAccountId = ctidTraderAccountId,
            AccessToken = secrets.AccessToken,
        });

    /// <summary>Account details (balance, leverage, hedging/netting). Account must be authenticated.</summary>
    public async Task<ProtoOATrader> GetTraderAsync(long ctidTraderAccountId)
    {
        var res = await RequestAsync<ProtoOATraderRes>(new ProtoOATraderReq { CtidTraderAccountId = ctidTraderAccountId });
        return res.Trader;
    }

    /// <summary>Assets (currencies, metals …) known to the account; used to name the deposit currency.</summary>
    public async Task<IReadOnlyList<ProtoOAAsset>> GetAssetsAsync(long ctidTraderAccountId)
    {
        var res = await RequestAsync<ProtoOAAssetListRes>(new ProtoOAAssetListReq { CtidTraderAccountId = ctidTraderAccountId });
        return res.Asset;
    }

    /// <summary>All symbols of the account, light form (ID, name, assets, enabled).</summary>
    public async Task<IReadOnlyList<ProtoOALightSymbol>> GetSymbolsAsync(long ctidTraderAccountId)
    {
        var res = await RequestAsync<ProtoOASymbolsListRes>(new ProtoOASymbolsListReq { CtidTraderAccountId = ctidTraderAccountId });
        return res.Symbol;
    }

    /// <summary>Full symbol details: digits, pip position, lot size, trading schedule …</summary>
    public async Task<IReadOnlyList<ProtoOASymbol>> GetSymbolDetailsAsync(long ctidTraderAccountId, IEnumerable<long> symbolIds)
    {
        var req = new ProtoOASymbolByIdReq { CtidTraderAccountId = ctidTraderAccountId };
        req.SymbolId.AddRange(symbolIds);
        var res = await RequestAsync<ProtoOASymbolByIdRes>(req);
        return res.Symbol;
    }

    /// <summary>Starts ProtoOASpotEvent (bid/ask) messages for the symbols, with server timestamps.</summary>
    public Task SubscribeSpotsAsync(long ctidTraderAccountId, IEnumerable<long> symbolIds)
    {
        var req = new ProtoOASubscribeSpotsReq { CtidTraderAccountId = ctidTraderAccountId, SubscribeToSpotTimestamp = true };
        req.SymbolId.AddRange(symbolIds);
        return RequestAsync<ProtoOASubscribeSpotsRes>(req);
    }

    /// <summary>Adds live candles of <paramref name="period"/> to the spot events of a symbol (spots must be subscribed first).</summary>
    public Task SubscribeLiveTrendbarAsync(long ctidTraderAccountId, long symbolId, ProtoOATrendbarPeriod period) =>
        RequestAsync<ProtoOASubscribeLiveTrendbarRes>(new ProtoOASubscribeLiveTrendbarReq
        {
            CtidTraderAccountId = ctidTraderAccountId,
            SymbolId = symbolId,
            Period = period,
        });

    /// <summary>Exchanges the refresh token for a new token pair. The old refresh token stops working.</summary>
    public async Task<TokenPair> RefreshTokenAsync(string refreshToken)
    {
        var res = await RequestAsync<ProtoOARefreshTokenRes>(new ProtoOARefreshTokenReq { RefreshToken = refreshToken });
        return new TokenPair(res.AccessToken, res.RefreshToken, TimeSpan.FromSeconds(res.ExpiresIn));
    }

    /// <summary>Cheap request used to check that the line is alive.</summary>
    public Task PingAsync(TimeSpan timeout) =>
        RequestAsync<ProtoOAVersionRes>(new ProtoOAVersionReq(), timeout);

    /// <summary>Sends <paramref name="request"/> and waits for its reply, or throws on an error reply / timeout / disconnect.</summary>
    public Task<TRes> RequestAsync<TRes>(IMessage request, TimeSpan? timeout = null)
        where TRes : class, IMessage =>
        SendAsync<TRes>(request, (message, done) =>
        {
            switch (message)
            {
                case TRes res: done.TrySetResult(res); break;
                case ProtoOAErrorRes err: done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description)); break;
                case ProtoErrorRes err: done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description)); break;
                default: done.TrySetException(new InvalidOperationException($"Unexpected reply {message.GetType().Name} to {request.GetType().Name}")); break;
            }
            return true;
        }, timeout);

    /// <summary>
    /// Opens a market order and waits until it is filled (or rejected). Returns the fill: price, position,
    /// commission and latency (request sent → fill message received). Throws <see cref="CTraderApiException"/>
    /// when the broker rejects it.
    /// </summary>
    /// <param name="volume">In Open API units: hundredths of a unit (0.01 lot EURUSD = 1,000 units = 100,000).</param>
    /// <param name="relativeStopLoss">Optional server-side stop-loss distance in 1/100,000 of the price.</param>
    /// <param name="comment">Optional order comment (max 512 characters), shown in cTrader next to the label;
    /// use <see cref="SentComment"/> to record the send time.</param>
    public Task<OrderFill> PlaceMarketOrderAsync(long ctidTraderAccountId, long symbolId, ProtoOATradeSide side, long volume,
        string label, string clientOrderId, long? relativeStopLoss = null, TimeSpan? timeout = null, string? comment = null)
    {
        var req = new ProtoOANewOrderReq
        {
            CtidTraderAccountId = ctidTraderAccountId,
            SymbolId = symbolId,
            OrderType = ProtoOAOrderType.Market,
            TradeSide = side,
            Volume = volume,
            Label = label,
            ClientOrderId = clientOrderId,
        };
        if (relativeStopLoss is { } sl)
            req.RelativeStopLoss = sl;
        if (!string.IsNullOrEmpty(comment))
            req.Comment = comment.Length <= 512 ? comment : comment[..512];
        return ExecuteAsync(req, timeout);
    }

    /// <summary>Order comment with the send time (open-code-pieces.md §4): "sent 2026-10-19 08:00:01.234Z" (UTC, milliseconds).</summary>
    public static string SentComment(DateTime utc) =>
        "sent " + utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "Z";

    /// <summary>Closes (part of) a position by its ID and waits for the fill. Never closes anything else.</summary>
    public Task<OrderFill> ClosePositionAsync(long ctidTraderAccountId, long positionId, long volume, TimeSpan? timeout = null) =>
        ExecuteAsync(new ProtoOAClosePositionReq { CtidTraderAccountId = ctidTraderAccountId, PositionId = positionId, Volume = volume }, timeout);

    /// <summary>
    /// Places a pending STOP order (a buy-stop above the price or a sell-stop below it) and waits until the server
    /// accepts it. Returns the accepted order (its <c>OrderId</c> is used to move or cancel it).
    /// </summary>
    public Task<ProtoOAOrder> PlaceStopOrderAsync(long ctidTraderAccountId, long symbolId, ProtoOATradeSide side, long volume,
        double stopPrice, string label, string clientOrderId, string? comment = null, TimeSpan? timeout = null)
    {
        var req = new ProtoOANewOrderReq
        {
            CtidTraderAccountId = ctidTraderAccountId,
            SymbolId = symbolId,
            OrderType = ProtoOAOrderType.Stop,
            TradeSide = side,
            Volume = volume,
            StopPrice = stopPrice,
            Label = label,
            ClientOrderId = clientOrderId,
        };
        if (!string.IsNullOrEmpty(comment))
            req.Comment = comment.Length <= 512 ? comment : comment[..512];
        return AcknowledgeAsync(req, timeout);
    }

    /// <summary>Moves a pending stop order to <paramref name="stopPrice"/>; waits for the server's confirmation.</summary>
    public Task<ProtoOAOrder> AmendStopOrderAsync(long ctidTraderAccountId, long orderId, double stopPrice, TimeSpan? timeout = null) =>
        AcknowledgeAsync(new ProtoOAAmendOrderReq { CtidTraderAccountId = ctidTraderAccountId, OrderId = orderId, StopPrice = stopPrice }, timeout);

    /// <summary>Cancels a pending order; waits for the server's confirmation.</summary>
    public Task<ProtoOAOrder> CancelOrderAsync(long ctidTraderAccountId, long orderId, TimeSpan? timeout = null) =>
        AcknowledgeAsync(new ProtoOACancelOrderReq { CtidTraderAccountId = ctidTraderAccountId, OrderId = orderId }, timeout);

    /// <summary>
    /// Sets a position's server-side stop-loss and take-profit (absolute prices). The request replaces both:
    /// null removes that one, so pass the current take-profit to keep it when changing only the stop-loss.
    /// </summary>
    public Task<ProtoOAOrder> AmendPositionProtectionAsync(long ctidTraderAccountId, long positionId, double? stopLoss, double? takeProfit, TimeSpan? timeout = null)
    {
        var req = new ProtoOAAmendPositionSLTPReq { CtidTraderAccountId = ctidTraderAccountId, PositionId = positionId };
        if (stopLoss is { } sl)
            req.StopLoss = sl;
        if (takeProfit is { } tp)
            req.TakeProfit = tp;
        return AcknowledgeAsync(req, timeout);
    }

    /// <summary>Historical candles from the broker (5 requests/s limit), oldest first.</summary>
    public async Task<IReadOnlyList<Candle>> GetTrendbarsAsync(long ctidTraderAccountId, long symbolId, ProtoOATrendbarPeriod period, DateTime fromUtc, DateTime toUtc)
    {
        var res = await RequestAsync<ProtoOAGetTrendbarsRes>(new ProtoOAGetTrendbarsReq
        {
            CtidTraderAccountId = ctidTraderAccountId,
            SymbolId = symbolId,
            Period = period,
            FromTimestamp = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds(),
            ToTimestamp = new DateTimeOffset(toUtc).ToUnixTimeMilliseconds(),
        });
        return res.Trendbar.Select(Candle.From).OrderBy(c => c.OpenUtc).ToList();
    }

    /// <summary>All open positions and pending orders of the account (used to find left-overs after a crash).</summary>
    public Task<ProtoOAReconcileRes> ReconcileAsync(long ctidTraderAccountId) =>
        RequestAsync<ProtoOAReconcileRes>(new ProtoOAReconcileReq { CtidTraderAccountId = ctidTraderAccountId });

    // The server answers an order request with several execution events under the request's clientMsgId:
    // ORDER_ACCEPTED, then ORDER_FILLED (with the deal) — or an order error / ORDER_REJECTED. On a close, the
    // position's stop-loss order is also cancelled under the same ID; only events for our own order count.
    private Task<OrderFill> ExecuteAsync(IMessage request, TimeSpan? timeout)
    {
        var clock = Stopwatch.StartNew();
        long? orderId = null;
        return SendAsync<OrderFill>(request, (message, done) =>
        {
            switch (message)
            {
                case ProtoOAExecutionEvent e when e.Order is not null && orderId is not null && e.Order.OrderId != orderId:
                    return false; // another order (e.g. the stop-loss being cancelled)
                case ProtoOAExecutionEvent { ExecutionType: ProtoOAExecutionType.OrderAccepted } e:
                    orderId ??= e.Order?.OrderId;
                    return false;
                case ProtoOAExecutionEvent { ExecutionType: ProtoOAExecutionType.OrderFilled, Deal: not null } e:
                    done.TrySetResult(OrderFill.From(e, clock.Elapsed));
                    return true;
                case ProtoOAExecutionEvent { ExecutionType: ProtoOAExecutionType.OrderRejected or ProtoOAExecutionType.OrderCancelled or ProtoOAExecutionType.OrderExpired } e:
                    done.TrySetException(new CTraderApiException(string.IsNullOrEmpty(e.ErrorCode) ? e.ExecutionType.ToString() : e.ErrorCode, "order not filled"));
                    return true;
                case ProtoOAOrderErrorEvent err:
                    done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                    return true;
                case ProtoOAErrorRes err:
                    done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                    return true;
                case ProtoErrorRes err:
                    done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                    return true;
                default:
                    return false; // e.g. partial fill: keep waiting
            }
        }, timeout);
    }

    // Pending-order and SL/TP requests: the first execution event under our clientMsgId is the answer
    // (ORDER_ACCEPTED, ORDER_REPLACED or ORDER_CANCELLED), or an error / ORDER_REJECTED / ORDER_CANCEL_REJECTED.
    private Task<ProtoOAOrder> AcknowledgeAsync(IMessage request, TimeSpan? timeout) =>
        SendAsync<ProtoOAOrder>(request, (message, done) =>
        {
            switch (message)
            {
                case ProtoOAExecutionEvent { ExecutionType: ProtoOAExecutionType.OrderRejected or ProtoOAExecutionType.OrderCancelRejected } e:
                    done.TrySetException(new CTraderApiException(string.IsNullOrEmpty(e.ErrorCode) ? e.ExecutionType.ToString() : e.ErrorCode, "request rejected"));
                    return true;
                case ProtoOAExecutionEvent e:
                    done.TrySetResult(e.Order ?? new ProtoOAOrder());
                    return true;
                case ProtoOAOrderErrorEvent err:
                    done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                    return true;
                case ProtoOAErrorRes err:
                    done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                    return true;
                case ProtoErrorRes err:
                    done.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                    return true;
                default:
                    return false;
            }
        }, timeout);

    /// <summary>Sends a request with a new clientMsgId and feeds every reply with that ID to <paramref name="onReply"/>
    /// until it returns true; the result is whatever <paramref name="onReply"/> puts into the completion source.</summary>
    private async Task<T> SendAsync<T>(IMessage request, Func<IMessage, TaskCompletionSource<T>, bool> onReply, TimeSpan? timeout)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Pending(m => onReply(m, done), ex => done.TrySetException(ex));
        var name = request.GetType().Name;
        var wait = timeout ?? DefaultTimeout;
        try
        {
            _log.Debug("→ {Request} {ClientMsgId}", name, id);
            await _client.SendMessage(request, id);
            return await done.Task.WaitAsync(wait);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No reply to {name} within {wait.TotalSeconds:0} s");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Diagnostic only: drops the connection as if the network failed.</summary>
    public void SimulateDrop()
    {
        _log.Event("TestDrop").Warning("Simulating a connection drop (test flag)");
        Close(new IOException("Simulated connection drop"));
        _client.Dispose();
    }

    // Must never throw: the library turns an observer exception into a dead connection.
    private void OnMessage(IMessage message)
    {
        try
        {
            Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);

            // The library delivers a raw envelope for messages with a clientMsgId, and for messages it cannot
            // decode; it then delivers the decoded copy only if it could decode it. The envelope completes the
            // matching request; listeners get the decoded copy — from us when the library has none.
            if (message is ProtoMessage envelope)
            {
                var libraryCannotDecode = PayloadDecoder.LibraryCannotDecode(envelope);
                var decoded = PayloadDecoder.Decode(envelope);
                if (decoded is null)
                {
                    _log.Debug("← unknown payload type {PayloadType}", envelope.PayloadType);
                    return;
                }

                if (envelope.HasClientMsgId && _pending.TryGetValue(envelope.ClientMsgId, out var pending))
                {
                    _log.Debug("← {Response} {ClientMsgId}", decoded.GetType().Name, envelope.ClientMsgId);
                    if (pending.OnMessage(decoded))
                        _pending.TryRemove(envelope.ClientMsgId, out _);
                }
                if (libraryCannotDecode)
                    MessageReceived?.Invoke(decoded);
                return;
            }

            MessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            _log.Event("HandlerError").Error(ex, "Error while handling {Message}", message.GetType().Name);
        }
    }

    private void OnError(Exception error) => Close(error);

    private void Close(Exception? error)
    {
        if (!_closed.TrySetResult(_disposing ? null : error))
            return;

        var reason = _disposing ? null : error ?? new IOException("Connection closed by the server");
        foreach (var (_, pending) in _pending)
            pending.Fail(new IOException("Connection closed", reason));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposing)
            return ValueTask.CompletedTask;

        _disposing = true;
        _subscription.Dispose();
        _client.Dispose();
        Close(null);
        _log.Event("Disconnected").Information("Disconnected");
        return ValueTask.CompletedTask;
    }
}
