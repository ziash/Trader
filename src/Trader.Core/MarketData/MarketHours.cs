namespace Trader.Core.MarketData;

/// <summary>
/// Main hours vs off-peak (audit-bot.md §2.3): main = Mon–Fri from 08:00 London to 17:00 New York, on the same
/// calendar day in both cities; everything else is off-peak. Local times use time-zone rules, so the
/// UK (25-Oct-2026) and US (1-Nov-2026) clock changes are handled automatically.
/// </summary>
public static class MarketHours
{
    public static readonly TimeSpan MainInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan OffPeakInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static bool IsMainHours(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var london = TimeZoneInfo.ConvertTimeFromUtc(utc, London);
        var ny = TimeZoneInfo.ConvertTimeFromUtc(utc, NewYork);
        return london.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
               && london.Date == ny.Date
               && london.TimeOfDay >= TimeSpan.FromHours(8)
               && ny.TimeOfDay < TimeSpan.FromHours(17);
    }

    /// <summary>Log interval for the period that contains <paramref name="utc"/>: 2 min (main) or 5 min (off-peak).</summary>
    public static TimeSpan IntervalAt(DateTime utc) => IsMainHours(utc) ? MainInterval : OffPeakInterval;

    /// <summary>
    /// End of the interval that starts at or contains <paramref name="utc"/>: the next multiple of the current
    /// interval length on the UTC clock (e.g. 08:02, 08:04 … or 21:05, 21:10 …). Main/off-peak only switch on
    /// whole hours, which are multiples of both lengths, so intervals never straddle a switch.
    /// </summary>
    public static DateTime NextBoundary(DateTime utc)
    {
        var step = IntervalAt(utc).Ticks;
        return new DateTime((utc.Ticks / step + 1) * step, DateTimeKind.Utc);
    }
}
