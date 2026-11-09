using System.Globalization;
using System.Text.Json;
using Trader.Core.Hosting;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary><c>--export-symbols</c> (Rewind spec §7.2): the account's symbol details, swaps, commission and margin rate, in Rewind's symbols.json format. Read-only; needs open markets for the margin rate.</summary>
internal static class SymbolExport
{
    public static async Task RunAsync(BotContext bot, ScoutAccount a, string path, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        bot.Connection.OnReady = async (session, _, _) =>
        {
            try
            {
                await WriteAsync(session, a, path);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        };
        await BotRunner.RunTogetherAsync(ct, [bot.Connection.RunAsync, async c => await done.Task.WaitAsync(c)]);
    }

    private static async Task WriteAsync(CTraderSession session, ScoutAccount a, string path)
    {
        var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
        var symbols = await catalog.GetDetailsAsync(session, a.BrokerSymbolNames);
        var conversions = SizeReport.Conversions(catalog, symbols);
        var feed = await SizeReport.ReadPricesAsync(session, a, symbols, conversions);
        var gateway = new SessionGateway(session, feed, conversions);
        var raw = (await session.GetSymbolDetailsAsync(a.AccountId, symbols.Select(s => s.SymbolId))).ToDictionary(s => s.SymbolId);
        var rows = new List<object>();
        foreach (var s in symbols.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var d = raw[s.SymbolId];
            if (feed.Last(a.AccountId, s.SymbolId) is not { } q || gateway.UsdPer(a.AccountId, s.QuoteAsset) is not { } rate)
            {
                Console.Error.WriteLine($"{s.Name}: no live price (market closed): left out");
                continue;
            }
            var lot = s.LotUnits * 100;
            var margin = (await session.GetExpectedMarginAsync(a.AccountId, s.SymbolId, [lot])).Single();
            var notional = s.LotUnits * q.Mid * rate;
            var commission = s.Commission?.RoundTripPerLotUsd(notional, rate) is { } roundTrip ? roundTrip / 2 / notional * 1e6 : 0;
            rows.Add(new
            {
                name = s.Name, @base = s.BaseAsset, quote = s.QuoteAsset, digits = s.Digits, pipPosition = s.PipPosition, lotSize = lot, minVolume = s.MinVolume, stepVolume = s.StepVolume,
                maxVolume = s.MaxVolume == long.MaxValue ? 1_000_000_000_000L : s.MaxVolume, commissionPerMillionUsd = Math.Round(commission, 4),
                leverage = margin.Buy > 0 ? Math.Round(notional / margin.Buy, 2) : 0, swapLong = d.SwapLong, swapShort = d.SwapShort,
                swapPercent = d.SwapCalculationType == ProtoOASwapCalculationType.Percentage, swapTripleDay = (int)d.SwapRollover3Days is 0 ? -1 : (int)d.SwapRollover3Days % 7, // ProtoOADayOfWeek: 0 none, 1 Monday … 7 Sunday
            });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { symbols = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{rows.Count} symbols → {path}"));
    }
}
