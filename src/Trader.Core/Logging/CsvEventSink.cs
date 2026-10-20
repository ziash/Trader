using System.Collections.Concurrent;
using System.Collections.Immutable;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Trader.Core.Csv;

namespace Trader.Core.Logging;

public static class LogEvents
{
    /// <summary>Property naming the event in events.csv (e.g. "Connected"); without it the event column says "Log".</summary>
    public const string EventProperty = "Event";

    /// <summary>Property with a ctidTraderAccountId: the event is written only to that account's events.csv.</summary>
    public const string AccountProperty = "Account";

    /// <summary><c>log.Event("Connected").Information(...)</c> — names the event for events.csv.</summary>
    public static ILogger Event(this ILogger log, string name) => log.ForContext(EventProperty, name);

    /// <summary><c>log.Event("SymbolMissing").ForAccount(ctid).Warning(...)</c> — routes the event to one account only.</summary>
    public static ILogger ForAccount(this ILogger log, long ctidTraderAccountId) => log.ForContext(AccountProperty, ctidTraderAccountId);
}

/// <summary>
/// A sink that can gain targets after the logger is built. BotRunner puts one in every bot's logger
/// (<see cref="Hosting.BotContext.LogTap"/>), so a bot can add its own files once it knows where they go.
/// </summary>
public sealed class LogTap : ILogEventSink
{
    private ImmutableArray<ILogEventSink> _sinks = [];

    public void Add(ILogEventSink sink) => ImmutableInterlocked.Update(ref _sinks, s => s.Add(sink));

    public void Emit(LogEvent logEvent)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Emit(logEvent);
            }
            catch
            {
                // a broken file must not stop logging elsewhere
            }
        }
    }
}

/// <summary>
/// Writes Information-and-above log events to events.csv files (shared.md §5): time_utc, level, event, detail.
/// An event carrying an Account property goes to that account's file only; others go to every account's file.
/// </summary>
public sealed class CsvEventSink : ILogEventSink, IDisposable
{
    public static readonly string[] Header = ["time_utc", "level", "event", "detail"];

    private static readonly MessageTemplateTextFormatter Detail = new("{Message:lj}");
    private readonly ConcurrentDictionary<long, DailyCsvWriter> _targets = new();

    public void AddAccount(long ctidTraderAccountId, DailyCsvWriter writer) => _targets[ctidTraderAccountId] = writer;

    public void Emit(LogEvent e)
    {
        if (e.Level < LogEventLevel.Information || _targets.IsEmpty)
            return;

        using var text = new StringWriter();
        Detail.Format(e, text);
        var detail = e.Exception is null ? text.ToString() : $"{text} | {e.Exception.GetType().Name}: {e.Exception.Message}";
        var name = e.Properties.TryGetValue(LogEvents.EventProperty, out var ev) && ev is ScalarValue { Value: string s } ? s : "Log";
        string[] row = [CsvFormat.Time(e.Timestamp.UtcDateTime), e.Level.ToString(), name, detail];

        if (e.Properties.TryGetValue(LogEvents.AccountProperty, out var acc) && acc is ScalarValue { Value: long id })
        {
            if (_targets.TryGetValue(id, out var one))
                one.Write(e.Timestamp.UtcDateTime, row);
            return;
        }

        foreach (var writer in _targets.Values)
            writer.Write(e.Timestamp.UtcDateTime, row);
    }

    public void Dispose()
    {
        foreach (var writer in _targets.Values)
            writer.Dispose();
    }
}
