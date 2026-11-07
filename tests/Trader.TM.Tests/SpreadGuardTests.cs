using Trader.Core.MarketData;
using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>
/// Spikes (tm.md §5.2, issue-log.md #2): hedges and TM's stops triggered on the other side of the price, the spread
/// guard on TM's own reactions, and the rollover window from the broker's trading hours.
/// </summary>
public sealed class SpreadGuardTests
{
    private const long Tenth = 1_000_000; // 0.1 lot in Open API units

    private static List<string> Events(IEnumerable<TmAction> actions) => actions.OfType<Notify>().Select(n => n.Event).ToList();

    // ── The 05-Nov-2026 GBPUSD trade (issue #2): short 1.34493, hedges at 1.34634 (1R = 14.1 pips) ─────────────────────
    private static FakeBroker GbpusdShort(string trigger)
    {
        var b = new FakeBroker { Config = new TmConfig([new TmAccount("brokerA", 1, ["EURUSD"])], StopTrigger: trigger) };
        b.MoveTo(1.34493);
        b.OpenManual(-1, Tenth, stopLoss: 1.34634);
        b.Run();
        Assert.Equal(1.34634, b.Order(TmLabel.HedgeMain)!.Price, 9);
        return b;
    }

    [Fact]
    public void Rollover_spike_as_on_05_Nov_without_the_guard_TM_closes_the_hedges_at_the_spike_price()
    {
        var b = GbpusdShort("Trade");                  // the broker's default trigger, as on 05-Nov
        b.Spread = 0.00160;                            // 22:01: ask 1.34644, bid 1.34484
        var spike = b.MoveTo(1.34484);

        Assert.Contains("HedgeFilled", Events(spike));
        Assert.Contains(spike, a => a is ClosePosition { Role: TmLabel.HedgeMain });   // closed at the bid, 16 pips below its fill
        Assert.Contains("LossWarning", Events(spike));
    }

    [Fact]
    public void Rollover_spike_with_the_guard_TM_waits_and_the_trade_carries_on_once_the_spread_is_back()
    {
        var b = GbpusdShort("Trade");                  // hedges still fill on the ask, as orders placed before §5.2 would
        b.Guarded = true;
        b.Spread = 0.00160;
        var spike = b.MoveTo(1.34484);

        Assert.Contains("HedgeFilled", Events(spike));
        Assert.DoesNotContain(spike, a => a is ClosePosition);
        Assert.DoesNotContain("LossWarning", Events(spike));
        Assert.DoesNotContain("LossLimit", Events(spike));
        Assert.Equal(4, b.Positions.Count);            // main, mirror and both hedges

        b.Guarded = false;                             // spread back to normal
        b.Spread = 0.00002;
        var after = b.MoveTo(1.34484);

        Assert.DoesNotContain("LossLimit", Events(after));
        Assert.Contains(after, a => a is ClosePosition { Role: TmLabel.HedgeMain }); // its stop was passed: closed, at a normal price now
        Assert.NotNull(b.Positions.SingleOrDefault(p => p.Label.Length == 0));       // the main carries on
        Assert.Equal(1.34634, b.Order(TmLabel.HedgeMain)!.Price, 9);                  // hedged again at H
        Assert.True(b.Book.Trades.Single().Main.Realized / b.Book.Trades.Single().Main.R > -1.2); // about −1.1R, not −3R
    }

    [Fact]
    public void With_the_trigger_on_the_other_side_a_spread_spike_fills_no_hedge_but_a_real_move_does()
    {
        var b = GbpusdShort("DoubleOpposite");
        Assert.Equal(ProtoOAOrderTriggerMethod.DoubleOpposite, b.Order(TmLabel.HedgeMain)!.Trigger);
        Assert.Equal(ProtoOAOrderTriggerMethod.DoubleOpposite, b.Positions.Single(p => p.Label.Length == 0).StopTrigger); // the backstop

        b.Spread = 0.00160;                            // ask 1.34644 past H, bid 1.34484 not
        b.MoveTo(1.34484);
        Assert.Equal(2, b.Positions.Count);            // no hedge filled
        Assert.NotNull(b.Order(TmLabel.HedgeMain));

        b.Spread = 0.00002;
        var move = b.MoveTo(1.34640);                  // the bid itself reaches H
        Assert.Contains("HedgeFilled", Events(move));
    }

    [Fact]
    public void Hedge_orders_and_TM_stops_the_broker_reports_with_another_trigger_are_set_again_once()
    {
        var b = GbpusdShort("DoubleOpposite");
        for (var i = 0; i < b.Orders.Count; i++)
            b.Orders[i] = b.Orders[i] with { Trigger = ProtoOAOrderTriggerMethod.Trade };          // placed before §5.2
        for (var i = 0; i < b.Positions.Count; i++)
            b.Positions[i] = b.Positions[i] with { StopTrigger = ProtoOAOrderTriggerMethod.Trade };

        var actions = b.Run();

        Assert.Equal(2, actions.OfType<MoveOrder>().Count(m => Math.Abs(m.Price - 1.34634) < 1e-9));
        Assert.Equal(2, actions.OfType<SetProtection>().Count(s => s.TmStop && s.StopLoss is not null));
        Assert.All(actions.OfType<MoveOrder>().Concat<TmAction>(actions.OfType<SetProtection>()), a => Assert.True(a.TriggerOnly)); // tried once, not retried if refused
        Assert.All(b.Orders, o => Assert.Equal(ProtoOAOrderTriggerMethod.DoubleOpposite, o.Trigger));
        Assert.DoesNotContain(b.Run(), a => a is not Notify);
    }

