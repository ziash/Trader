// Audit Bot (plan Section 6). So far (Phase 1 step 2): stays connected to the cTrader Open API (demo) —
// heartbeat, dead-line detection, auto-reconnect, token refresh — until Ctrl+C or SIGTERM.
// Price feed, registry and probe orders are added in steps 3–5 (plan 8.1).
// Options: see BotRunner.Usage (--help).

using Trader.Core.Hosting;

return await BotRunner.RunAsync("AuditBot", args, async (bot, ct) =>
{
    bot.Connection.OnReady = (_, accounts, _) =>
    {
        bot.Log.Information("Ready: {Count} demo account(s) on the connection", accounts.Count);
        return Task.CompletedTask;
    };

    await bot.Connection.RunAsync(ct);
});
