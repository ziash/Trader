using System.Threading.Channels;
using Google.Protobuf;
using Serilog;
using Trader.Core.Config;

namespace Trader.Core.OpenApi;

/// <summary>A problem that reconnecting cannot fix (bad client ID/secret, refresh token rejected). The bot should stop.</summary>
public sealed class FatalConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A demo trading account authenticated on the connection.</summary>
public sealed record TradingAccount(long CtidTraderAccountId, long TraderLogin);

public sealed class ConnectionOptions
{
    /// <summary>The accounts (ctidTraderAccountId) this bot uses. Empty = every demo account the token grants.
    /// A listed account that the token does not grant is a fatal error.</summary>
    public IReadOnlyCollection<long> AccountIds { get; init; } = [];

    /// <summary>Silence on the line after which a ping is sent. The server itself only sends a heartbeat after ~30 s.</summary>
    public TimeSpan PingAfterSilence { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>A ping not answered within this time means the connection is dead.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the token expiry is checked and the status line is logged.</summary>
    public TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Diagnostic only: drop the connection this long after each connect, to test reconnecting.</summary>
    public TimeSpan? SimulateDropAfter { get; init; }
}

/// <summary>
/// Keeps one Open API connection up for as long as <see cref="RunAsync"/> runs (plan 5.2):
/// connect → app auth → token refresh if due → account auth → <see cref="OnReady"/> → watch.
/// On any loss it reconnects with backoff (<see cref="Backoff"/>) and authenticates again.
/// </summary>
public sealed class ConnectionManager(string secretsPath, TraderSecrets secrets, ILogger log, ConnectionOptions? options = null)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly ConnectionOptions _options = options ?? new ConnectionOptions();
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();
    private TraderSecrets _secrets = secrets;
    private IReadOnlyList<TradingAccount> _accounts = [];

    /// <summary>Called after every successful (re)connect, once all accounts are authenticated —
    /// the place to (re)subscribe to spots, trendbars etc. An exception here forces a reconnect.</summary>
    public Func<CTraderSession, IReadOnlyList<TradingAccount>, CancellationToken, Task>? OnReady { get; set; }

    /// <summary>Every decoded incoming message from the current connection. Must not block.</summary>
    public event Action<IMessage>? MessageReceived;

    public IReadOnlyList<TradingAccount> Accounts => _accounts;

    /// <summary>Times the connection was lost after having been ready.</summary>
    public int Reconnects { get; private set; }

    public DateTime? AccessTokenExpiresUtc => _secrets.AccessTokenExpiresUtc;

    /// <summary>Runs until <paramref name="ct"/> is cancelled (returns normally) or a fatal problem occurs (throws).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        var forceRefresh = false;

