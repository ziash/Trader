// Audit Bot (plan Section 6). Phase 1 step 2: stays connected to the cTrader Open API (demo) —
// heartbeat, dead-line detection, auto-reconnect, token refresh — until Ctrl+C or SIGTERM.
// Price feed, registry and probe orders are added in steps 3–5 (plan 8.1).
//
// Options:
//   --verbose, -v              debug logging (every request/response)
//   --test-drop-after <sec>    diagnostic: drop the connection <sec> seconds after each connect
//
// Exit codes: 0 = stopped normally, 1 = secrets/config problem or fatal API problem.

using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using Trader.Core.Config;
using Trader.Core.Logging;
using Trader.Core.OpenApi;

var verbose = args.Contains("--verbose") || args.Contains("-v");
TimeSpan? dropAfter = null;
var dropIndex = Array.IndexOf(args, "--test-drop-after");
if (dropIndex >= 0)
{
    if (dropIndex + 1 >= args.Length || !int.TryParse(args[dropIndex + 1], out var seconds) || seconds <= 0)
    {
        Console.Error.WriteLine("--test-drop-after needs a number of seconds");
        return 1;
    }
    dropAfter = TimeSpan.FromSeconds(seconds);
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
    .Enrich.WithUtcTimestamp()
    .WriteTo.Console(outputTemplate: UtcTimestampEnricher.Template + " [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

using var stop = new CancellationTokenSource();
void RequestStop(PosixSignalContext ctx)
{
    ctx.Cancel = true; // we shut down ourselves
    if (!stop.IsCancellationRequested)
        Log.Information("{Signal} received; stopping", ctx.Signal);
    stop.Cancel();
}
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);

try
{
    var path = SecretsFile.DefaultPath;
    var secrets = SecretsFile.Load(path);
    Log.Information("Audit Bot starting (PID {Pid}); secrets from {Path}, environment {Env}",
        Environment.ProcessId, path, secrets.Environment);
    if (dropAfter is not null)
        Log.Warning("Test mode: the connection will be dropped {Seconds} s after each connect", dropAfter.Value.TotalSeconds);

    var manager = new ConnectionManager(path, secrets, Log.Logger, new ConnectionOptions { SimulateDropAfter = dropAfter })
    {
        OnReady = (_, accounts, _) =>
        {
            Log.Information("Ready: {Count} demo account(s) on the connection", accounts.Count);
            return Task.CompletedTask;
        },
    };

    await manager.RunAsync(stop.Token);
    return 0;
}
catch (SecretsException ex)
{
    Log.Error("{Message}", ex.Message);
    return 1;
}
catch (FatalConnectionException ex)
{
    Log.Fatal("{Message}. Stopping.", ex.Message);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
