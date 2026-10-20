using System.Collections.Concurrent;
using System.Threading.Channels;
using Serilog;
using Serilog.Events;
using Trader.Core.Logging;

namespace Trader.Core.Notifications;

/// <summary>
/// The one place a bot sends messages to the owner (open-code-pieces.md §7):
///
///     bot.Notify.Warning("LossWarning", "AUDJPY short at -2R", "Trade P123456789, leg MIR", account);
///
/// Every notification is logged at once (journal and events.csv, event name = <c>Event</c>), then delivered in the
/// background to each channel whose filter in notifications.json accepts it, so a slow or broken channel never
/// holds up trading. The same notification (level, event, title, message) again within <c>repeatSeconds</c> is
/// logged but not sent again. A failing channel is logged once until it works again.
/// </summary>
public sealed class Notifier : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly string _botName;
    private readonly IReadOnlyList<(ChannelConfig Config, INotificationChannel Channel)> _channels;
    private readonly TimeSpan _repeat;
    private readonly Func<DateTime> _utcNow;
    private readonly Channel<Notification> _queue = Channel.CreateUnbounded<Notification>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<(NotificationLevel, string, string, string), DateTime> _lastSent = new();
    private readonly ConcurrentDictionary<string, bool> _failing = new();
    private readonly Task _worker;

    public Notifier(ILogger log, string botName, NotificationConfig config,
        Func<ChannelConfig, INotificationChannel>? createChannel = null, Func<DateTime>? utcNow = null)
    {
        _log = log;
        _botName = botName;
        _repeat = TimeSpan.FromSeconds(config.RepeatSeconds);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        var create = createChannel ?? NotificationChannels.Create;
        _channels = (config.Channels ?? NotificationConfig.Default.Channels!).Where(c => c.Enabled).Select(c => (c, create(c))).ToList();
        _worker = Task.Run(DeliverAsync);
    }

    /// <summary>For bots: the notifier with ~/tradebot/config/notifications.json (or the defaults).</summary>
    public static Notifier FromConfigDir(ILogger log, string botName, string configDir) =>
        new(log, botName, NotificationConfig.Load(Path.Combine(configDir, NotificationConfig.FileName)));

    /// <summary>Channel names in use, for the start-up log line.</summary>
    public IReadOnlyList<string> ChannelNames => _channels.Select(c => $"{c.Channel.Name} (from {c.Config.MinLevel})").ToList();

    public void Info(string @event, string title, string message = "", long? accountId = null) =>
        Send(NotificationLevel.Info, @event, title, message, accountId);

    public void Warning(string @event, string title, string message = "", long? accountId = null) =>
        Send(NotificationLevel.Warning, @event, title, message, accountId);

    public void Critical(string @event, string title, string message = "", long? accountId = null) =>
        Send(NotificationLevel.Critical, @event, title, message, accountId);

    public void Send(NotificationLevel level, string @event, string title, string message = "", long? accountId = null)
    {
        var n = new Notification(_utcNow(), level, _botName, @event, title, message, accountId);

        var log = _log.Event(@event);
        if (accountId is { } ctid)
            log = log.ForAccount(ctid);
        var logLevel = level switch
        {
            NotificationLevel.Critical => LogEventLevel.Error,
            NotificationLevel.Warning => LogEventLevel.Warning,
            _ => LogEventLevel.Information,
        };
        if (message.Length > 0)
            log.Write(logLevel, "{Title}: {Message}", title, message);
        else
            log.Write(logLevel, "{Title}", title);

        var key = (level, @event, title, message);
        if (_lastSent.TryGetValue(key, out var last) && n.TimeUtc - last < _repeat)
            return;
        _lastSent[key] = n.TimeUtc;
        _queue.Writer.TryWrite(n);
    }

    private async Task DeliverAsync()
    {
        await foreach (var n in _queue.Reader.ReadAllAsync())
        {
            foreach (var (config, channel) in _channels)
            {
                if (!config.Accepts(n))
                    continue;
                try
                {
                    await channel.SendAsync(n, CancellationToken.None);
                    if (_failing.TryRemove(channel.Name, out _))
                        _log.Event("NotifyChannelOk").Information("Notification channel {Channel} works again", channel.Name);
                }
                catch (Exception ex)
                {
                    if (_failing.TryAdd(channel.Name, true))
                        _log.Event("NotifyChannelFailed").Warning("Notification channel {Channel} failed ({Message}); messages are still logged",
                            channel.Name, ex.Message);
                }
            }
        }
    }

    /// <summary>Delivers what is still queued (at most 15 s), then stops. Call on shut-down so "stopped" messages arrive.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAny(_worker, Task.Delay(TimeSpan.FromSeconds(15)));
    }
}