        while (!ct.IsCancellationRequested)
        {
            CTraderSession? session = null;
            var wasReady = false;
            string reason;
            try
            {
                session = await CTraderSession.ConnectAsync(_secrets.Environment, log);
                session.MessageReceived += OnSessionMessage;

                await AuthenticateApplicationAsync(session);
                var refreshed = await EnsureTokenAsync(session, forceRefresh, ct);
                forceRefresh = false;
                try
                {
                    await AuthenticateAccountsAsync(session);
                }
                catch (CTraderApiException ex) when (ex.IsTokenProblem && refreshed)
                {
                    throw new FatalConnectionException($"Access token rejected right after a refresh ({ex.ErrorCode})", ex);
                }

                // Signals queued by a previous connection are stale: this one is freshly authenticated.
                while (_signals.Reader.TryRead(out _)) { }

                attempt = 0;
                wasReady = true;
                if (OnReady is { } onReady)
                    await onReady(session, _accounts, ct);

                reason = await MonitorAsync(session, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (FatalConnectionException)
            {
                throw;
            }
            catch (CTraderApiException ex) when (ex.IsTokenProblem)
            {
                forceRefresh = true;
                reason = $"access token rejected ({ex.ErrorCode})";
            }
            catch (Exception ex)
            {
                reason = $"{ex.GetType().Name}: {ex.Message}";
                if (ex is not (TimeoutException or IOException or CTraderApiException
                    or OpenAPI.Net.Exceptions.ConnectionException or System.Net.Sockets.SocketException))
                    log.Error(ex, "Unexpected error in connection loop");
            }
            finally
            {
                if (session is not null)
                {
                    session.MessageReceived -= OnSessionMessage;
                    await session.DisposeAsync();
                }
            }

            if (ct.IsCancellationRequested)
                break;

            if (wasReady)
                Reconnects++;
            var delay = forceRefresh && attempt == 0 ? TimeSpan.FromSeconds(1) : Backoff.Delay(attempt);
            attempt++;
            log.Warning("Connection lost ({Reason}); reconnecting in {Delay:0.0} s (attempt {Attempt})",
                reason, delay.TotalSeconds, attempt);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        log.Information("Connection manager stopped");
    }

    private async Task AuthenticateApplicationAsync(CTraderSession session)
    {
        try
        {
            await session.AuthenticateApplicationAsync(_secrets);
        }
        catch (CTraderApiException ex) when (ex.ErrorCode == "CH_CLIENT_AUTH_FAILURE")
        {
            throw new FatalConnectionException(
                $"Application authentication failed ({ex.ErrorCode}): check CTRADER_CLIENT_ID / CTRADER_CLIENT_SECRET", ex);
        }
        log.Information("Application authenticated");
    }

    /// <returns>True if new tokens are now in use.</returns>
    private async Task<bool> EnsureTokenAsync(CTraderSession session, bool force, CancellationToken ct)
    {
        if (!force && !TokenRefresher.NeedsRefresh(_secrets, DateTime.UtcNow))
            return false;

        if (force)
            log.Warning("Access token rejected by the server; refreshing");
        else if (_secrets.AccessTokenExpiresUtc is { } expires)
            log.Information("Access token expires {Expires:yyyy-MM-dd HH:mm}Z (less than {Days} days); refreshing",
                expires, TokenRefresher.RefreshMargin.TotalDays);
        else
            log.Information("Access token expiry unknown (first run); refreshing to learn it");

        bool refreshed;
        try
        {
            (_secrets, refreshed) = await TokenRefresher.RefreshAsync(
                secretsPath, _secrets, session.RefreshTokenAsync, () => DateTime.UtcNow, ct);
        }
        catch (CTraderApiException ex)
        {
            throw new FatalConnectionException(
                $"Token refresh rejected ({ex.ErrorCode}). Get a new access token and refresh token in the " +
                $"Open API Playground and put them in {secretsPath}", ex);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // If the server did refresh but the reply was lost, the old refresh token is now invalid
            // and the next attempt will fail with a fatal error asking for new tokens.
            log.Warning("Token refresh did not complete ({Message})", ex.Message);
            throw;
        }

        if (refreshed)
            log.Information("Token refreshed: new access token {Token}, expires {Expires:yyyy-MM-dd HH:mm}Z; saved to {Path}",
                TraderSecrets.Mask(_secrets.AccessToken), _secrets.AccessTokenExpiresUtc, secretsPath);
        else
            log.Information("Tokens were already refreshed by another instance; reloaded from {Path}", secretsPath);
        return true;
    }

    private async Task AuthenticateAccountsAsync(CTraderSession session)
    {
        var all = await session.GetAccountsAsync(_secrets);
        var demo = SelectAccounts(all, _options.AccountIds);

        foreach (var acc in demo)
            await AuthenticateAccountAsync(session, acc.CtidTraderAccountId);

        if (!demo.SequenceEqual(_accounts))
            log.Information("Accounts authenticated: {Accounts}{Live}",
                string.Join(", ", demo.Select(a => $"{a.CtidTraderAccountId} (login {a.TraderLogin})")),
                all.Count > demo.Count ? $"; {all.Count - demo.Count} other account(s) not used" : "");
        else
            log.Information("{Count} account(s) authenticated", demo.Count);
        _accounts = demo;
    }

    /// <summary>Demo accounts granted by the token, narrowed to <paramref name="wanted"/> when it is not empty.</summary>
    internal static List<TradingAccount> SelectAccounts(IEnumerable<ProtoOACtidTraderAccount> granted, IReadOnlyCollection<long> wanted)
    {
        var demo = granted.Where(a => !a.IsLive)
            .Select(a => new TradingAccount((long)a.CtidTraderAccountId, (long)a.TraderLogin))
            .ToList();
        if (demo.Count == 0)
            throw new FatalConnectionException("The access token grants no demo accounts");
        if (wanted.Count == 0)
            return demo;

        var missing = wanted.Where(id => demo.All(a => a.CtidTraderAccountId != id)).ToList();
        if (missing.Count > 0)
            throw new FatalConnectionException(
                $"Account(s) {string.Join(", ", missing)} not granted by the access token (granted demo accounts: " +
                $"{string.Join(", ", demo.Select(a => a.CtidTraderAccountId))})");
        return demo.Where(a => wanted.Contains(a.CtidTraderAccountId)).ToList();
    }

    private async Task AuthenticateAccountAsync(CTraderSession session, long ctid)
    {
        try
        {
            await session.AuthenticateAccountAsync(ctid, _secrets);
        }
        catch (CTraderApiException ex) when (ex.ErrorCode == "ALREADY_LOGGED_IN")
        {
            // fine: the account is still authenticated on this connection
        }
    }

    /// <summary>Watches the connection; returns the reason when it must be replaced.</summary>
    private async Task<string> MonitorAsync(CTraderSession session, CancellationToken ct)
    {
        var connectedAt = DateTime.UtcNow;
        var nextHousekeeping = connectedAt + _options.HousekeepingInterval;
        var dropAt = connectedAt + _options.SimulateDropAfter;
        Task<bool>? signalWait = null;

        while (true)
        {
            signalWait ??= _signals.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(session.Closed, signalWait, Task.Delay(Tick, ct));
            ct.ThrowIfCancellationRequested();

            if (session.Closed.IsCompleted)
                return session.Closed.Result?.Message ?? "closed by the server";

            if (signalWait.IsCompleted)
            {
                signalWait = null;
                while (_signals.Reader.TryRead(out var signal))
                    await HandleSignalAsync(session, signal, ct);
            }

            var now = DateTime.UtcNow;
            var silence = now - session.LastReceivedUtc;
            if (silence > _options.PingAfterSilence)
            {
                try
                {
                    await session.PingAsync(_options.PingTimeout);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    return $"no data for {silence.TotalSeconds:0} s and no reply to ping";
                }
            }

            if (now >= nextHousekeeping)
            {
                nextHousekeeping = now + _options.HousekeepingInterval;
                if (await EnsureTokenAsync(session, force: false, ct))
                    await AuthenticateAccountsAsync(session);
                log.Information("Status: connected for {Uptime}, reconnects {Reconnects}, accounts {Accounts}, access token expires {Expires}",
                    FormatDuration(now - connectedAt), Reconnects, _accounts.Count,
                    _secrets.AccessTokenExpiresUtc is { } e ? $"{e:yyyy-MM-dd HH:mm}Z" : "unknown");
            }

            if (dropAt is { } d && now >= d)
            {
                dropAt = null;
                session.SimulateDrop();
            }
        }
    }

    private async Task HandleSignalAsync(CTraderSession session, Signal signal, CancellationToken ct)
    {
        switch (signal)
        {
            case Signal.TokenInvalidated:
                await EnsureTokenAsync(session, force: true, ct);
                await AuthenticateAccountsAsync(session);
                break;
            case Signal.AccountDisconnected(var ctid):
                log.Warning("Server ended the session of account {Ctid}; authenticating it again", ctid);
                await AuthenticateAccountAsync(session, ctid);
                break;
        }
    }

    // Runs on the receive thread: only queue work, never block.
    private void OnSessionMessage(IMessage message)
    {
        switch (message)
        {
            case ProtoOAAccountsTokenInvalidatedEvent e:
                log.Warning("Server invalidated the access token for {Count} account(s): {Reason}",
                    e.CtidTraderAccountIds.Count, e.Reason);
                _signals.Writer.TryWrite(new Signal.TokenInvalidated());
                break;
            case ProtoOAAccountDisconnectEvent e:
                _signals.Writer.TryWrite(new Signal.AccountDisconnected((long)e.CtidTraderAccountId));
                break;
            case ProtoOAClientDisconnectEvent e:
                log.Warning("Server is closing the connection: {Reason}", e.Reason);
                break;
        }

        MessageReceived?.Invoke(message);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";

    private abstract record Signal
    {
        public sealed record TokenInvalidated : Signal;
        public sealed record AccountDisconnected(long Ctid) : Signal;
    }
}
