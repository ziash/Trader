using System.Collections.Concurrent;
using Google.Protobuf;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;
using Serilog;
using Trader.Core.Config;
using Trader.Core.Logging;

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
/// - Requests carry a unique clientMsgId; the reply with the same ID completes the request,
///   so several requests can be in flight at once.
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessage>> _pending = new();
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
    public async Task<TRes> RequestAsync<TRes>(IMessage request, TimeSpan? timeout = null)
        where TRes : class, IMessage
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var reply = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        var name = request.GetType().Name;
        var wait = timeout ?? DefaultTimeout;
        try
        {
            _log.Debug("→ {Request} {ClientMsgId}", name, id);
            await _client.SendMessage(request, id);

            var message = await reply.Task.WaitAsync(wait);
            return message switch
            {
                TRes res => res,
                ProtoOAErrorRes err => throw new CTraderApiException(err.ErrorCode, err.Description),
                ProtoErrorRes err => throw new CTraderApiException(err.ErrorCode, err.Description),
                _ => throw new InvalidOperationException($"Unexpected reply {message.GetType().Name} to {name}"),
            };
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

                if (envelope.HasClientMsgId && _pending.TryGetValue(envelope.ClientMsgId, out var reply))
                {
                    _log.Debug("← {Response} {ClientMsgId}", decoded.GetType().Name, envelope.ClientMsgId);
                    reply.TrySetResult(decoded);
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
            pending.TrySetException(new IOException("Connection closed", reason));
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
