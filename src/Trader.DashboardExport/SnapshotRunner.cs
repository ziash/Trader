using System.Text.Json;
using Serilog;
using Trader.Core.Config;
using Trader.Core.Csv;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.DashboardExport;

/// <summary>
/// The account snapshot (dashboard.md §3.2, §3.3; build step 5a). Connects with the shared code and only asks: account,
/// positions and orders, open results, deals, order history (labels), symbol details and candles. Writes the snapshot into
/// the outbox and adds account.csv and deals.csv rows and the trade pictures under logs/&lt;broker&gt;/&lt;ctid&gt;/Dashboard/.
/// Never refreshes the tokens (TM does) and never sends an order message.
/// </summary>
public sealed class SnapshotRunner(string home, Outbox outbox, ILogger log)
{
    /// <summary>How far back deals and TM's actions are read: the upload's window (§3.1).</summary>
    public const int Days = 14;

    /// <summary>The broker allows 5 candle requests per second; one picture is one request.</summary>
    private static readonly TimeSpan CandlePause = TimeSpan.FromMilliseconds(250);

    public static readonly string[] AccountHeader =
        ["time_utc", "balance_usd", "equity_usd", "margin_used_usd", "free_margin_usd", "margin_level_pct", "leverage", "positions", "pending_orders"];

    /// <summary>0 = every account done; 2 = cTrader could not be reached or an account failed (the upload goes on without it).</summary>
    public async Task<int> RunAsync(IReadOnlyList<DashAccount> accounts, DateTime nowUtc)
    {
        TraderSecrets secrets;
        try
        {
            secrets = SecretsFile.Load(SecretsFile.DefaultPath);
        }
        catch (SecretsException e)
        {
            log.Error("Snapshot skipped: {Message}", e.Message);
            return 2;
        }

        var failed = 0;
        try
        {
            await using var session = await CTraderSession.ConnectAsync(secrets, log);
            await session.AuthenticateApplicationAsync(secrets);
            foreach (var account in accounts)
            {
                try
                {
                    await session.AuthenticateAccountAsync(account.AccountId, secrets);
                    await AccountAsync(session, account, nowUtc);
                }
                catch (Exception e) when (e is CTraderApiException or TimeoutException or IOException or InvalidOperationException)
                {
                    failed++;
                    log.Warning("Snapshot of {Account} failed: {Message}", account.Id, e.Message);
                }
            }
        }
        catch (Exception e) when (e is CTraderApiException or TimeoutException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            log.Warning("Snapshot skipped, cTrader not reached: {Message}", e.Message);
            return 2;
        }
        return failed == 0 ? 0 : 2;
    }

