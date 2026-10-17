using Serilog;
using Serilog.Events;
using Trader.Core.Hosting;

namespace Trader.Core.Logging;

/// <summary>The one logging setup all bots share: console, UTC timestamps, instance ID on every line.</summary>
public static class BotLogging
{
    public const string BotProperty = "Bot";

    public static ILogger Create(BotIdentity? identity, bool verbose)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.WithUtcTimestamp();

        var template = UtcTimestampEnricher.Template + " [{Level:u3}] ";
        if (identity is not null)
        {
            config = config.Enrich.WithProperty(BotProperty, identity.InstanceId);
            template += "[{" + BotProperty + "}] ";
        }

        return config
            .WriteTo.Console(outputTemplate: template + "{Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
