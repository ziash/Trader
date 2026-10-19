using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.Core.Notifications;

/// <summary>
/// ~/tradebot/config/notifications.json — which notifications go to which channel, for every bot. Example:
/// <code>
/// {
///   "repeatSeconds": 60,                  // same notification again within this time: logged, not sent again
///   "channels": [
///     { "type": "desktop", "minLevel": "Warning" },
///     { "type": "desktop", "minLevel": "Info", "bots": ["TM"], "events": ["TradeTakenOver"] }
///   ]
/// }
/// </code>
/// A missing file means: desktop pop-ups for Warning and Critical from every bot. Logs always get everything.
/// </summary>
public sealed record NotificationConfig(int RepeatSeconds = 60, IReadOnlyList<ChannelConfig>? Channels = null)
{
    public const string FileName = "notifications.json";

    public static readonly NotificationConfig Default = new(60, [new ChannelConfig("desktop")]);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // catch typos in field names
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads the file; a missing file gives <see cref="Default"/>. Throws <see cref="NotificationConfigException"/> on a broken one.</summary>
    public static NotificationConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default;
        NotificationConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<NotificationConfig>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new NotificationConfigException($"{Path.GetFileName(path)}: {ex.Message}");
        }
        return (config ?? Default).Validated(Path.GetFileName(path));
    }

    internal NotificationConfig Validated(string file)
    {
        if (RepeatSeconds < 0)
            throw new NotificationConfigException($"{file}: \"repeatSeconds\" must be 0 or more");
        foreach (var c in Channels ?? [])
            if (!NotificationChannels.Known.Contains(c.Type ?? "", StringComparer.OrdinalIgnoreCase))
                throw new NotificationConfigException($"{file}: channel type \"{c.Type}\" unknown; known: {string.Join(", ", NotificationChannels.Known)}");
        return this;
    }
}

/// <summary>One channel and its filter. All filters must match for a notification to be sent.</summary>
/// <param name="Type">Channel kind, see <see cref="NotificationChannels.Known"/>.</param>
/// <param name="MinLevel">Lowest level sent (default Warning).</param>
/// <param name="Bots">Only these bots (default: all).</param>
/// <param name="Events">Only these events (default: all).</param>
/// <param name="ExcludeEvents">Never these events.</param>
/// <param name="Enabled">false switches the channel off without deleting it.</param>
public sealed record ChannelConfig(
    string Type,
    NotificationLevel MinLevel = NotificationLevel.Warning,
    IReadOnlyList<string>? Bots = null,
    IReadOnlyList<string>? Events = null,
    IReadOnlyList<string>? ExcludeEvents = null,
    bool Enabled = true)
{
    public bool Accepts(Notification n) =>
        Enabled &&
        n.Level >= MinLevel &&
        (Bots is null || Bots.Contains(n.BotName, StringComparer.OrdinalIgnoreCase)) &&
        (Events is null || Events.Contains(n.Event, StringComparer.OrdinalIgnoreCase)) &&
        (ExcludeEvents is null || !ExcludeEvents.Contains(n.Event, StringComparer.OrdinalIgnoreCase));
}

/// <summary>The channel kinds this build knows. Adding one (e.g. "telegram") = a new <see cref="INotificationChannel"/> class + a line here.</summary>
public static class NotificationChannels
{
    public static readonly string[] Known = ["desktop"];

    public static INotificationChannel Create(ChannelConfig config) => config.Type.ToLowerInvariant() switch
    {
        "desktop" => new DesktopChannel(),
        _ => throw new NotificationConfigException($"channel type \"{config.Type}\" unknown"),
    };
}

public sealed class NotificationConfigException(string message) : Exception(message);