    private async Task AccountAsync(CTraderSession session, DashAccount account, DateTime now)
    {
        var ctid = account.AccountId;
        var trader = await session.GetTraderAsync(ctid);
        var reconcile = await session.ReconcileAsync(ctid);
        var pnl = reconcile.Position.Count > 0 ? await session.GetUnrealizedPnLAsync(ctid) : new ProtoOAGetPositionUnrealizedPnLRes();
        var deals = await session.GetDealsAsync(ctid, now.AddDays(-Days), now);
        // A day more of orders, so the order a mirror is named after (O<orderId>) is found even when it was placed before the window.
        var orders = await session.GetOrdersAsync(ctid, now.AddDays(-Days - 1), now);

        var ids = deals.Select(d => d.SymbolId)
            .Concat(reconcile.Position.Select(p => p.TradeData.SymbolId))
            .Concat(reconcile.Order.Select(o => o.TradeData.SymbolId)).Distinct().ToList();
        var symbols = await SymbolsAsync(session, ctid, ids);

        var linker = new TradeLinker(SnapshotBuilder.PositionOfOrder(orders, deals));
        var labelOfOrder = orders.GroupBy(o => o.OrderId).ToDictionary(g => g.Key, g => g.First().TradeData.Label ?? "");
        var labelOfPosition = SnapshotBuilder.LabelOfPosition(orders, reconcile);
        var rows = deals.Where(d => d.DealStatus is ProtoOADealStatus.Filled or ProtoOADealStatus.PartiallyFilled && symbols.ContainsKey(d.SymbolId))
            .Select(d => SnapshotBuilder.Deal(d, symbols[d.SymbolId],
                labelOfOrder.GetValueOrDefault(d.OrderId) ?? labelOfPosition.GetValueOrDefault(d.PositionId) ?? "", linker))
            .ToList();

        var snapshot = SnapshotBuilder.Snapshot(account, now, trader, reconcile, pnl, symbols, linker);
        var dir = AccountDir(account);
        var conditions = 0;
        try
        {
            var rows2 = await ConditionsAsync(session, account, trader, snapshot.Status.FreeMargin);
            snapshot = snapshot with { Conditions = rows2 };
            conditions = ConditionsMath.AppendChanged(DayFile(dir, DateOnly.FromDateTime(now), "conditions.csv"), rows2, now);
        }
        catch (Exception e) when (e is CTraderApiException or TimeoutException or InvalidOperationException)
        {
            log.Warning("Trading conditions of {Account} not read: {Message}", account.Id, e.Message); // the rest of the snapshot still goes up
        }
        if (!outbox.Write($"snapshot-{account.Broker}-{ctid}.json", JsonSerializer.Serialize(snapshot, StatusReport.Json)))
            throw new InvalidOperationException("the snapshot would contain a secret value");

        var added = AppendDeals(dir, rows, DateOnly.FromDateTime(now).AddDays(-Days - 1));
        using (var accountCsv = new DailyCsvWriter(d => DayFile(dir, d, "account.csv"), AccountHeader))
        {
            var s = snapshot.Status;
            accountCsv.Write(now,
            [
                CsvFormat.Time(now), CsvFormat.Num(s.Balance, 2), CsvFormat.Num(s.Equity, 2), CsvFormat.Num(s.MarginUsed, 2), CsvFormat.Num(s.FreeMargin, 2),
                CsvFormat.Num(s.MarginLevelPct, 1), s.Leverage, CsvFormat.Int(s.Positions), CsvFormat.Int(s.PendingOrders),
            ]);
        }

        var mainOrders = orders
            .Where(o => !o.ClosingOrder && o.PositionId > 0 && o.OrderStatus == ProtoOAOrderStatus.OrderStatusFilled)
            .GroupBy(o => o.PositionId)
            .ToDictionary(g => g.Key, g =>
            {
                var o = g.OrderBy(x => x.TradeData.OpenTimestamp).First();
                var s = symbols.GetValueOrDefault(o.TradeData.SymbolId);
                double R(double v) => s?.Round(v) ?? v;
                double? level = o.OrderType is ProtoOAOrderType.Stop or ProtoOAOrderType.StopLimit && o.HasStopPrice ? R(o.StopPrice)
                    : o.OrderType == ProtoOAOrderType.Limit && o.HasLimitPrice ? R(o.LimitPrice) : null;
                return new MainOrder(o.OrderId, DateTimeOffset.FromUnixTimeMilliseconds(o.TradeData.OpenTimestamp).UtcDateTime, level,
                    o.HasStopLoss ? R(o.StopLoss) : null);
            });
        var actions = TradeHistory.ReadActions(dir, DateOnly.FromDateTime(now).AddDays(-Days));
        var trades = TradeHistory.Build(rows, snapshot, mainOrders, symbols.Values.ToDictionary(s => s.Name, s => s.SymbolId), actions);

        var drawn = 0;
        foreach (var t in trades.Where(TradeHistory.Pictured))
        {
            if (!symbols.TryGetValue(t.SymbolId, out var sym) || !NeedsPicture(dir, t, now))
                continue;
            var start = t.StartUtc;
            var end = t.ClosedUtc ?? now;
            var tf = TradeChart.TimeframeFor(start, end);
            var len = TradeChart.Length(tf);
            var to = end + len * TradeChart.After < now ? end + len * TradeChart.After : now;
            var candles = await session.GetTrendbarsAsync(ctid, t.SymbolId, tf == "H4" ? ProtoOATrendbarPeriod.H4 : ProtoOATrendbarPeriod.H1,
                start - len * TradeChart.Before, to);
            await Task.Delay(CandlePause);
            if (candles.Count == 0)
                continue;
            var picture = new TradePicture(account.Broker, ctid, t.Trade, t.Owner, t.Symbol, sym.Digits, tf, start, t.ClosedUtc,
                t.Main?.Level, t.StopLossSteps(), t.HedgeSteps(), t.Deals, Math.Round(t.Result, 2), t.Open);
            WriteAtomic(PicturePath(dir, t), TradeChart.Svg(picture, candles));
            drawn++;
        }

        log.Information("Snapshot {Account}: {Status}; {Positions} positions, {Orders} orders; {Deals} deals in {Days} days ({Added} new); {Trades} trades, {Drawn} pictures drawn; conditions of {Symbols} symbols ({Changed} rows written)",
            account.Id, AccountStatus.From(trader, reconcile, pnl, now).Describe(), snapshot.Positions.Count, snapshot.Orders.Count, rows.Count, Days, added, trades.Count, drawn,
            snapshot.Conditions?.Count ?? 0, conditions);
    }

