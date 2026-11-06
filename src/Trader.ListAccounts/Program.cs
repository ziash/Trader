// Phase 1, step 1: connect to the cTrader Open API (demo), authenticate the app,
// list the accounts the access token can reach, and show basic details of each: balance, equity and margin
// (AccountStatus, issue-log.md #1).
// Read-only: places no orders and does not refresh tokens.
//
// Exit codes: 0 = OK, 1 = secrets/config problem, 2 = API or network problem.

using Serilog;
using Trader.Core.Config;
using Trader.Core.Logging;
using Trader.Core.OpenApi;

var verbose = args.Contains("--verbose") || args.Contains("-v");
var showPositions = args.Contains("--positions");

Log.Logger = BotLogging.Create(identity: null, verbose);

try
{
    return await RunAsync(showPositions);
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> RunAsync(bool showPositions)
{
    TraderSecrets secrets;
    try
    {
        var path = SecretsFile.DefaultPath;
        secrets = SecretsFile.Load(path);
        Log.Information("Secrets loaded from {Path} (client ID {ClientId}, access token {AccessToken}, environment {Env})",
            path, TraderSecrets.Mask(secrets.ClientId), TraderSecrets.Mask(secrets.AccessToken), secrets.Environment);
    }
    catch (SecretsException ex)
    {
        Log.Error("{Message}", ex.Message);
        return 1;
    }

    try
    {
        await using var session = await CTraderSession.ConnectAsync(secrets, Log.Logger);

        await session.AuthenticateApplicationAsync(secrets);
        Log.Information("Application authenticated");

        var accounts = await session.GetAccountsAsync(secrets);
        Log.Information("Access token grants {Count} account(s)", accounts.Count);

        var rows = new List<string[]>();
        foreach (var acc in accounts)
        {
            var row = new List<string>
            {
                acc.CtidTraderAccountId.ToString(),
                acc.TraderLogin.ToString(),
                acc.IsLive ? "live" : "demo",
            };

            if (acc.IsLive)
            {
                // The demo connection cannot serve live accounts, and live is out of scope before Phase 5.
                row.AddRange(["-", "-", "-", "-", "-", "-", "-", "-", "-", NotTouched]);
                rows.Add([.. row]);
                continue;
            }

            var ctid = (long)acc.CtidTraderAccountId;
            try
            {
                await session.AuthenticateAccountAsync(ctid, secrets);
                var trader = await session.GetTraderAsync(ctid);
                var assets = await session.GetAssetsAsync(ctid);
                var currency = assets.FirstOrDefault(a => a.AssetId == trader.DepositAssetId)?.Name ?? $"asset {trader.DepositAssetId}";
                var digits = trader.HasMoneyDigits ? (int)trader.MoneyDigits : 2;
                var balance = trader.Balance / Math.Pow(10, digits);
                var reconcile = await session.ReconcileAsync(ctid);
                var status = await session.GetAccountStatusAsync(ctid, reconcile);
                if (showPositions)
                    await PrintPositionsAsync(session, ctid, reconcile, acc.TraderLogin);

                row.AddRange([
                    trader.AccountType.ToString(),
                    $"{balance.ToString($"N{digits}")} {currency}",
                    status.Equity.ToString($"N{digits}"),
                    status.UsedMargin.ToString($"N{digits}"),
                    status.FreeMargin.ToString($"N{digits}"),
                    status.MarginLevel is { } level ? $"{level:0} %" : "-",
                    $"{status.Positions} / {status.Orders}",
                    $"1:{trader.LeverageInCents / 100}",
                    trader.BrokerName,
                    "OK",
                ]);
            }
            catch (CTraderApiException ex)
            {
                row.AddRange(["-", "-", "-", "-", "-", "-", "-", "-", "-", ex.ErrorCode]);
                Log.Warning("Account {Ctid}: {Error}", ctid, ex.Message);
            }
            rows.Add([.. row]);
        }

        PrintTable(["ctidTraderAccountId", "Login", "Env", "Type", "Balance", "Equity", "Margin used", "Free margin", "Margin level", "Positions / orders", "Leverage", "Broker name", "Status"], rows);
        return rows.All(r => r[^1] is "OK" or NotTouched) ? 0 : 2;
    }
    catch (CTraderApiException ex)
    {
        Log.Error("Open API error {Code}: {Message}", ex.ErrorCode, ex.Message);
        if (ex.ErrorCode.Contains("TOKEN", StringComparison.OrdinalIgnoreCase))
            Log.Error("The access token looks expired or invalid. Get a new one in the Open API Playground (token refresh is a later step).");
        return 2;
    }
    catch (Exception ex) when (ex is TimeoutException or OpenAPI.Net.Exceptions.ConnectionException
                                  or System.Net.Sockets.SocketException or IOException)
    {
        Log.Error("Network problem: {Message}. Check: openssl s_client -connect demo.ctraderapi.com:5035 -brief", ex.Message);
        return 2;
    }
}

// --positions: per symbol, how many positions, the volume on each side, the margin they hold and their open result.
static async Task PrintPositionsAsync(CTraderSession session, long ctid, ProtoOAReconcileRes reconcile, long login)
{
    if (reconcile.Position.Count == 0)
        return;
    var pnl = (await session.GetUnrealizedPnLAsync(ctid)).Let(r => r.Positions.ToDictionary(p => p.PositionId, p => p.Net / Math.Pow(10, r.MoneyDigits)));
    var names = (await session.GetSymbolsAsync(ctid)).ToDictionary(s => s.SymbolId, s => s.SymbolName);
    var rows = reconcile.Position.GroupBy(p => p.TradeData.SymbolId)
        .Select(g => new
        {
            Name = names.GetValueOrDefault(g.Key, g.Key.ToString()),
            Count = g.Count(),
            Buy = g.Where(p => p.TradeData.TradeSide == ProtoOATradeSide.Buy).Sum(p => p.TradeData.Volume),
            Sell = g.Where(p => p.TradeData.TradeSide == ProtoOATradeSide.Sell).Sum(p => p.TradeData.Volume),
            Margin = g.Sum(p => (long)p.UsedMargin / Math.Pow(10, p.HasMoneyDigits ? p.MoneyDigits : 2)),
            Open = g.Sum(p => pnl.GetValueOrDefault(p.PositionId)),
            NoStop = g.Count(p => !(p.HasStopLoss && p.StopLoss > 0)),
        })
        .OrderBy(r => r.Open).ToList();
    Console.WriteLine($"\nAccount {login}: open positions by symbol (volume in Open API units)");
    PrintTable(["Symbol", "Positions", "Buy volume", "Sell volume", "Margin", "Open result", "Without stop-loss"],
        rows.Select(r => new[] { r.Name, r.Count.ToString(), r.Buy.ToString("N0"), r.Sell.ToString("N0"), r.Margin.ToString("N2"), r.Open.ToString("N2"), r.NoStop.ToString() }).ToList());
}

static void PrintTable(string[] header, List<string[]> rows)
{
    var widths = header.Select((h, i) => rows.Select(r => r[i].Length).Prepend(h.Length).Max()).ToArray();
    string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();

    Console.WriteLine();
    Console.WriteLine(Line(header));
    Console.WriteLine(Line(widths.Select(w => new string('-', w)).ToArray()));
    foreach (var r in rows)
        Console.WriteLine(Line(r));
    Console.WriteLine();
}

partial class Program
{
    private const string NotTouched = "not touched (live)";
}

static class Extensions
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
