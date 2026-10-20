using Trader.Core.MarketData;

namespace Trader.AuditBot;

/// <summary>One probe window: its log name (probes.csv "window" column) and when it starts.</summary>
public sealed record ProbeWindow(string Name, DateTime StartUtc, string? Detail = null);

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
            var at = release.Utc;
            foreach (var (suffix, offset) in NewsOffsets)
                yield return new ProbeWindow("NewsRelease" + suffix, at + offset, release.Event);
        }
    }

    private static DateTime Utc(DateOnly date, int hour, int minute, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), zone);

    /// <summary>Reads ~/tradebot/config/news-releases.json (<see cref="NewsCalendar"/>); a missing file means no news windows.</summary>
    public static IReadOnlyList<NewsRelease> LoadNews(string path)
    {
        try
        {
            return NewsCalendar.Load(path);
        }
        catch (NewsCalendarException ex)
        {
            throw new AuditConfigException(ex.Message);
        }
    }
}
