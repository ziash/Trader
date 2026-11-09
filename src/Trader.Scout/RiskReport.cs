using Trader.Core.Hosting;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary><c>--risk</c> (scout.md §5): the money at risk on the account, by symbol and group, against the portfolio limits, whether they are on or off. Read-only.</summary>
internal static class RiskReport
{
    /// <summary>TM's saved trades on this account (tm.md §6).</summary>
    public static string TmStateFile(ScoutAccount a) => Path.Combine(TradebotPaths.Root, "run", "tm", $"{a.Broker}-{a.AccountId}.json");

    public static async Task RunAsync(BotContext bot, ScoutConfig config, ScoutAccount a, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        bot.Connection.OnReady = async (session, _, _) =>
        {
            try
            {
                var catalog = await SymbolCatalog.LoadAsync(session, a.AccountId);
                var symbols = await catalog.GetDetailsAsync(session, a.BrokerSymbolNames);
                var conversions = SizeReport.Conversions(catalog, symbols);
                var feed = await SizeReport.ReadPricesAsync(session, a, symbols, conversions);
                var gateway = new SessionGateway(session, feed, conversions);
                var rec = await session.ReconcileAsync(a.AccountId);
                var status = await session.GetAccountStatusAsync(a.AccountId, rec);
                var byId = symbols.ToDictionary(s => s.SymbolId);
                var book = RiskBook.Build(config.PortfolioInForce, status.Equity, new RiskInputs(rec, byId, c => gateway.UsdPer(a.AccountId, c),
                    id => gateway.LastQuote(a.AccountId, id)?.Spread ?? 0, (label, id) => AccountScout.KeyOf(label, id, byId), config.SizingInForce, TmLinks.Load(TmStateFile(a))));
                foreach (var line in book.Report())
                    Console.WriteLine(line);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        };
        await BotRunner.RunTogetherAsync(ct, [bot.Connection.RunAsync, async c => await done.Task.WaitAsync(c)]);
    }
}