    [Fact]
    public void A_new_mirror_waits_while_guarded()
    {
        var b = new FakeBroker { Guarded = true };
        b.OpenManual(-1, Tenth, stopLoss: 1.16100);
        var actions = b.Run();
        Assert.DoesNotContain(actions, a => a is OpenMirror);
        Assert.Contains(actions, a => a is PlaceHedge { Price: not null }); // the hedge order at H protects meanwhile

        b.Guarded = false;
        Assert.Contains(b.Run(), a => a is OpenMirror);
    }

    // ── The guard itself ─────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly DateTime T0 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc); // a Tuesday morning
    private static readonly TmConfig Config = new([new TmAccount("brokerA", 1, ["EURUSD"])]);
    private static Quote Q(double spread, DateTime t) => new(1, 1, 1.16000, 1.16000 + spread, t);

    /// <summary>Feeds 5 minutes of normal prices (0.2 pip) so the usual spread is known; returns the time after.</summary>
    private static DateTime Warm(SpreadGuard g, TradingSchedule schedule, DateTime from)
    {
        var t = from;
        for (var i = 0; i < 30; i++, t = t.AddSeconds(10))
            Assert.Null(g.Update(1, Q(0.00002, t), schedule, t));
        return t;
    }

    [Fact]
    public void Guard_starts_above_5x_the_usual_spread_and_ends_when_it_is_back()
    {
        var g = new SpreadGuard(Config);
        Assert.Null(g.Usual(1));
        var t = Warm(g, TradingSchedule.AlwaysOpen, T0);
        Assert.Equal(0.00002, g.Usual(1)!.Value, 9);

        Assert.Null(g.Update(1, Q(0.00009, t), TradingSchedule.AlwaysOpen, t));         // 4.5×: not yet
        Assert.Equal("on", g.Update(1, Q(0.00016, t.AddSeconds(1)), TradingSchedule.AlwaysOpen, t.AddSeconds(1))?.Kind);
        Assert.True(g.IsGuarded(1));
        Assert.Equal("off", g.Update(1, Q(0.00002, t.AddSeconds(30)), TradingSchedule.AlwaysOpen, t.AddSeconds(30))?.Kind);
        Assert.False(g.IsGuarded(1));
    }

    [Fact]
    public void Guard_ends_after_15_minutes_even_if_the_spread_stays_wide()
    {
        var g = new SpreadGuard(Config);
        var t = Warm(g, TradingSchedule.AlwaysOpen, T0);
        Assert.Equal("on", g.Update(1, Q(0.00020, t), TradingSchedule.AlwaysOpen, t)?.Kind);
        Assert.True(g.IsGuarded(1));
        Assert.Null(g.Update(1, Q(0.00020, t.AddMinutes(14)), TradingSchedule.AlwaysOpen, t.AddMinutes(14)));
        Assert.Equal("long", g.Update(1, Q(0.00020, t.AddMinutes(15)), TradingSchedule.AlwaysOpen, t.AddMinutes(15))?.Kind);
        Assert.False(g.IsGuarded(1));
        Assert.Null(g.Update(1, Q(0.00020, t.AddMinutes(16)), TradingSchedule.AlwaysOpen, t.AddMinutes(16)));
    }

    // Forex-like hours in UTC with a daily break 22:00–22:05 (seconds from Sunday 00:00): Sunday 22:05 to Friday 22:00.
    private static readonly TradingSchedule Forex = new(
        Enumerable.Range(0, 5).Select(d => ((uint)(d * 86400 + 22 * 3600 + 300), (uint)((d + 1) * 86400 + 22 * 3600))), TimeZoneInfo.Utc);

    [Theory]
    [InlineData(21, 50, false)] // ten minutes before the break
    [InlineData(21, 56, true)]  // within 5 minutes before the close
    [InlineData(22, 15, true)]  // within 15 minutes after the reopening at 22:05
    [InlineData(22, 21, false)] // 16 minutes after
    public void Rollover_window_comes_from_the_brokers_trading_hours(int hour, int minute, bool guarded)
    {
        var g = new SpreadGuard(Config);
        var now = new DateTime(2026, 11, 10, hour, minute, 0, DateTimeKind.Utc);
        g.Update(1, Q(0.00002, now), Forex, now);
        Assert.Equal(guarded, g.IsGuarded(1));

        var crypto = new SpreadGuard(Config);
        crypto.Update(1, Q(0.00002, now), TradingSchedule.AlwaysOpen, now);
        Assert.False(crypto.IsGuarded(1));             // traded around the clock: no window
    }

    [Fact]
    public void Config_checks_the_trigger_and_the_guard_values()
    {
        var ok = new TmConfig([new TmAccount("brokerA", 1, ["EURUSD"])]).Validated();
        Assert.Equal((ProtoOAOrderTriggerMethod.DoubleOpposite, 5.0, 15, 5, 15),
            (ok.TriggerMethod, ok.SpreadGuardFactor, ok.SpreadGuardMaxMinutes, ok.RolloverBeforeMinutes, ok.RolloverAfterMinutes));
        Assert.Throws<TmConfigException>(() => (ok with { StopTrigger = "Bid" }).Validated());
        Assert.Throws<TmConfigException>(() => (ok with { StopTrigger = "1" }).Validated());
        Assert.Throws<TmConfigException>(() => (ok with { SpreadGuardFactor = 1 }).Validated());
    }
}
