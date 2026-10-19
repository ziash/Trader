namespace Trader.Core.Notifications;

/// <summary>How urgent a notification is. Channels choose a minimum level in notifications.json.</summary>
public enum NotificationLevel
{
    /// <summary>Good to know (e.g. a trade taken over). Logged; pop-ups only if a channel asks for Info.</summary>
    Info,

    /// <summary>Needs a look (e.g. 2R loss warning, mirror skipped, news in 10 min).</summary>
    Warning,

    /// <summary>Needs action or something stopped (e.g. 3R close, bot stopped, connection down). Desktop: stays on screen.</summary>
    Critical,
}

/// <summary>One message to the owner.</summary>
/// <param name="Event">Short machine name, also the <c>event</c> column of events.csv, e.g. "LossLimit".</param>
/// <param name="Title">One line, e.g. "AUDJPY short closed at 3R".</param>
/// <param name="Message">Detail; may be several lines.</param>
/// <param name="AccountId">ctidTraderAccountId it concerns, if any (routes the log line to that account's events.csv).</param>
public sealed record Notification(
    DateTime TimeUtc,
    NotificationLevel Level,
    string BotName,
    string Event,
    string Title,
    string Message,
    long? AccountId = null);

/// <summary>
/// A way of reaching the owner: desktop pop-up, later Telegram, e-mail… To add one, implement this interface
/// and add a line to <see cref="NotificationChannels.Create"/>; no bot code changes.
/// </summary>
public interface INotificationChannel
{
    /// <summary>For log lines, e.g. "desktop".</summary>
    string Name { get; }

    /// <summary>Delivers one notification; throws on failure (the notifier logs it and carries on).</summary>
    Task SendAsync(Notification notification, CancellationToken ct);
}
