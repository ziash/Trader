// Scout (docs/bots/scout/scout.md; setup rules: github.com/ziash/Scout docs/spec.md). Watches the owner's charts for the
// "pause, then stop order" setup on the broker's own closed candles. Watch mode alerts; Trade mode places the first
// stop order of each setup (label Scout-<6hex>-<tf>-<pause start>-M/R) and TM takes it over. Demo accounts only.
// - Reads ~/tradebot/config/scout.json and notifications.json.
// - Remembers what it did in ~/tradebot/run/scout/<broker>-<ctid>.json.
// - Logs to ~/tradebot/logs/<broker>/<ctid>/<instanceId>/<date>/events.csv, setups.csv and orders.csv.
// - Acts on an account only while it is Primary there (open-code-pieces.md §6).
//
// Options: --config-dir <dir> (default ~/tradebot/config); --dry-run (no orders: logs what it would do);
// --test-order (places one far-away EURUSD Buy Stop with stop-loss, trailing and expiry on the first account,
// reads it back, cancels it and exits); --candles <SYMBOL> <TF> <from yyyy-MM-dd> <file.csv> (writes the first account's
// closed candles to a file and exits; read-only); --replay <file.csv> (runs the detector on such a file and prints
// its events; no connection; with --symbol <SYMBOL> --orders <out.csv> [--higher <h4.csv>] [--timeframe H1] it writes the
// orders Scout would place and cancel instead, for Scout Lens, scout-lens.md §8 step 1); plus the common ones (--help, --test-notification).

using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;
using Trader.Scout;

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
var dryRun = args.Contains("--dry-run");
var testOrder = args.Contains("--test-order");
var candlesAt = Array.IndexOf(args, "--candles");
if (candlesAt >= 0 && candlesAt + 4 >= args.Length)
{
    Console.Error.WriteLine("--candles needs <SYMBOL> <TF> <from yyyy-MM-dd> <file.csv>");
    return BotRunner.ExitUsage;
}

if (Array.IndexOf(args, "--replay") is var replayAt and >= 0)
{
    if (replayAt + 1 >= args.Length)
    {
        Console.Error.WriteLine("--replay needs <file.csv>");
        return BotRunner.ExitUsage;
    }
    string? Arg(string name) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    if (Arg("--orders") is { } ordersPath)
    {
        // Orders Scout would place on past candles, for Scout Lens (scout-lens.md §8 step 1): the setup settings and the
        // expiry come from scout.json when it is there (else Scout_Pine's defaults); the outcome is played out by Rewind.
        if (Arg("--symbol") is not { } symbol)
        {
            Console.Error.WriteLine("--replay … --orders needs --symbol <SYMBOL> (and optionally --higher <file.csv>, --timeframe H1)");
            return BotRunner.ExitUsage;
        }
        var tf = Arg("--timeframe") ?? "H1";
        var settings = new DetectorSettings();
        var expiry = 16;
        var configPath = Path.Combine(configDir, ScoutConfig.FileName);
        if (File.Exists(configPath))
        {
            var cfg = ScoutConfig.Load(configPath);
            (settings, expiry) = (cfg.SetupSettings, cfg.ExpiryCandles);
        }
        var higher = Arg("--higher") is { } hp ? CandleFile.Read(hp) : [];
        var orders = ReplayOrders.Run(symbol, tf, CandleFile.Read(args[replayAt + 1]), higher, settings, expiry);
        ReplayOrders.Write(ordersPath, symbol, tf, orders);
        Console.WriteLine($"{symbol} {tf}: {orders.Count(o => o.Action == "place")} orders placed, {orders.Count(o => o.Action == "cancel")} cancelled → {ordersPath}" +
            $" (settings from {(File.Exists(configPath) ? configPath : "defaults")}, expiry {expiry} candles)");
        return BotRunner.ExitOk;
    }
    var detector = new Detector();
    foreach (var c in CandleFile.Read(args[replayAt + 1]))
        foreach (var e in detector.Add(c))
            Console.WriteLine($"{e.CandleUtc:yyyy-MM-dd HH:mm} {e.Kind,-14} {(e.Dir == 1 ? "BUY " : "SELL")} {e.Level:0.#####} far {e.FarSide:0.#####} pause {e.PauseStartUtc:MM-dd HH:mm} {e.Reason}");
    return BotRunner.ExitOk;
}

