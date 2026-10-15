using Google.Protobuf;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;
using Serilog;
using Trader.Core.Config;

namespace Trader.Core.OpenApi;

/// <summary>An error reply from the Open API (ProtoOAErrorRes / ProtoErrorRes).</summary>
public sealed class CTraderApiException(string errorCode, string? description)
    : Exception($"{errorCode}: {description}")
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// One TLS connection to the cTrader Open API (demo host only), with simple
/// request → response calls on top of Spotware's OpenAPI.Net client.
/// The library sends the 10 s heartbeat itself.
///
/// Requests are sent one at a time and matched to replies by message type,
/// which is enough for this step; streaming events (spots, fills) come later.
/// </summary>
public sealed class CTraderSession : IAsyncDisposable
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly OpenClient _client;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly IDisposable _subscription;
    private Action<IMessage>? _onMessage;
    private Action<Exception>? _onError;

    private CTraderSession(OpenClient client, ILogger log)
    {
        _client = client;
        _log = log;
        _subscription = client.Subscribe(
            message => _onMessage?.Invoke(message),
            error => _onError?.Invoke(error));
    }

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
        log.Information("Connecting to {Host}:{Port} (TLS)", host, ApiInfo.Port);
        await client.Connect();
        log.Information("Connected");
        return session;
    }

    /// <summary>Authenticates the application (client ID + secret). Must be the first request.</summary>
    public Task AuthenticateApplicationAsync(TraderSecrets secrets) =>
        RequestAsync<ProtoOAApplicationAuthReq, ProtoOAApplicationAuthRes>(new()
        {
            ClientId = secrets.ClientId,
            ClientSecret = secrets.ClientSecret,
        });

    /// <summary>Lists every trading account the access token was granted for.</summary>
    public async Task<IReadOnlyList<ProtoOACtidTraderAccount>> GetAccountsAsync(TraderSecrets secrets)
    {
        var res = await RequestAsync<ProtoOAGetAccountListByAccessTokenReq, ProtoOAGetAccountListByAccessTokenRes>(
            new() { AccessToken = secrets.AccessToken });
        return res.CtidTraderAccount;
    }

    /// <summary>Authenticates one trading account on this connection.</summary>
    public Task AuthenticateAccountAsync(long ctidTraderAccountId, TraderSecrets secrets) =>
        RequestAsync<ProtoOAAccountAuthReq, ProtoOAAccountAuthRes>(new()
        {
            CtidTraderAccountId = ctidTraderAccountId,
            AccessToken = secrets.AccessToken,
        });

    /// <summary>Account details (balance, leverage, hedging/netting). Account must be authenticated.</summary>
    public async Task<ProtoOATrader> GetTraderAsync(long ctidTraderAccountId)
    {
        var res = await RequestAsync<ProtoOATraderReq, ProtoOATraderRes>(new() { CtidTraderAccountId = ctidTraderAccountId });
        return res.Trader;
    }

    /// <summary>Assets (currencies, metals …) known to the account; used to name the deposit currency.</summary>
    public async Task<IReadOnlyList<ProtoOAAsset>> GetAssetsAsync(long ctidTraderAccountId)
    {
        var res = await RequestAsync<ProtoOAAssetListReq, ProtoOAAssetListRes>(new() { CtidTraderAccountId = ctidTraderAccountId });
        return res.Asset;
    }

    /// <summary>Sends <paramref name="request"/> and waits for a <typeparamref name="TRes"/> or an error reply.</summary>
    public async Task<TRes> RequestAsync<TReq, TRes>(TReq request, TimeSpan? timeout = null)
        where TReq : IMessage
        where TRes : class, IMessage
    {
        await _oneAtATime.WaitAsync();
        try
        {
            var reply = new TaskCompletionSource<TRes>(TaskCreationOptions.RunContinuationsAsynchronously);
            _onMessage = message =>
            {
                switch (message)
                {
                    case TRes res:
                        reply.TrySetResult(res);
                        break;
                    case ProtoOAErrorRes err:
                        reply.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                        break;
                    case ProtoErrorRes err:
                        reply.TrySetException(new CTraderApiException(err.ErrorCode, err.Description));
                        break;
                }
            };
            _onError = error => reply.TrySetException(error);

            _log.Debug("→ {Request}", typeof(TReq).Name);
            await _client.SendMessage(request);
            var result = await reply.Task.WaitAsync(timeout ?? DefaultTimeout);
            _log.Debug("← {Response}", typeof(TRes).Name);
            return result;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No reply to {typeof(TReq).Name} within {(timeout ?? DefaultTimeout).TotalSeconds:0} s");
        }
        finally
        {
            _onMessage = null;
            _onError = null;
            _oneAtATime.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        _client.Dispose();
        _oneAtATime.Dispose();
        _log.Information("Disconnected");
        return ValueTask.CompletedTask;
    }
}