    /// <summary>How long prices are listened to for the spread and the USD conversion (dashboard.md §2.5).</summary>
    private static readonly TimeSpan PriceWait = TimeSpan.FromSeconds(3);

    /// <summary>The trading conditions of the symbols Scout and TM work on (dashboard.md §2.5). Read-only.</summary>
    private async Task<List<ConditionsRow>> ConditionsAsync(CTraderSession session, DashAccount account, ProtoOATrader trader, double freeMargin)
    {
        var ctid = account.AccountId;
        var names = ConditionsMath.ConfiguredSymbols(Path.Combine(home, "config")).GetValueOrDefault(account.Id) ?? [];
        if (names.Count == 0)
            return [];
        var catalog = await SymbolCatalog.LoadAsync(session, ctid);
        var light = names.Select(n => (Name: n, Light: catalog.Find(n))).Where(x => x.Light is not null).ToList();
        var details = (await session.GetSymbolDetailsAsync(ctid, light.Select(x => x.Light!.SymbolId))).ToDictionary(s => s.SymbolId);

        // The symbols that convert each quote currency into USD (as the Audit Bot does).
        var conversions = new Dictionary<string, (long SymbolId, bool Invert)>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in light.Select(x => catalog.AssetName(x.Light!.QuoteAssetId)).Distinct(StringComparer.OrdinalIgnoreCase).Where(q => !q.Equals("USD", StringComparison.OrdinalIgnoreCase)))
            if (catalog.FindConversion(q, "USD") is { } c && catalog.Find(c.Symbol) is { } cs)
                conversions[q] = (cs.SymbolId, c.Invert);

        // A short price subscription: bid and ask now, for the spread and the conversions.
        var feed = new PriceFeed();
        var ids = light.Select(x => x.Light!.SymbolId).Concat(conversions.Values.Select(v => v.SymbolId)).Distinct().ToList();
        session.MessageReceived += feed.Handle;
        try
        {
            await session.SubscribeSpotsAsync(ctid, ids);
            var until = DateTime.UtcNow + PriceWait;
            while (DateTime.UtcNow < until && ids.Any(id => feed.Last(ctid, id) is null))
                await Task.Delay(200);
            await session.UnsubscribeSpotsAsync(ctid, ids);
        }
        finally
        {
            session.MessageReceived -= feed.Handle;
        }

        // A closed market sends no price: its last 1H close stands in for the USD figures.
        var closes = new Dictionary<long, double?>();
        async Task<double?> Price(long symbolId)
        {
            if (feed.Last(ctid, symbolId) is { } q)
                return q.Mid;
            if (!closes.TryGetValue(symbolId, out var close))
            {
                var now = DateTime.UtcNow;
                var candles = await session.GetTrendbarsAsync(ctid, symbolId, ProtoOATrendbarPeriod.H1, now.AddDays(-5), now);
                await Task.Delay(CandlePause);
                closes[symbolId] = close = candles.Count > 0 ? candles[^1].Close : null;
            }
            return close;
        }

