using Trader.Core.MarketData;
using Trader.Core.OpenApi;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>When Scout asks for candles (scout.md §2): market hours, late-candle backoff, charts spread out.</summary>
public sealed class CandleTimingTests
{
    private static readonly TimeSpan H1 = TimeSpan.FromHours(1);
    private static readonly TimeSpan M30 = TimeSpan.FromMinutes(30);

    // Forex-like hours in UTC: open Sunday 22:00 to Friday 22:00 (seconds from Sunday 00:00).
    private static readonly TradingSchedule Forex = new([(22 * 3600, 5 * 86400 + 22 * 3600)], TimeZoneInfo.Utc);

    private static DateTime Utc(int day, int hour, int minute = 0, int second = 0) => new(2026, 11, day, hour, minute, second, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(18)] // the largest offset a chart gets
    public void In_a_replay_the_turn_at_a_candles_close_is_already_past_the_time_scout_asks_for_it(int offset)
    {
        var close = Utc(11, 12);
        Assert.True(close + LockStep.CandleReadDelay >= AccountScout.CandleDue(Utc(11, 10), H1, Forex, TimeSpan.FromSeconds(offset)));
    }

    [Fact]
    public void Next_candle_is_asked_for_a_few_seconds_after_it_closes_plus_the_charts_offset()
    {
        // Wednesday 11-Nov: the last candle opened 10:00, so the 11:00 candle closes at 12:00.
        Assert.Equal(Utc(11, 12, 0, 5), AccountScout.CandleDue(Utc(11, 10), H1, Forex, TimeSpan.Zero));
        Assert.Equal(Utc(11, 12, 0, 11), AccountScout.CandleDue(Utc(11, 10), H1, Forex, TimeSpan.FromSeconds(6)));
        Assert.Equal(Utc(11, 12, 0, 5), AccountScout.NextFetch(Utc(11, 10), H1, Forex, 0, TimeSpan.Zero, Utc(11, 11, 0, 7)));
    }

    [Fact]
    public void A_closed_market_costs_no_requests_until_the_first_candle_after_it_opens()
    {
        // Friday 13-Nov 21:00 is the last candle before the close at 22:00; the market opens again Sunday 15-Nov 22:00.
        var due = AccountScout.CandleDue(Utc(13, 21), H1, Forex, TimeSpan.Zero);
        Assert.Equal(Utc(15, 23, 0, 5), due);
        Assert.Equal(due, AccountScout.NextFetch(Utc(13, 21), H1, Forex, 0, TimeSpan.Zero, Utc(14, 12)));
        // Always open (crypto, an empty schedule): the next hour as usual.
        Assert.Equal(Utc(13, 23, 0, 5), AccountScout.CandleDue(Utc(13, 21), H1, TradingSchedule.AlwaysOpen, TimeSpan.Zero));
    }

    [Fact]
    public void A_late_candle_is_asked_for_again_after_1_2_4_8_minutes_then_once_per_candle()
    {
        var now = Utc(11, 12, 0, 10);   // the 11:00 candle should be in, it is not
        TimeSpan Wait(int misses, TimeSpan length) => AccountScout.NextFetch(Utc(11, 10), length, Forex, misses, TimeSpan.Zero, now) - now;

        Assert.Equal([1, 2, 4, 8, 60, 60], new[] { 1, 2, 3, 4, 5, 9 }.Select(m => Wait(m, H1).TotalMinutes));
        Assert.Equal(30, Wait(5, M30).TotalMinutes);
        Assert.Equal(8, Wait(5, TimeSpan.FromMinutes(5)).TotalMinutes);   // never more often than every 8 minutes
    }
}
