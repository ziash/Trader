// Audit Bot (plan Section 6). Phase 1 steps 3–4: price feed to disk, instance registry.
// - Reads ~/tradebot/config/audit.*.json (one per broker).
// - Stays connected (heartbeat, reconnect, token refresh — Trader.Core).
// - Subscribes to bid/ask and M5 trendbars for the configured symbols, plus the symbols needed to
//   convert JPY/GBP costs into USD.
// - Writes spreads.csv (one row per symbol every 2 min main hours / 5 min off-peak) and events.csv to
//   ~/tradebot/logs/<broker>/<ctidTraderAccountId>/<instanceId>/<yyyy-MM-dd>/.
// - Registers in ~/tradebot/run/instances/ (plan 6.4): per account, one instance is Primary (may send
//   probe orders from step 5), the others Passive; a Passive takes over at once if the Primary stops.
// Probe orders follow in step 5 (plan 8.1). Places no orders.
//
// Options: --config-dir <dir> (default ~/tradebot/config), plus the common ones (--help).

using System.Collections.Concurrent;
using Trader.AuditBot;
using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

var configDir = TradebotPaths.Config;
var dirIndex = Array.IndexOf(args, "--config-dir");
if (dirIndex >= 0)
{
    if (dirIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--config-dir needs a folder");
        return BotRunner.ExitUsage;
    }
    configDir = args[dirIndex + 1];
}

IReadOnlyList<AuditConfig> configs;
try
{
    configs = AuditConfig.LoadAll(configDir);
}
catch (AuditConfigException ex)
{
    Console.Error.WriteLine($"Config problem: {ex.Message}");
    return BotRunner.ExitFatal;
}

return await BotRunner.RunAsync("AuditBot", args, async (bot, ct) =>
{
    string Folder(AuditConfig c) => Path.Combine(TradebotPaths.Logs, c.Broker, c.AccountId.ToString(), bot.Identity.InstanceId);
    string DayFile(AuditConfig c, DateOnly d, string file) => Path.Combine(Folder(c), d.ToString("yyyy-MM-dd"), file);

    var feed = new PriceFeed();
    var recorder = new SpreadRecorder(bot.Log, feed);
    var events = new CsvEventSink();
    foreach (var c in configs)
    {
        recorder.AddAccount(c, new DailyCsvWriter(d => DayFile(c, d, "spreads.csv"), SpreadRecorder.Header));
        events.AddAccount(c.AccountId, new DailyCsvWriter(d => DayFile(c, d, "events.csv"), CsvEventSink.Header));
        bot.Log.Information("{Broker} ({BrokerName}, account {Ctid}): {Count} symbols → {Folder}",
            c.Broker, c.BrokerName, c.AccountId, c.Symbols.Count, Folder(c));
    }
    bot.LogTap.Add(events);

    var registry = new InstanceRegistry(TradebotPaths.Instances, bot.Identity, bot.Log);
    foreach (var c in configs)
        registry.Register(c.Broker, c.AccountId, c.Symbols);

    // Log once per connection and account that M5 trendbars are arriving (they are used from Phase 4).
    var trendbarSeen = new ConcurrentDictionary<long, bool>();
    feed.TrendbarReceived += (ctid, _, bar) =>
    {
        if (bar.Period == ProtoOATrendbarPeriod.M5 && trendbarSeen.TryAdd(ctid, true))
            bot.Log.Event("TrendbarsOk").ForAccount(ctid).Information("M5 trendbars arriving for account {Ctid}", ctid);
    };

    bot.Connection.MessageReceived += feed.Handle;
    feed.QuoteReceived += recorder.OnQuote;

    bot.Connection.OnReady = async (session, accounts, _) =>
    {
        feed.Reset();
        trendbarSeen.Clear();
        foreach (var c in configs.Where(c => accounts.Any(a => a.CtidTraderAccountId == c.AccountId)))
            await SubscribeAsync(session, c);
    };

    async Task SubscribeAsync(CTraderSession session, AuditConfig c)
    {
        var log = bot.Log.ForAccount(c.AccountId);
        var catalog = await SymbolCatalog.LoadAsync(session, c.AccountId);
        var names = c.BrokerSymbolNames;
        var symbols = await catalog.GetDetailsAsync(session, names);

        foreach (var missing in names.Where(n => symbols.All(s => s.Name != n.Key)))
            log.Event("SymbolMissing").Warning("{Broker}: symbol {Symbol} (broker name {BrokerSymbol}) not found; skipped",
                c.Broker, missing.Key, missing.Value);
        foreach (var s in symbols.Where(s => !s.IsTradable))
            log.Event("SymbolNotTradable").Warning("{Broker}: {Symbol} is not enabled for trading ({Mode}); logging its prices anyway",
                c.Broker, s.Name, s.TradingMode);

        // Symbols to convert each quote currency into USD (e.g. JPY → USDJPY, GBP → GBPUSD).
        var conversions = new Dictionary<string, ConversionSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var quote in symbols.Select(s => s.QuoteAsset).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(q => !q.Equals("USD", StringComparison.OrdinalIgnoreCase)))
        {
            if (catalog.FindConversion(quote, "USD") is not { } conv)
            {
                log.Event("ConversionMissing").Warning("{Broker}: no symbol converts {Asset} to USD; USD columns stay empty for it", c.Broker, quote);
                continue;
            }
            var info = (await catalog.GetDetailsAsync(session, new Dictionary<string, string> { [conv.Symbol] = conv.Symbol })).Single();
            conversions[quote] = new ConversionSymbol(info, conv.Invert);
        }

        recorder.Configure(c.AccountId, symbols, conversions);

        var ids = symbols.Select(s => s.SymbolId).Concat(conversions.Values.Select(v => v.Symbol.SymbolId)).Distinct().ToList();
        if (ids.Count == 0)
            return;
        await session.SubscribeSpotsAsync(c.AccountId, ids);
        foreach (var s in symbols)
            await session.SubscribeLiveTrendbarAsync(c.AccountId, s.SymbolId, ProtoOATrendbarPeriod.M5);

        log.Event("Subscribed").Information("{Broker}: subscribed to {Symbols}{Conversions}; M5 trendbars on",
            c.Broker, string.Join(", ", symbols.Select(s => s.Name)),
            conversions.Count > 0 ? $" (+ {string.Join(", ", conversions.Values.Select(v => v.Symbol.BrokerName))} for USD conversion)" : "");
        foreach (var s in symbols)
            log.Debug("{Broker} {Symbol}: id {Id}, pip {Pip}, lot {Lot} units, quote {Quote}",
                c.Broker, s.Name, s.SymbolId, s.PipSize, s.LotUnits, s.QuoteAsset);
    }

    try
    {
        await BotRunner.RunTogetherAsync(ct, registry.RunAsync, bot.Connection.RunAsync, recorder.RunAsync);
    }
    finally
    {
        registry.Dispose();
        events.Dispose();
    }
}, new BotOptions { AccountIds = configs.Select(c => c.AccountId).ToList() });
