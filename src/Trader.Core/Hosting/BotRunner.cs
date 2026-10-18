using System.Runtime.InteropServices;
using Serilog;
using Trader.Core.Config;
using Trader.Core.Logging;
using Trader.Core.OpenApi;

namespace Trader.Core.Hosting;

/// <summary>What a bot gets from <see cref="BotRunner"/>: its identity, a logger, and a ready-to-run connection.</summary>
public sealed class BotContext(BotIdentity identity, ILogger log, LogTap logTap, ConnectionManager connection, string secretsPath, IReadOnlyList<string> args)
{
    public BotIdentity Identity { get; } = identity;
    public ILogger Log { get; } = log;

    /// <summary>Add sinks here to send log events to the bot's own files (e.g. <see cref="CsvEventSink"/> for events.csv).</summary>
    public LogTap LogTap { get; } = logTap;

    /// <summary>Set <see cref="ConnectionManager.OnReady"/> / subscribe to <see cref="ConnectionManager.MessageReceived"/>,
    /// then await <see cref="ConnectionManager.RunAsync"/>.</summary>
    public ConnectionManager Connection { get; } = connection;

    public string SecretsPath { get; } = secretsPath;

    /// <summary>Command-line arguments not used by BotRunner, for the bot's own options.</summary>
    public IReadOnlyList<string> Args { get; } = args;
}

/// <summary>Defaults a bot passes to <see cref="BotRunner.RunAsync"/>. Command-line options override them.</summary>
public sealed class BotOptions
{
    /// <summary>Accounts this bot uses; empty = all demo accounts the token grants. Overridden by --account.</summary>
    public IReadOnlyCollection<long> AccountIds { get; init; } = [];
}

/// <summary>
/// The shared start-up and shut-down for every bot, so a bot's Program.cs only holds bot logic:
///
///     return await BotRunner.RunAsync("MyBot", args, async (bot, ct) =>
///     {
///         bot.Connection.OnReady = (session, accounts, ct) => ...;   // subscribe here
///         await bot.Connection.RunAsync(ct);
///     });
///
/// It parses the common options, sets up logging, loads secrets (demo only), creates the
/// connection, stops cleanly on Ctrl+C / SIGTERM, and maps errors to exit codes.
/// </summary>
public static class BotRunner
{
    public const int ExitOk = 0;
    public const int ExitFatal = 1;     // secrets/config problem or fatal API problem
    public const int ExitUsage = 2;     // bad command-line options
    public const int ExitCrash = 3;     // unexpected error (a bug)

    public const string Usage =
        "Common options:\n" +
        "  --verbose, -v              debug logging (every request/response)\n" +
        "  --account <ctid>           use only this account (repeat for more); default: all demo accounts\n" +
        "  --test-drop-after <sec>    diagnostic: drop the connection <sec> seconds after each connect\n" +
        "  --help, -h                 show this help";

    public static async Task<int> RunAsync(string botName, string[] args, Func<BotContext, CancellationToken, Task> run, BotOptions? options = null)
    {
        var identity = BotIdentity.Create(botName);

        BotArgs parsed;
        try
        {
            parsed = BotArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"{ex.Message}\n\n{Usage}");
            return ExitUsage;
        }
        if (parsed.Help)
        {
            Console.WriteLine($"{botName}\n\n{Usage}");
            return ExitOk;
        }

        var tap = new LogTap();
        var log = BotLogging.Create(identity, parsed.Verbose, tap);
        Log.Logger = log;

        using var stop = new CancellationTokenSource();
        void RequestStop(PosixSignalContext ctx)
        {
            ctx.Cancel = true; // we shut down ourselves
            if (!stop.IsCancellationRequested)
                log.Information("{Signal} received; stopping", ctx.Signal);
            stop.Cancel();
        }
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);

        try
        {
            var path = SecretsFile.DefaultPath;
            var secrets = SecretsFile.Load(path);
            var accountIds = parsed.AccountIds.Count > 0 ? parsed.AccountIds : options?.AccountIds ?? [];
            log.Information("{BotName} starting (PID {Pid}); secrets from {Path}, environment {Env}, accounts {Accounts}",
                botName, Environment.ProcessId, path, secrets.Environment,
                accountIds.Count > 0 ? string.Join(", ", accountIds) : "all demo");
            if (parsed.TestDropAfter is { } drop)
                log.Warning("Test mode: the connection will be dropped {Seconds} s after each connect", drop.TotalSeconds);

            var connection = new ConnectionManager(path, secrets, log, new ConnectionOptions
            {
                AccountIds = accountIds,
                SimulateDropAfter = parsed.TestDropAfter,
            });

            await run(new BotContext(identity, log, tap, connection, path, parsed.Rest), stop.Token);
            log.Information("{BotName} stopped", botName);
            return ExitOk;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log.Information("{BotName} stopped", botName);
            return ExitOk;
        }
        catch (SecretsException ex)
        {
            log.Error("{Message}", ex.Message);
            return ExitFatal;
        }
        catch (FatalConnectionException ex)
        {
            log.Fatal("{Message}. Stopping.", ex.Message);
            return ExitFatal;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Unexpected error; stopping");
            return ExitCrash;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}

/// <summary>Command-line options shared by all bots; anything else is left in <see cref="Rest"/> for the bot.</summary>
internal sealed record BotArgs(bool Verbose, bool Help, IReadOnlyList<long> AccountIds, TimeSpan? TestDropAfter, IReadOnlyList<string> Rest)
{
    public static BotArgs Parse(IReadOnlyList<string> args)
    {
        bool verbose = false, help = false;
        var accounts = new List<long>();
        TimeSpan? drop = null;
        var rest = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                case "--account":
                    accounts.Add(long.TryParse(Value(), out var id) && id > 0
                        ? id
                        : throw new ArgumentException("--account needs a ctidTraderAccountId (a positive number)"));
                    break;
                case "--test-drop-after":
                    drop = int.TryParse(Value(), out var s) && s > 0
                        ? TimeSpan.FromSeconds(s)
                        : throw new ArgumentException("--test-drop-after needs a number of seconds");
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        return new BotArgs(verbose, help, accounts.Distinct().ToList(), drop, rest);
    }
}
