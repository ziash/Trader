# Trader.Core — shared framework for all bots

Every bot in this repo is a small program on top of this library. The connection, token handling,
logging and start-up/shut-down live here **once**; bots do not copy them (plan decision 14).

| Folder | What it gives a bot |
|---|---|
| `Hosting/` | `BotRunner` (start-up, options, Ctrl+C/SIGTERM, exit codes), `BotIdentity` (instance ID, order labels) |
| `OpenApi/` | `ConnectionManager` (stay connected: reconnect, dead-line ping, token refresh, account auth), `CTraderSession` (requests/replies on one connection) |
| `Config/` | `SecretsFile` (reads `~/.config/trader/secrets.env`, demo only), `TokenRefresher` (refresh + atomic save under a lock) |
| `Logging/` | `BotLogging` (console, UTC timestamps, instance ID on every line) |

## Add a new bot (about 5 minutes)

```bash
cp -r src/Trader.SampleBot src/Trader.MyBot
mv src/Trader.MyBot/Trader.SampleBot.csproj src/Trader.MyBot/Trader.MyBot.csproj
# in src/Trader.MyBot/Program.cs: change "SampleBot" to "MyBot"
dotnet sln add src/Trader.MyBot
dotnet run --project src/Trader.MyBot -- --account 49123893
```

The whole of a minimal bot:

```csharp
using Trader.Core.Hosting;

return await BotRunner.RunAsync("MyBot", args, async (bot, ct) =>
{
    // Every message from the server (spots, fills, …). Runs on the receive thread: keep it short.
    bot.Connection.MessageReceived += message => { /* … */ };

    // After every connect AND every reconnect, once the accounts are authenticated.
    // Subscribe to spots/trendbars here, so subscriptions come back after a reconnect.
    bot.Connection.OnReady = async (session, accounts, ct) =>
    {
        foreach (var account in accounts)
        {
            var trader = await session.GetTraderAsync(account.CtidTraderAccountId);
            bot.Log.Information("Using account {Ctid} ({Type})", account.CtidTraderAccountId, trader.AccountType);
        }
    };

    await bot.Connection.RunAsync(ct);   // returns on Ctrl+C / SIGTERM
});
```

## Common options (every bot)

| Option | Meaning |
|---|---|
| `--account <ctid>` | Use only this account; repeat for more. Default: all demo accounts the token grants. A bot can also set a default with `new BotOptions { AccountIds = [...] }`. |
| `--verbose`, `-v` | Debug logging: every request and reply. |
| `--test-drop-after <sec>` | Diagnostic: drop the connection after each connect, to test reconnecting. |
| `--help`, `-h` | List the options. |

Anything else on the command line is passed to the bot in `bot.Args`.

Exit codes: `0` stopped normally · `1` secrets/config or fatal API problem (e.g. refresh token rejected) · `2` bad options · `3` unexpected error.

## What the connection does for you

- TLS to `demo.ctraderapi.com:5035`; a heartbeat so the client is never silent for more than 10 s.
- After 40 s with nothing received it sends a ping; no reply within 10 s → reconnect.
- Reconnect waits 1, 2, 4 … up to 60 s (plus random jitter), then authenticates the app and the accounts again, then calls `OnReady` again.
- Refreshes the access token when it has under 7 days left, or when the server rejects it, and saves the new pair to `secrets.env`.
- Logs a status line once an hour.

## Rules for code using this library

- **Demo only.** `CTRADER_ENV` must be `demo`; nothing here connects to the live host before Phase 5.
- **One secrets file for all bots.** Never write `secrets.env` yourself: only `TokenRefresher` / `SecretsFile.WriteTokens`, which hold `secrets.env.lock`. When one bot refreshes, the others pick up the new tokens automatically.
- **Never log secrets.** Use `TraderSecrets.Mask(...)` if a value must appear.
- **Label every order** with `bot.Identity.Label()` or `bot.Identity.Label("T1-S1")` (max 100 characters).
- **Respect rate limits:** 50 requests/s per connection (5/s for historical data). Each bot has its own connection.
- **Keep `MessageReceived` handlers short;** do heavy work elsewhere (queue it).
- Code shared by more than one bot belongs here, not in a bot folder. Changes here affect every bot: build and run all tests (`dotnet test`) before committing.

## Using it outside this repo

If a bot ever lives in another repo, pack this library as a versioned NuGet package
(`dotnet pack src/Trader.Core -o ~/tradebot/packages`) and reference that version, rather than copying the folder.
Copying works (the folder builds on its own), but the copy no longer gets fixes.
