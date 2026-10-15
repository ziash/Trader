using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Trader.Core.Logging;

/// <summary>
/// Adds a "UtcTimestamp" property so output templates can print UTC time
/// (Serilog's built-in {Timestamp} is local time). Plan Section 5.2: logs in UTC.
/// </summary>
public sealed class UtcTimestampEnricher : ILogEventEnricher
{
    public const string PropertyName = "UtcTimestamp";

    /// <summary>Use in output templates: "{UtcTimestamp:yyyy-MM-dd HH:mm:ss.fff}Z".</summary>
    public const string Template = "{" + PropertyName + ":yyyy-MM-dd HH:mm:ss.fff}Z";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, logEvent.Timestamp.UtcDateTime));
}

public static class UtcTimestampEnricherExtensions
{
    public static LoggerConfiguration WithUtcTimestamp(this LoggerEnrichmentConfiguration enrich) =>
        enrich.With<UtcTimestampEnricher>();
}
