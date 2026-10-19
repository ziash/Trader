// TM — Trade Manually (plan 10.9, docs/bots/tm.md). The owner opens positions by hand in cTrader; TM mirrors each
// one and hedges both legs, manages the hedges, protects each leg at 1R, applies the 2R/3R loss rules and cleans
// up when the owner closes. Demo accounts only.
// - Reads ~/tradebot/config/tm.json (accounts, symbols, rule values) and notifications.json.
// - Remembers its trades in ~/tradebot/run/tm/<broker>-<ctid>.json.
// - Logs to ~/tradebot/logs/<broker>/<ctid>/<instanceId>/<date>/events.csv and actions.csv.
// - Acts on an account only while it is Primary there (one TM copy acts; plan 6.4).
//
// Options: --config-dir <dir> (default ~/tradebot/config), plus the common ones (--help, --test-notification).

using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.MarketData;
using Trader.TM;

var configDir = TradebotPaths.Config;
if (Array.IndexOf(args, "--config-dir") is var dirIndex and >= 0)
{
    if (dirIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--config-dir needs a folder");
        return BotRunner.ExitUsage;
    }
    configDir = args[dirIndex + 1];
}

TmConfig config;
try
{
    config = TmConfig.Load(Path.Combine(configDir, TmConfig.FileName));
}
catch (TmConfigException ex)
{
    Console.Error.WriteLine($"Config problem: {ex.Message}");
    return BotRunner.ExitFatal;
}

return await BotRunner.RunAsync("TM", args, async (bot, ct) =>
{
    string Folder(TmAccount a) => Path.Combine(TradebotPaths.Logs, a.Broker, a.AccountId.ToString(), bot.Identity.InstanceId);
    string DayFile(TmAccount a, DateOnly d, string file) => Path.Combine(Folder(a), d.ToString("yyyy-MM-dd"), file);

    var feed = new PriceFeed();
    var events = new CsvEventSink();
    var registry = new InstanceRegistry(TradebotPaths.Instances, bot.Identity, bot.Log);
    var writers = new List<DailyCsvWriter>();
    var managers = new List<AccountManager>();
    foreach (var a in config.Accounts)
    {
        var eventsCsv = new DailyCsvWriter(d => DayFile(a, d, "events.csv"), CsvEventSink.Header);
        var actionsCsv = new DailyCsvWriter(d => DayFile(a, d, "actions.csv"), AccountManager.Header);
        writers.AddRange([eventsCsv, actionsCsv]);
        events.AddAccount(a.AccountId, eventsCsv);
        registry.Register(a.Broker, a.AccountId, a.Symbols);
        var book = TradeBook.Load(Path.Combine(TradebotPaths.Root, "run", "tm", $"{a.Broker}-{a.AccountId}.json"));
        managers.Add(new AccountManager(a, config, bot.Identity, bot.Log, bot.Notify, registry, feed, book, actionsCsv));
        bot.Log.Information("{Broker} (account {Ctid}): {Symbols}; {Trades} remembered trade(s) → {Folder}",
            a.Broker, a.AccountId, string.Join(", ", a.Symbols), book.Trades.Count, Folder(a));
    }
    bot.LogTap.Add(events);
    bot.Log.Information("Rules: hedge {Pct} % of the {Days}-day range (min {Floor} × spread), arm {Arm}R, hedge stop {Stop}R, warn {Warn}R, close {Close}R",
        config.RangePercent, config.RangeDays, config.SpreadFloor, config.ArmR, config.HedgeStopR, config.WarnR, config.CloseR);

    bot.Connection.MessageReceived += feed.Handle;
    bot.Connection.MessageReceived += m =>
    {
        foreach (var manager in managers)
            manager.OnMessage(m);
    };

    bot.Connection.OnReady = async (session, accounts, _) =>
    {
        feed.Reset();
        foreach (var a in config.Accounts.Where(a => accounts.Any(x => x.CtidTraderAccountId == a.AccountId)))
        {
            var log = bot.Log.ForAccount(a.AccountId);
            var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
            var symbols = await catalog.GetDetailsAsync(session, a.BrokerSymbolNames);
            foreach (var missing in a.BrokerSymbolNames.Where(n => symbols.All(s => s.Name != n.Key)))
                log.Event("SymbolMissing").Warning("{Broker}: symbol {Symbol} not found; positions on it are left alone", a.Broker, missing.Key);
            if (symbols.Count > 0)
                await session.SubscribeSpotsAsync(a.AccountId, symbols.Select(s => s.SymbolId));
            managers.Single(m => m.AccountId == a.AccountId).SetSession(session, symbols);
            log.Event("Subscribed").Information("{Broker}: watching {Symbols}", a.Broker, string.Join(", ", symbols.Select(s => s.Name)));
        }
    };

    bot.Notify.Info("Started", "TM started", $"Watching {string.Join(", ", config.Accounts.Select(a => a.Broker))}");
    try
    {
        await BotRunner.RunTogetherAsync(ct, [registry.RunAsync, bot.Connection.RunAsync, .. managers.Select(m => (Func<CancellationToken, Task>)m.RunAsync)]);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        bot.Notify.Warning("Stopped", "TM stopped", "Positions keep their hedges and stops on the server; nothing is managed until TM runs again.");
        throw;
    }
    catch (Exception ex)
    {
        bot.Notify.Critical("Stopped", "TM stopped with an error", $"{ex.Message}. Positions keep their hedges and stops on the server.");
        throw;
    }
    finally
    {
        registry.Dispose();
        events.Dispose();
        foreach (var w in writers)
            w.Dispose();
    }
}, new BotOptions { AccountIds = config.Accounts.Select(a => a.AccountId).ToList() });
