namespace Trader.Core.MarketData;

/// <summary>
/// When a symbol can trade, from ProtoOASymbol.Schedule: intervals in seconds from Sunday 00:00 in the
/// symbol's time zone (both brokers use America/New_York). Forex, for example, pauses daily around 17:00 New York.
/// An empty schedule is treated as always open.
/// </summary>
public sealed class TradingSchedule
{
    private const int SecondsPerWeek = 7 * 24 * 3600;
    private static readonly TimeSpan CheckStep = TimeSpan.FromSeconds(10);

    private readonly (uint Start, uint End)[] _intervals;
    private readonly TimeZoneInfo _zone;

    public TradingSchedule(IEnumerable<(uint Start, uint End)> intervals, TimeZoneInfo zone)
    {
        _intervals = intervals.ToArray();
        _zone = zone;
    }

    public static TradingSchedule AlwaysOpen { get; } = new([], TimeZoneInfo.Utc);

    public static TradingSchedule From(ProtoOASymbol symbol)
    {
        var zone = string.IsNullOrEmpty(symbol.ScheduleTimeZone)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(symbol.ScheduleTimeZone);
        return new TradingSchedule(symbol.Schedule.Select(i => (i.StartSecond, i.EndSecond)), zone);
    }

    public bool IsOpen(DateTime utc)
    {
        if (_intervals.Length == 0)
            return true;

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);
        var second = (uint)(((int)local.DayOfWeek * 86400 + (int)local.TimeOfDay.TotalSeconds) % SecondsPerWeek);
        foreach (var (start, end) in _intervals)
            if (second >= start && second < end)
                return true;
        return false;
    }

    /// <summary>True if the market is open during all of [start, end), checked every 10 s (enough for 2–5 minute intervals).</summary>
    public bool IsOpenThroughout(DateTime startUtc, DateTime endUtc)
    {
        for (var t = startUtc; t < endUtc; t += CheckStep)
            if (!IsOpen(t))
                return false;
        return IsOpen(endUtc.AddTicks(-1));
    }
}
