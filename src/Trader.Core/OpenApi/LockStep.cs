using Google.Protobuf;
using Trader.Core.Config;

namespace Trader.Core.OpenApi;

/// <summary>
/// The switch <c>--lockstep</c> (open-code-pieces.md §3, Rewind spec §7.2): a whole-account replay on Rewind. The bot takes its time from the replay, acts once per turn the replay gives it,
/// and says when it has finished, so years of history run in hours with the bot's own code. Works only in replay mode (<c>CTRADER_ENV=replay</c>, which reaches this computer only).
/// </summary>
public sealed class LockStep
{
    public const string Option = "--lockstep";
    private const string TurnPrefix = "turn ";
    private readonly SemaphoreSlim _turn = new(0);
    private long _ticks;
    private CTraderSession? _session;
    private long _accountId;

    /// <summary>The switch from the command line; null when it is not given. Refused unless the secrets file says replay.</summary>
    public static LockStep? FromArgs(IEnumerable<string> args, string secretsPath)
    {
        if (!args.Contains(Option))
            return null;
        var environment = SecretsFile.Load(secretsPath).Environment;
        if (environment != ApiEnvironment.Replay)
            throw new InvalidOperationException($"{Option} works only with CTRADER_ENV=replay, not {environment}");
        return new LockStep();
    }

    /// <summary>The replay's time, from its latest turn.</summary>
    public DateTime UtcNow => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

    /// <summary>Every message from the server (receive thread).</summary>
    public void Handle(IMessage message)
    {
        if (message is not ProtoOAVersionRes { Version: { } v } || !v.StartsWith(TurnPrefix, StringComparison.Ordinal))
            return;
        Interlocked.Exchange(ref _ticks, long.Parse(v[TurnPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture));
        _turn.Release();
    }

    /// <summary>Takes part from now on: replay time on the session, no pause between candle requests.</summary>
    public async Task JoinAsync(CTraderSession session, long accountId)
    {
        (_session, _accountId) = (session, accountId);
        session.UseReplayClock(() => UtcNow);
        await SayAsync();
    }

    public Task NextTurnAsync(CancellationToken ct) => _turn.WaitAsync(ct);

    public Task FinishTurnAsync() => SayAsync();

    private Task SayAsync() =>
        (_session ?? throw new InvalidOperationException("Not joined")).RequestAsync<ProtoOASymbolCategoryListRes>(new ProtoOASymbolCategoryListReq { CtidTraderAccountId = _accountId });
}
