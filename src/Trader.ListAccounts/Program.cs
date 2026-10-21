// Phase 1, step 1: connect to the cTrader Open API (demo), authenticate the app,
// list the accounts the access token can reach, and show basic details of each.
// Read-only: places no orders and does not refresh tokens.
//
// Exit codes: 0 = OK, 1 = secrets/config problem, 2 = API or network problem.

using Serilog;
using Trader.Core.Config;
using Trader.Core.Logging;
using Trader.Core.OpenApi;

var verbose = args.Contains("--verbose") || args.Contains("-v");

Log.Logger = BotLogging.Create(identity: null, verbose);

try
{
    return await RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> RunAsync()
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
                row.AddRange(["-", "-", "-", "-", NotTouched]);
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

                row.AddRange([
                    trader.AccountType.ToString(),
                    $"{balance.ToString($"N{digits}")} {currency}",
                    $"1:{trader.LeverageInCents / 100}",
                    trader.BrokerName,
                    "OK",
                ]);
            }
            catch (CTraderApiException ex)
            {
                row.AddRange(["-", "-", "-", "-", ex.ErrorCode]);
                Log.Warning("Account {Ctid}: {Error}", ctid, ex.Message);
            }
            rows.Add([.. row]);
        }

        PrintTable(["ctidTraderAccountId", "Login", "Env", "Type", "Balance", "Leverage", "Broker name", "Status"], rows);
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
