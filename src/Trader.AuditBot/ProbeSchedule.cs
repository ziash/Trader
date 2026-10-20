using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.AuditBot;

/// <summary>One probe window: its log name (probes.csv "window" column) and when it starts.</summary>
public sealed record ProbeWindow(string Name, DateTime StartUtc, string? Detail = null);

/// <summary>A scheduled US news release, in New York time (BLS publishes 08:30 ET).</summary>
public sealed record NewsRelease(DateTime NewYorkTime, string Event);

/// <summary>
/// Probe windows of audit-bot.md §2.2, Monday–Friday in local market time (daylight saving handled by time-zone rules):
/// LondonOpen 08:00 London, NewYorkOpen 08:00 New York, QuietHour 03:00 London, and for each news release
/// three windows: 2 min before, 5 s after and 5 min after (NewsRelease-2m, NewsRelease+5s, NewsRelease+5m).
/// </summary>
public sealed class ProbeSchedule(IReadOnlyList<NewsRelease> news)
{
    public static readonly (string Suffix, TimeSpan Offset)[] NewsOffsets =
    [
        ("-2m", TimeSpan.FromMinutes(-2)),
        ("+5s", TimeSpan.FromSeconds(5)),
        ("+5m", TimeSpan.FromMinutes(5)),
    ];

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public IReadOnlyList<NewsRelease> News { get; } = news;

    /// <summary>The first window starting strictly after <paramref name="afterUtc"/>; null if none within 8 days.</summary>
    public ProbeWindow? Next(DateTime afterUtc)
    {
        var first = DateOnly.FromDateTime(afterUtc).AddDays(-1);
        return Enumerable.Range(0, 10)
            .SelectMany(i => WindowsOn(first.AddDays(i)))
            .Where(w => w.StartUtc > afterUtc)
            .MinBy(w => w.StartUtc);
    }

    /// <summary>All windows whose local date is <paramref name="date"/>.</summary>
    public IEnumerable<ProbeWindow> WindowsOn(DateOnly date)
    {
        if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        {
            yield return new ProbeWindow("QuietHour", Utc(date, 3, 0, London));
            yield return new ProbeWindow("LondonOpen", Utc(date, 8, 0, London));
            yield return new ProbeWindow("NewYorkOpen", Utc(date, 8, 0, NewYork));
        }

        foreach (var release in News.Where(n => DateOnly.FromDateTime(n.NewYorkTime) == date))
        {
            var at = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(release.NewYorkTime, DateTimeKind.Unspecified), NewYork);
            foreach (var (suffix, offset) in NewsOffsets)
                yield return new ProbeWindow("NewsRelease" + suffix, at + offset, release.Event);
        }
    }

    private static DateTime Utc(DateOnly date, int hour, int minute, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), zone);

    // ---- news-releases.json -------------------------------------------------------------------------

    private sealed record NewsFile(IReadOnlyList<NewsEntry> Releases);
    private sealed record NewsEntry(string Time, string Event);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Reads ~/tradebot/config/news-releases.json; a missing file means no news windows.
    /// Format: { "releases": [ { "time": "2026-11-06 08:30", "event": "NFP" }, … ] } — times in New York local time.
    /// </summary>
    public static IReadOnlyList<NewsRelease> LoadNews(string path)
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
            throw new AuditConfigException($"{Path.GetFileName(path)}: {ex.Message}");
        }

        var result = new List<NewsRelease>();
        foreach (var entry in file?.Releases ?? [])
        {
            if (!DateTime.TryParseExact(entry.Time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new AuditConfigException($"{Path.GetFileName(path)}: time \"{entry.Time}\" must look like 2026-11-06 08:30 (New York time)");
            if (string.IsNullOrWhiteSpace(entry.Event))
                throw new AuditConfigException($"{Path.GetFileName(path)}: every release needs an \"event\" name");
            result.Add(new NewsRelease(time, entry.Event));
        }
        return result.OrderBy(r => r.NewYorkTime).ToList();
    }
}