        var leverages = new Dictionary<long, ProtoOADynamicLeverage?>();
        var rows = new List<ConditionsRow>();
        foreach (var (name, l) in light)
        {
            if (!details.TryGetValue(l!.SymbolId, out var f))
                continue;
            var quoteAsset = catalog.AssetName(l.QuoteAssetId);
            double? usdPerQuote = quoteAsset.Equals("USD", StringComparison.OrdinalIgnoreCase) ? 1
                : conversions.TryGetValue(quoteAsset, out var conv) && await Price(conv.SymbolId) is { } cp and > 0 ? (conv.Invert ? 1 / cp : cp) : null;
            if (f.HasLeverageId && !leverages.ContainsKey(f.LeverageId))
                leverages[f.LeverageId] = await session.GetDynamicLeverageAsync(ctid, f.LeverageId);
            var margins = await session.GetExpectedMarginAsync(ctid, f.SymbolId, ConditionsMath.MarginVolumes(f));
            rows.Add(ConditionsMath.Compute(f, name, catalog.AssetName(l.BaseAssetId), quoteAsset, trader.LeverageInCents,
                f.HasLeverageId ? leverages[f.LeverageId] : null, feed.Last(ctid, f.SymbolId), await Price(f.SymbolId), usdPerQuote, margins, freeMargin));
        }
        return rows;
    }

    private async Task<Dictionary<long, SymbolFacts>> SymbolsAsync(CTraderSession session, long ctid, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return [];
        var names = (await session.GetSymbolsAsync(ctid)).ToDictionary(s => s.SymbolId, s => s.SymbolName);
        var details = await session.GetSymbolDetailsAsync(ctid, ids);
        return details.ToDictionary(s => s.SymbolId, s => new SymbolFacts(s.SymbolId, names.GetValueOrDefault(s.SymbolId, $"symbol {s.SymbolId}"), s.Digits, s.LotSize / 100));
    }

    private string AccountDir(DashAccount a) => Path.Combine(home, "logs", a.Broker, a.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string DayFile(string accountDir, DateOnly day, string name) =>
        Path.Combine(accountDir, "Dashboard", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), name);

    public static string PicturePath(string accountDir, TradeFacts t) =>
        Path.Combine(Path.GetDirectoryName(DayFile(accountDir, DateOnly.FromDateTime(t.StartUtc), "x"))!, "charts", TradeChart.FileName(t.Trade));

    /// <summary>Drawn again while the trade is open, after anything new happened to it, and until the candles after its close are in.</summary>
    public static bool NeedsPicture(string accountDir, TradeFacts t, DateTime now)
    {
        var file = new FileInfo(PicturePath(accountDir, t));
        if (!file.Exists || t.Open || t.LastUtc >= file.LastWriteTimeUtc)
            return true;
        var tf = TradeChart.TimeframeFor(t.StartUtc, t.ClosedUtc ?? now);
        return t.ClosedUtc + TradeChart.Length(tf) * TradeChart.After > file.LastWriteTimeUtc;
    }

    /// <summary>Adds each deal not written yet to the deals.csv of its day; returns how many were added.</summary>
    public static int AppendDeals(string accountDir, IEnumerable<DealRow> rows, DateOnly firstDay)
    {
        var known = new HashSet<string>();
        var dash = Path.Combine(accountDir, "Dashboard");
        if (Directory.Exists(dash))
            foreach (var dayDir in Directory.EnumerateDirectories(dash))
            {
                var file = Path.Combine(dayDir, "deals.csv");
                if (DateOnly.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", out var d) && d >= firstDay && File.Exists(file))
                    foreach (var r in CsvTable.Read(file).Rows)
                        known.Add(r["deal_id"]);
            }
        var added = 0;
        using var writer = new DailyCsvWriter(d => DayFile(accountDir, d, "deals.csv"), DealRow.Header);
        foreach (var row in rows.OrderBy(r => r.TimeUtc).ThenBy(r => r.DealId))
        {
            if (!known.Add(CsvFormat.Int(row.DealId)))
                continue;
            writer.Write(row.TimeUtc, row.Values());
            added++;
        }
        return added;
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