ScoutConfig config;
try
{
    config = ScoutConfig.Load(Path.Combine(configDir, ScoutConfig.FileName));
}
catch (ScoutConfigException ex)
{
    Console.Error.WriteLine($"Config problem: {ex.Message}");
    return BotRunner.ExitFatal;
}

return await BotRunner.RunAsync(ScoutConfig.BotName, args, async (bot, ct) =>
{
    if (candlesAt >= 0)
    {
        await CandlesAsync(bot, config.Accounts[0], args[candlesAt + 1], args[candlesAt + 2], DateTime.Parse(args[candlesAt + 3], null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), args[candlesAt + 4], ct);
        return;
    }
    if (testOrder)
    {
        await TestOrderAsync(bot, config.Accounts[0], ct);
        return;
    }

    string Folder(ScoutAccount a) => Path.Combine(TradebotPaths.Logs, a.Broker, a.AccountId.ToString(), bot.Identity.InstanceId);
    string DayFile(ScoutAccount a, DateOnly d, string file) => Path.Combine(Folder(a), d.ToString("yyyy-MM-dd"), file);

    var events = new CsvEventSink();
    var registry = new InstanceRegistry(TradebotPaths.Instances, bot.Identity, bot.Log);
    var writers = new List<DailyCsvWriter>();
    var scouts = new List<AccountScout>();
    foreach (var a in config.Accounts)
    {
        var eventsCsv = new DailyCsvWriter(d => DayFile(a, d, "events.csv"), CsvEventSink.Header);
        var setupsCsv = new DailyCsvWriter(d => DayFile(a, d, "setups.csv"), AccountScout.SetupsHeader);
        var ordersCsv = new DailyCsvWriter(d => DayFile(a, d, "orders.csv"), AccountScout.OrdersHeader);
        writers.AddRange([eventsCsv, setupsCsv, ordersCsv]);
        events.AddAccount(a.AccountId, eventsCsv);
        registry.Register(a.Broker, a.AccountId, a.Charts.Select(c => c.Symbol).Distinct().ToList());
        var state = ScoutState.Load(Path.Combine(TradebotPaths.Root, "run", "scout", $"{a.Broker}-{a.AccountId}.json"));
        scouts.Add(new AccountScout(a, config, bot.Identity, bot.Log, bot.Notify, registry, state, setupsCsv, ordersCsv, Folder(a), dryRun));
        bot.Log.Information("{Broker} (account {Ctid}): {Charts}; {Roles} remembered setup order(s) → {Folder}",
            a.Broker, a.AccountId, string.Join(", ", a.Charts.Select(c => $"{c.Symbol} {c.Timeframe} {c.Mode}")), state.Roles.Count, Folder(a));
    }
    bot.LogTap.Add(events);
    var s = config.SetupSettings;
    bot.Log.Information("Setup (Scout spec §3.2): strong {Push}×ATR, move {Move}×ATR, pause {Min}–{Max} candles, ≤ {H}×ATR, pull-back ≤ {R}, reversal after {Rev}, turn {Flip}×ATR / {Ch} candles, level {Level}",
        s.PushBody, s.MinMove, s.MinPause, s.MaxPause, s.MaxPauseHeight, s.MaxRetrace, s.ReversalAfter, s.FlipAtr, s.Channel, s.Level);
    bot.Log.Information("Orders: {Lots} lot ({Own} symbol(s) with their own size, listed after connecting), stop-loss at the far side of the pause, trailing on, expiry after {Expiry} candles{Dry}",
        config.Lots, config.LotsBySymbol?.Count ?? 0, config.ExpiryCandles, dryRun ? " — DRY RUN: no orders are placed or cancelled" : "");

    var feed = new PriceFeed(); // live prices: an order whose level has already passed is not sent (scout.md §2.3)
    bot.Connection.MessageReceived += m =>
    {
        feed.Handle(m);
        foreach (var scout in scouts)
            scout.OnMessage(m);
    };

    bot.Connection.OnReady = async (session, accounts, _) =>
    {
        feed.Reset();
        foreach (var a in config.Accounts.Where(a => accounts.Any(x => x.CtidTraderAccountId == a.AccountId)))
        {
            var log = bot.Log.ForAccount(a.AccountId);
            var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
            var symbols = await catalog.GetDetailsAsync(session, a.BrokerSymbolNames);
            foreach (var missing in a.BrokerSymbolNames.Where(n => symbols.All(x => x.Name != n.Key)))
                log.Event("SymbolMissing").Warning("{Broker}: symbol {Symbol} not found; its charts are skipped", a.Broker, missing.Key);
            if (symbols.Count > 0)
                await session.SubscribeSpotsAsync(a.AccountId, symbols.Select(x => x.SymbolId));
            scouts.Single(x => x.AccountId == a.AccountId).SetSession(session, feed, symbols);
            log.Event("Subscribed").Information("{Broker}: watching {Symbols}", a.Broker, string.Join(", ", symbols.Select(x => x.Name)));
            LogOrderSizes(log, a.Broker, config, symbols);
        }
    };

    bot.Notify.Info("Started", "Scout started", $"{string.Join(", ", config.Accounts.Select(a => a.Broker))}{(dryRun ? " (dry run)" : "")}");
    try
    {
        await BotRunner.RunTogetherAsync(ct, [registry.RunAsync, bot.Connection.RunAsync, .. scouts.Select(x => (Func<CancellationToken, Task>)x.RunAsync)]);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        bot.Notify.Warning("Stopped", "Scout stopped", "Its pending orders stay on the server and expire by themselves; TM manages the filled ones.");
        throw;
    }
    catch (Exception ex)
    {
        bot.Notify.Critical("Stopped", "Scout stopped with an error", $"{ex.Message}. Pending orders stay on the server and expire by themselves.");
        throw;
    }
    finally
    {
        registry.Dispose();
        events.Dispose();
        foreach (var w in writers)
            w.Dispose();
    }
}, new BotOptions { AccountIds = testOrder || candlesAt >= 0 ? [config.Accounts[0].AccountId] : config.Accounts.Select(a => a.AccountId).ToList() });

