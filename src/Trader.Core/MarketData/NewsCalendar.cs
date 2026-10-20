using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.Core.MarketData;

/// <summary>A scheduled US news release, in New York time (BLS publishes 08:30 ET).</summary>
public sealed record NewsRelease(DateTime NewYorkTime, string Event)
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>The release time in UTC (daylight saving handled by time-zone rules).</summary>
    public DateTime Utc => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(NewYorkTime, DateTimeKind.Unspecified), NewYork);
}

/// <summary>A problem in news-releases.json.</summary>
public sealed class NewsCalendarException(string message) : Exception(message);

/// <summary>
/// ~/tradebot/config/news-releases.json, shared by the bots that care about news (Audit Bot probe windows,
/// TM pop-ups). Format: { "releases": [ { "time": "2026-11-06 08:30", "event": "NFP" }, … ] } — New York local time.
/// </summary>
public static class NewsCalendar
{
    public const string FileName = "news-releases.json";

    private sealed record NewsFile(IReadOnlyList<NewsEntry> Releases);
    private sealed record NewsEntry(string Time, string Event);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The releases sorted by time; a missing file means no releases.</summary>
    public static IReadOnlyList<NewsRelease> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        NewsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<NewsFile>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new NewsCalendarException($"{Path.GetFileName(path)}: {ex.Message}");
        }

        var result = new List<NewsRelease>();
        foreach (var entry in file?.Releases ?? [])
        {
            if (!DateTime.TryParseExact(entry.Time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new NewsCalendarException($"{Path.GetFileName(path)}: time \"{entry.Time}\" must look like 2026-11-06 08:30 (New York time)");
            if (string.IsNullOrWhiteSpace(entry.Event))
                throw new NewsCalendarException($"{Path.GetFileName(path)}: every release needs an \"event\" name");
            result.Add(new NewsRelease(time, entry.Event));
        }
        return result.OrderBy(r => r.NewYorkTime).ToList();
    }
}
