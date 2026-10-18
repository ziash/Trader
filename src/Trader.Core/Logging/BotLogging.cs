using Serilog;
using Serilog.Core;
using Serilog.Events;
using Trader.Core.Hosting;

namespace Trader.Core.Logging;

/// <summary>The one logging setup all bots share: console, UTC timestamps, instance ID on every line.</summary>
public static class BotLogging
{
    public const string BotProperty = "Bot";

    /// <param name="tap">Optional extra sink that receives every event (see <see cref="LogTap"/>).</param>
    public static ILogger Create(BotIdentity? identity, bool verbose, ILogEventSink? tap = null)
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

        if (tap is not null)
            config = config.WriteTo.Sink(tap);

        return config
            .WriteTo.Console(outputTemplate: template + "{Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