// One far-away EURUSD Buy Stop (0.01 lot, 3 % above the price) with stop-loss, trailing switch and a 1-hour expiry:
// checks that the server accepts and keeps all three (scout.md §5), then cancels it.
static async Task TestOrderAsync(BotContext bot, ScoutAccount a, CancellationToken ct)
{
    var done = new TaskCompletionSource();
    bot.Connection.OnReady = async (session, _, _) =>
    {
        try
        {
            var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
            var sym = (await catalog.GetDetailsAsync(session, new Dictionary<string, string> { ["EURUSD"] = "EURUSD" })).Single();
            var last = (await session.GetTrendbarsAsync(a.AccountId, sym.SymbolId, ProtoOATrendbarPeriod.H1, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow))[^1];
            var price = Math.Round(last.Close * 1.03, sym.Digits);
            var sl = Math.Round(last.Close * 1.02, sym.Digits);
            var expires = DateTime.UtcNow.AddHours(1);
            var order = await session.PlacePendingOrderAsync(a.AccountId, sym.SymbolId, ProtoOAOrderType.Stop, ProtoOATradeSide.Buy, sym.VolumeForLots(0.01),
                price, sl, null, bot.Identity.Label("TEST"), Guid.NewGuid().ToString("N")[..16], "Scout test order", TimeSpan.FromSeconds(10),
                trailingStopLoss: true, expiresUtc: expires);
            var seen = (await session.ReconcileAsync(a.AccountId)).Order.Single(o => o.OrderId == order.OrderId);
            bot.Log.Information("Test order {OrderId}: Buy Stop {Price}, SL {Sl}, trailing {Trailing}, time in force {Tif}, expires {Expires:yyyy-MM-dd HH:mm:ss}Z, label {Label}",
                seen.OrderId, seen.StopPrice, seen.StopLoss, seen.TrailingStopLoss, seen.TimeInForce,
                DateTimeOffset.FromUnixTimeMilliseconds(seen.ExpirationTimestamp).UtcDateTime, seen.TradeData.Label);
            await session.CancelOrderAsync(a.AccountId, order.OrderId, TimeSpan.FromSeconds(10));
            bot.Log.Information("Test order {OrderId} cancelled", order.OrderId);
            done.TrySetResult();
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    };
    await BotRunner.RunTogetherAsync(ct, [bot.Connection.RunAsync, async c => await done.Task.WaitAsync(c)]);
}

// Closed candles of one symbol from the first account, oldest first, as CSV (time_utc,open,high,low,close), in
// 30-day requests. For the detector tests and the trial review (scout.md §5).
static async Task CandlesAsync(BotContext bot, ScoutAccount a, string symbol, string tf, DateTime fromUtc, string file, CancellationToken ct)
{
    var done = new TaskCompletionSource();
    bot.Connection.OnReady = async (session, _, _) =>
    {
        try
        {
            var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
            var name = a.SymbolNames?.GetValueOrDefault(symbol) ?? symbol;
            var sym = (await catalog.GetDetailsAsync(session, new Dictionary<string, string> { [symbol] = name })).Single();
            var len = Timeframes.Length(tf);
            var now = DateTime.UtcNow;
            var all = new SortedDictionary<DateTime, Candle>();
            for (var from = fromUtc; from < now; from += TimeSpan.FromDays(30))
            {
                foreach (var c in await session.GetTrendbarsAsync(a.AccountId, sym.SymbolId, Timeframes.Period(tf), from, Min(from + TimeSpan.FromDays(30), now)))
                    if (c.OpenUtc + len <= now)
                        all[c.OpenUtc] = c;
                await Task.Delay(300, ct);
            }
            var f = "F" + sym.Digits;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllLines(file, ["time_utc,open,high,low,close", .. all.Values.Select(c =>
                $"{CsvFormat.Time(c.OpenUtc)},{c.Open.ToString(f, inv)},{c.High.ToString(f, inv)},{c.Low.ToString(f, inv)},{c.Close.ToString(f, inv)}")]);
            bot.Log.Information("{Count} {Symbol} {Tf} candles → {File}", all.Count, symbol, tf, file);
            done.TrySetResult();
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    };
    await BotRunner.RunTogetherAsync(ct, [bot.Connection.RunAsync, async c => await done.Task.WaitAsync(c)]);
}

static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

// The size each symbol's orders really get (scout.md §2): the broker raises a size below its minimum and rounds down to its step.
static void LogOrderSizes(Serilog.ILogger log, string broker, ScoutConfig config, IReadOnlyList<SymbolInfo> symbols)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    foreach (var s in symbols)
    {
        var want = config.LotsFor(s.Name);
        if (s.VolumeNotAbove(want) is not { } volume)
        {
            log.Event("LotsRefused").Warning("{Broker} {Symbol}: {Want} lot is below the broker's minimum {Min}; no orders on this symbol until its size is changed",
                broker, s.Name, want, s.LotsForVolume(s.MinVolume));
            continue;
        }
        var real = s.LotsForVolume(volume);
        if (Math.Abs(real - want) > 1e-9)
            log.Event("LotsAdjusted").Warning("{Broker} {Symbol}: {Want} lot is not a size the broker takes (step {Step}); orders use {Real} lot",
                broker, s.Name, want, s.LotsForVolume(s.StepVolume), real);
    }
    log.Information("{Broker}: order size per symbol: {Sizes}", broker, string.Join(", ", symbols.Where(s => s.VolumeNotAbove(config.LotsFor(s.Name)) is not null).Select(s =>
    {
        var real = s.LotsForVolume(s.VolumeNotAbove(config.LotsFor(s.Name))!.Value);
        return $"{s.Name} {real.ToString("0.##", inv)} lot = {(real * s.LotUnits).ToString("0.##", inv)} {s.BaseAsset}";
    })));
}
