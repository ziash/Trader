using Trader.Core.MarketData;
using Trader.Core.Notifications;
using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>News, Friday-close and connection pop-ups (tm.md §6).</summary>
public sealed class AlertsTests
{
    private static readonly TmConfig Config = new([new TmAccount("brokerA", 1, ["EURUSD"])]);

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private DateTime? _down;
    private List<string> _trades = ["brokerA EURUSD short P123"];

    private Alerts Make(params NewsRelease[] news) => new(Config, news, () => _down, () => _trades);

    [Fact]
    public void News_pop_up_10_minutes_before_once_with_the_open_trades()
    {
        // NFP 6-Nov-2026 08:30 New York = 13:30 UTC (US on winter time since 1 Nov)
        var alerts = Make(new NewsRelease(new DateTime(2026, 11, 6, 8, 30, 0), "NFP"));

        Assert.Empty(alerts.Due(Utc(2026, 11, 6, 13, 19, 59)));
        var a = Assert.Single(alerts.Due(Utc(2026, 11, 6, 13, 20)));
        Assert.Equal((NotificationLevel.Warning, "NewsSoon", "NFP in 10 min (08:30 New York)"), (a.Level, a.Event, a.Title));
        Assert.Contains("brokerA EURUSD short P123", a.Message);
        Assert.Empty(alerts.Due(Utc(2026, 11, 6, 13, 21)));   // once
        Assert.Empty(alerts.Due(Utc(2026, 11, 6, 13, 30)));   // not at or after the release
    }

    [Fact]
    public void News_pop_up_also_when_TM_starts_inside_the_lead_time()
    {
        _trades = [];
        var alerts = Make(new NewsRelease(new DateTime(2026, 11, 10, 8, 30, 0), "CPI"));
        var a = Assert.Single(alerts.Due(Utc(2026, 11, 10, 13, 26, 30)));
        Assert.Equal("CPI in 4 min (08:30 New York)", a.Title);
        Assert.Equal("No open TM trades.", a.Message);
    }

    [Theory]
    [InlineData(2026, 10, 30, 20, 50)] // New York on summer time (EDT): 17:00 = 21:00 UTC
    [InlineData(2026, 11, 6, 21, 50)]  // winter time (EST): 17:00 = 22:00 UTC
    public void Friday_close_pop_up_10_minutes_before_17_00_New_York(int y, int mo, int d, int h, int mi)
    {
        var alerts = Make();
        var at = Utc(y, mo, d, h, mi);
        Assert.Empty(alerts.Due(at.AddSeconds(-1)));
        var a = Assert.Single(alerts.Due(at));
        Assert.Equal((NotificationLevel.Warning, "WeekendSoon", "Friday close in 10 min (17:00 New York)"), (a.Level, a.Event, a.Title));
        Assert.Empty(alerts.Due(at.AddMinutes(5)));
        Assert.Empty(alerts.Due(at.AddMinutes(10)));
    }

    [Fact]
    public void No_Friday_pop_up_on_other_days()
    {
        var alerts = Make();
        Assert.Empty(alerts.Due(Utc(2026, 10, 29, 20, 55))); // Thursday 16:55 New York
    }

    [Fact]
    public void Disconnect_pop_up_after_60_s_and_one_when_back()
    {
        var alerts = Make();
        _down = Utc(2026, 10, 20, 9, 0);
        Assert.Empty(alerts.Due(Utc(2026, 10, 20, 9, 0, 59)));   // a normal reconnect takes a few seconds
        var a = Assert.Single(alerts.Due(Utc(2026, 10, 20, 9, 1)));
        Assert.Equal((NotificationLevel.Critical, "Disconnected"), (a.Level, a.Event));
        Assert.Empty(alerts.Due(Utc(2026, 10, 20, 9, 5)));       // once

        _down = null;
        var back = Assert.Single(alerts.Due(Utc(2026, 10, 20, 9, 6, 30)));
        Assert.Equal((NotificationLevel.Warning, "Reconnected"), (back.Level, back.Event));
        Assert.Contains("7 min", back.Message);
        Assert.Empty(alerts.Due(Utc(2026, 10, 20, 9, 7)));

        _down = Utc(2026, 10, 20, 10, 0);
        Assert.Single(alerts.Due(Utc(2026, 10, 20, 10, 1)));    // a second outage alerts again
        _down = null;
        Assert.Contains("(1 min)", Assert.Single(alerts.Due(Utc(2026, 10, 20, 10, 1))).Message);
    }

    [Fact]
    public void Short_outage_reported_in_seconds()
    {
        var alerts = new Alerts(Config with { DisconnectAlertSeconds = 3 }, [], () => _down, () => _trades);
        _down = Utc(2026, 10, 20, 9, 0);
        Assert.Single(alerts.Due(Utc(2026, 10, 20, 9, 0, 3)));
        _down = null;
        Assert.Contains("(6 s)", Assert.Single(alerts.Due(Utc(2026, 10, 20, 9, 0, 6))).Message);
    }

    [Fact]
    public void Short_drop_gives_no_pop_up()
    {
        var alerts = Make();
        _down = Utc(2026, 10, 20, 9, 0);
        Assert.Empty(alerts.Due(Utc(2026, 10, 20, 9, 0, 7)));
        _down = null;
        Assert.Empty(alerts.Due(Utc(2026, 10, 20, 9, 0, 8)));
    }
}
