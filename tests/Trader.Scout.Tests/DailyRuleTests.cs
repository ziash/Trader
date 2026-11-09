using System.Text.Json;
using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>The daily-direction rule (scout.md §2.10): direction first; when it is unknown, the daily zones decide; never a look ahead.</summary>
public sealed class DailyRuleTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DailySettings On = new();
    private static readonly IReadOnlyList<DailyZone> Zone = [new DailyZone(110, 112, 2)];
    private const double Atr = 10; // near = within 5

    private static Candle Day(int i, double o, double h, double l, double c) => new(T0.AddDays(i), o, h, l, c);

    [Theory]
    [InlineData(1, 1, null)]
    [InlineData(-1, -1, null)]
    [InlineData(1, -1, DailyRule.AgainstDaily)]
    [InlineData(-1, 1, DailyRule.AgainstDaily)]
    public void With_a_known_direction_only_that_side_trades_and_zones_do_not_matter(int dir, int trend, string? expected)
    {
        Assert.Equal(expected, DailyRule.Hold(dir, 108, trend, Zone, Atr, On));
    }

    [Theory]
    [InlineData(1, 104.0, null)]                          // a buy 6 below the zone: not near
    [InlineData(1, 105.0, DailyRule.NearResistance)]      // exactly 5 below: near
    [InlineData(1, 108.0, DailyRule.NearResistance)]
    [InlineData(1, 111.0, DailyRule.NearResistance)]      // inside the zone
    [InlineData(1, 113.0, null)]                          // beyond the top: the zone is cleared, the owner's entry just past a level
    [InlineData(-1, 118.0, null)]                         // a sell 6 above the zone: not near
    [InlineData(-1, 117.0, DailyRule.NearSupport)]        // exactly 5 above: near
    [InlineData(-1, 111.0, DailyRule.NearSupport)]        // inside
    [InlineData(-1, 109.0, null)]                         // below the bottom: cleared
    public void With_the_direction_unknown_no_buy_near_resistance_and_no_sell_near_support(int dir, double level, string? expected)
    {
        Assert.Equal(expected, DailyRule.Hold(dir, level, 0, Zone, Atr, On));
    }

    [Fact]
    public void With_the_direction_unknown_and_no_zones_or_no_atr_the_order_goes_on()
    {
        Assert.Null(DailyRule.Hold(1, 108, 0, [], Atr, On));
        Assert.Null(DailyRule.Hold(1, 108, 0, Zone, null, On));
    }

    /// <summary>Flat candles at 100 with spikes up to the highs given at the days given.</summary>
    private static List<Candle> Spikes(int count, params (int Day, double High)[] spikes) =>
        Enumerable.Range(0, count).Select(i => Day(i, 100, spikes.Where(s => s.Day == i).Select(s => s.High).DefaultIfEmpty(101).First(), 99, 100)).ToList();

    [Fact]
    public void Two_swing_highs_close_together_make_a_zone_and_one_alone_does_not()
    {
        var zone = Assert.Single(DailyRule.Zones(Spikes(30, (10, 120), (20, 121)), Atr, On), z => z.Low >= 110);
        Assert.Equal((120.0, 121.0, 2), (zone.Low, zone.High, zone.Touches));

        Assert.DoesNotContain(DailyRule.Zones(Spikes(30, (10, 120)), Atr, On), z => z.Low >= 110);       // held once only
        Assert.DoesNotContain(DailyRule.Zones(Spikes(30, (10, 120), (20, 130)), Atr, On), z => z.Low >= 110); // 10 apart: wider than 0.25 ATR
    }

    [Fact]
    public void A_swing_needs_its_later_candles_closed_so_the_newest_candles_cannot_make_one()
    {
        Assert.DoesNotContain(DailyRule.Zones(Spikes(30, (10, 120), (27, 121)), Atr, On), z => z.Low >= 110); // day 27 of 30 has only 2 candles after it
        Assert.Contains(DailyRule.Zones(Spikes(31, (10, 120), (27, 121)), Atr, On), z => z.Low >= 110);       // one more day closed
    }

    [Fact]
    public void Candles_older_than_the_window_do_not_count()
    {
        var s = new DailySettings { ZoneCandles = 15 };
        Assert.DoesNotContain(DailyRule.Zones(Spikes(30, (10, 120), (24, 121)), Atr, s), z => z.Low >= 110);
    }

    [Theory]
    [InlineData("H1", true, true)]
    [InlineData("H4", false, true)]  // has the daily chart as its higher timeframe
    [InlineData("D1", false, false)] // the rule is for charts below the daily
    public void The_rule_applies_to_1H_and_4H_charts(string timeframe, bool readsDaily, bool hasDaily)
    {
        var t = new ChartTracker(new ScoutChart("EURUSD", timeframe, ScoutMode.Trade), new DetectorSettings());
        Assert.Equal((readsDaily, hasDaily), (t.ReadsDaily, t.Daily is not null));
    }

    [Fact]
    public void A_daily_candle_counts_only_once_it_has_closed()
    {
        var t = new ChartTracker(new ScoutChart("EURUSD", "H1", ScoutMode.Trade), new DetectorSettings());
        var day = Day(0, 100, 101, 99, 100);
        t.Feed([], [], T0.AddHours(23), [day]);
        Assert.Equal(0, t.Daily!.Count);
        t.Feed([], [], T0.AddHours(24), [day]);
        Assert.Equal(1, t.Daily!.Count);
    }

    // ── The replay ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<Candle> H1(params (double O, double H, double L, double C)[] pause)
    {
        var bars = new List<(double O, double H, double L, double C)>();
        for (var i = 0; i < 16; i++)
            bars.Add((100, 105, 95, 100));
        bars.AddRange([(100, 121, 99, 120), (120, 141, 119, 140), (140, 161, 139, 160)]);
        bars.AddRange(pause);
        return bars.Select((b, i) => new Candle(T0.AddHours(i), b.O, b.H, b.L, b.C)).ToList();
    }

    private static readonly (double, double, double, double)[] Pause =
        [(160, 163, 155, 158), (158, 162, 154, 157), (157, 162, 155, 158), (158, 162, 155, 158), (158, 162, 155, 158), (158, 162, 155, 158), (158, 162, 155, 158)];

    /// <summary>16 flat days ending at T0, then one strong day, up or down, that closes <paramref name="closesAfter"/> after T0.</summary>
    private static List<Candle> Daily(double strongClose, TimeSpan closesAfter)
    {
        var days = Enumerable.Range(0, 16).Select(i => new Candle(T0.AddDays(i - 17).Add(closesAfter), 100, 105, 95, 100)).ToList();
        days.Add(new Candle(T0.AddDays(-1).Add(closesAfter), 100, Math.Max(101, strongClose + 1), Math.Min(99, strongClose - 1), strongClose));
        return days;
    }

    private static List<ReplayOrder> Replay(List<Candle> daily, DailySettings? rule, params (double, double, double, double)[] pause) =>
        ReplayOrders.Run("EURUSD", "H1", H1(pause), [], new DetectorSettings(), 16, daily, rule);

    [Fact]
    public void In_the_replay_a_buy_against_a_falling_daily_chart_is_not_placed()
    {
        Assert.DoesNotContain(Replay(Daily(90, TimeSpan.Zero), On, Pause), o => o.Action == "place" && o.Dir == 1);
    }

    [Fact]
    public void In_the_replay_a_buy_with_a_rising_daily_chart_is_placed_and_labelled()
    {
        var place = Assert.Single(Replay(Daily(110, TimeSpan.Zero), On, Pause), o => o is { Action: "place", Reversal: false });
        Assert.Equal((1, 1), (place.Dir, place.DailyTrend));
    }

    [Fact]
    public void With_the_rule_off_the_replay_keeps_every_order_and_labels_it()
    {
        var place = Assert.Single(Replay(Daily(90, TimeSpan.Zero), new DailySettings { Enabled = false }, Pause), o => o is { Action: "place", Reversal: false });
        Assert.Equal((1, -1), (place.Dir, place.DailyTrend));
    }

    [Fact]
    public void Without_daily_candles_the_replay_is_as_before()
    {
        Assert.Single(ReplayOrders.Run("EURUSD", "H1", H1(Pause), [], new DetectorSettings(), 16), o => o is { Action: "place", Reversal: false });
    }

    [Fact]
    public void In_the_replay_a_waiting_order_is_cancelled_when_the_daily_direction_turns_against_it()
    {
        var orders = Replay(Daily(90, TimeSpan.FromHours(24)), On, Pause);
        var place = Assert.Single(orders, o => o is { Action: "place", Reversal: false });
        Assert.Equal((1, 0), (place.Dir, place.DailyTrend));                    // unknown until the strong day closes
        var cancel = Assert.Single(orders, o => o.Action == "cancel");
        Assert.Equal((DailyRule.AgainstDaily, T0.AddHours(24)), (cancel.Reason, cancel.TimeUtc));
    }

    // ── The switch ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly ScoutAccount Account = new("brokerA", 49123893, [new ScoutChart("EURUSD", "H1", ScoutMode.Trade)]);
    private static readonly ControlCatalogue Catalogue = new(2, [new("dailyDirection", "scout", "switch", null)]);
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static ControlFile File(string json) => JsonSerializer.Deserialize<ControlFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void The_rule_is_on_by_default()
    {
        Assert.True(new DailySettings().Enabled);
        Assert.True(new ScoutConfig([Account]).DailyInForce.Enabled);
        Assert.Null(Controls.None.DailyDirection);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void The_dashboard_can_set_the_switch_for_the_account(string value, bool expected)
    {
        var c = Controls.Evaluate(File($$"""{ "version": 1, "account": "brokerA-49123893", "scout": { "dailyDirection": {{value}} } }"""), Catalogue, Account, Now);
        Assert.Equal(expected, c.DailyDirection);
        var e = Assert.Single(c.Entries);
        Assert.Equal(("scout", "all", "dailyDirection", value, true), (e.Scope, e.Target, e.Key, e.Value, e.Applied));
    }

    [Theory]
    [InlineData("""{ "version": 1, "account": "brokerA-49123893", "scout": { "dailyDirection": "off" } }""", "on / off expected")]
    [InlineData("""{ "version": 1, "account": "brokerB-49124666", "scout": { "dailyDirection": false } }""", "the file is for brokerB-49124666, not brokerA-49123893")]
    [InlineData("""{ "version": 1, "account": "brokerA-49123893", "scout": { "somethingElse": false } }""", "scout option \"somethingElse\" is not in the catalogue")]
    public void A_bad_entry_is_refused_and_the_setting_stays_as_configured(string json, string why)
    {
        var c = Controls.Evaluate(File(json), Catalogue, Account, Now);
        Assert.Null(c.DailyDirection);
        var e = Assert.Single(c.Entries);
        Assert.Equal((false, why), (e.Applied, e.Reason));
    }

    [Fact]
    public void Scout_json_values_are_checked()
    {
        var bad = new ScoutConfig([Account], Daily: new DailySettings { ZoneSwing = 0 });
        Assert.Throws<ScoutConfigException>(() => bad.Validated());
    }
}
