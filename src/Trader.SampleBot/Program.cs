// SampleBot — the smallest complete bot, and the starting point for a new one.
// To make a new bot: copy this folder to src/Trader.<Name>/, rename the .csproj to Trader.<Name>.csproj,
// change "SampleBot" below, then `dotnet sln add src/Trader.<Name>`. See src/Trader.Core/README.md.
//
// What it shows:
//   - BotRunner: options, logging, secrets, Ctrl+C/SIGTERM and exit codes are handled for you;
//   - OnReady: runs after every (re)connect once the accounts are authenticated — subscribe here;
//   - MessageReceived: every message from the server (spots, fills, …) — keep handlers short;
//   - bot.Identity.Label(): the label to put on every order.
// It places no orders.
//
// Run:  dotnet run --project src/Trader.SampleBot -- --account 49123893

using Trader.Core.Hosting;

return await BotRunner.RunAsync("SampleBot", args, async (bot, ct) =>
{
    var messages = 0;
    bot.Connection.MessageReceived += _ => Interlocked.Increment(ref messages);

    bot.Connection.OnReady = async (session, accounts, _) =>
    {
        foreach (var account in accounts)
        {
            var trader = await session.GetTraderAsync(account.CtidTraderAccountId);
            var digits = trader.HasMoneyDigits ? (int)trader.MoneyDigits : 2;
            bot.Log.Information("Account {Ctid} (login {Login}): balance {Balance}, {Type}",
                account.CtidTraderAccountId, account.TraderLogin,
                (trader.Balance / Math.Pow(10, digits)).ToString($"N{digits}"), trader.AccountType);
        }
        bot.Log.Information("Orders from this process would carry the label {Label}", bot.Identity.Label());
    };

    await bot.Connection.RunAsync(ct);
    bot.Log.Information("Received {Count} message(s) from the server", messages);
});
