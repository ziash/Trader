using System.Globalization;
using Trader.Core.Hosting;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary>
/// <c>--sizes</c> (scout.md §5): what the money-at-risk sizing would give on each symbol, beside today's fixed lots, for typical stop distances. Read-only: it
/// reads prices, candles and the account, and places nothing, whether sizing is on or off.
/// </summary>
internal static class SizeReport
{
    // Stop distance in ATR at the 5th percentile, the median and the 95th percentile of the replay (review v1.0).
    private static readonly double[] StopAtr = [0.57, 1.08, 2.05];
    private static readonly TimeSpan PriceWait = TimeSpan.FromSeconds(6);

    /// <summary>The symbols that price each non-USD quote currency in USD.</summary>
    public static IReadOnlyDictionary<string, UsdConversion> Conversions(SymbolCatalog catalog, IReadOnlyList<SymbolInfo> symbols)
    {
        var found = new Dictionary<string, UsdConversion>(StringComparer.OrdinalIgnoreCase);
        foreach (var quote in symbols.Select(s => s.QuoteAsset).Distinct(StringComparer.OrdinalIgnoreCase).Where(q => !q.Equals("USD", StringComparison.OrdinalIgnoreCase)))
            if (catalog.FindConversion(quote, "USD") is { } c && catalog.Find(c.Symbol) is { } light)
                found[quote] = new UsdConversion(light.SymbolId, c.Invert);
        return found;
    }

    /// <summary>A few seconds of live prices for the symbols and their conversions; a closed market gives none.</summary>
    public static async Task<PriceFeed> ReadPricesAsync(CTraderSession session, ScoutAccount a, IReadOnlyList<SymbolInfo> symbols, IReadOnlyDictionary<string, UsdConversion> conversions)
    {
        var feed = new PriceFeed();
        var ids = symbols.Select(s => s.SymbolId).Concat(conversions.Values.Select(c => c.SymbolId)).Distinct().ToList();
        session.MessageReceived += feed.Handle;
        try
        {
            await session.SubscribeSpotsAsync(a.AccountId, ids);
            var until = DateTime.UtcNow + PriceWait;
            while (DateTime.UtcNow < until && ids.Any(id => feed.Last(a.AccountId, id) is null))
                await Task.Delay(200);
            await session.UnsubscribeSpotsAsync(a.AccountId, ids);
        }
        finally
        {
            session.MessageReceived -= feed.Handle;
        }
        return feed;
    }

    public static async Task RunAsync(BotContext bot, ScoutConfig config, ScoutAccount a, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        bot.Connection.OnReady = async (session, _, _) =>
        {
            try
            {
                await PrintAsync(session, config, a);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        };
        await BotRunner.RunTogetherAsync(ct, [bot.Connection.RunAsync, async c => await done.Task.WaitAsync(c)]);
    }

    private static async Task PrintAsync(CTraderSession session, ScoutConfig config, ScoutAccount a)
    {
        var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
        var symbols = await catalog.GetDetailsAsync(session, a.BrokerSymbolNames);
        var conversions = Conversions(catalog, symbols);
        var status = await session.GetAccountStatusAsync(a.AccountId);

        var feed = await ReadPricesAsync(session, a, symbols, conversions);

        var inv = CultureInfo.InvariantCulture;
        var p = config.SizingInForce;
        var now = DateTime.UtcNow;
        async Task<IReadOnlyList<Candle>> H1(long id) => await session.GetTrendbarsAsync(a.AccountId, id, ProtoOATrendbarPeriod.H1, now.AddDays(-30), now);
        async Task<double?> UsdPer(string currency)
        {
            if (currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (!conversions.TryGetValue(currency, out var c))
                return null;
            var mid = feed.Last(a.AccountId, c.SymbolId)?.Mid ?? (await H1(c.SymbolId)).LastOrDefault()?.Close;
            return mid is null ? null : c.Invert ? 1 / mid : mid;
        }

        Console.WriteLine($"Equity ${status.Equity.ToString("N2", inv)}; sizing is {(p.Enabled ? "ON" : "OFF")}; budget {p.RiskPercent.ToString("0.##", inv)} % per trade = ${(status.Equity * p.RiskPercent / 100).ToString("N2", inv)}, {p.Legs} legs. Median stop = {StopAtr[1].ToString("0.00", inv)} ATR.");
        Console.WriteLine("symbol,price,spread,atr,fixed_lots,fixed_risk_usd,new_lots_tight,new_lots_median,new_lots_wide,new_risk_usd,note");
        foreach (var s in symbols)
        {
            var candles = await H1(s.SymbolId);
            var detector = new Detector(config.SetupSettings);
            foreach (var c in candles)
                detector.Add(c);
            var live = feed.Last(a.AccountId, s.SymbolId);
            var price = live?.Mid ?? candles.LastOrDefault()?.Close ?? 0;
            var spread = live?.Spread ?? 0;
            var atr = detector.Atr ?? 0;
            var rate = await UsdPer(s.QuoteAsset);
            var note = live is null ? "market closed: no live spread; " : "";

            SizeDecision At(double stopAtr) => PositionSizer.Decide(s, p, status.Equity, price, price - stopAtr * atr, spread, rate);
            var median = At(StopAtr[1]);
            var fixedLots = config.LotsFor(s.Name);
            var perLot = rate is { } r ? (StopAtr[1] * atr + p.SlippageSpreads * spread) * s.LotUnits * r + (s.Commission?.RoundTripPerLotUsd(price * s.LotUnits * r, r) ?? 0) : 0;
            string Lot(SizeDecision d) => d.Volume is { } v ? s.LotsForVolume(v).ToString("0.###", inv) : "refused";
            if (median.Refusal is { } why)
                note += why;
            Console.WriteLine(string.Join(',', s.Name, price.ToString("F" + s.Digits, inv), spread.ToString("F" + s.Digits, inv), atr.ToString("F" + s.Digits, inv), fixedLots.ToString("0.###", inv),
                (p.Legs * fixedLots * perLot).ToString("0.00", inv), Lot(At(StopAtr[0])), Lot(median), Lot(At(StopAtr[2])), median.RiskCash.ToString("0.00", inv), note.TrimEnd(' ', ';')));
        }
    }
}
