using Trader.Core.Notifications;
using Trader.Core.OpenApi;
using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>
/// TM's margin rules (tm.md §5.1, issue-log.md #1): backstop stop-losses, a hedge refused for lack of money, no mirrors
/// when margin is short, and the margin rescue. EURUSD, 0.5 lot short at 1.16000, H 1.16100 (1R = 10 pips).
/// </summary>
public sealed class MarginTests
{
    private const long Half = 5_000_000;

    private static List<string> Events(IEnumerable<TmAction> actions) => actions.OfType<Notify>().Select(n => n.Event).ToList();

    private static AccountStatus Level(FakeBroker b, double level) => new(1, b.Now, 1000, 1000, 1000 / level * 100, 4, 2);

    [Fact]
    public void A_triggered_hedge_the_broker_refused_leaves_the_backstop_and_past_H_TM_closes_the_legs_instead_of_retrying()
    {
        var b = new FakeBroker();
        var main = b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();
        Assert.Equal(1.16300, b.Positions.Single(p => p.Id == main.Id).StopLoss!.Value, 9); // backstop while the hedge waits at H

        // ETHUSD on 06-Nov-2026: the hedge stops trigger but the broker refuses them (no money), so they are just gone.
        b.Orders.Clear();
        b.Refuse.Add(nameof(PlaceHedge));
        b.RefuseCode = Planner.NotEnoughMoney;
        var actions = b.MoveTo(1.16150);                      // past H, no hedge

        Assert.Equal(2, actions.OfType<PlaceHedge>().Count()); // one market hedge per leg, refused …
        Assert.Equal(2, Events(actions).Count(e => e == "HedgeNoMoney")); // … then each leg closed, not retried every second
        Assert.Empty(b.Positions);
        Assert.Equal(NotificationLevel.Critical, actions.OfType<Notify>().First(n => n.Event == "HedgeNoMoney").Level);
    }

    [Fact]
    public void A_stop_hedge_refused_for_money_waits_a_minute_with_the_backstop_in_place()
    {
        var b = new FakeBroker();
        b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();
        b.Orders.Clear();                                      // both hedge orders gone (refused when they triggered)
        b.Refuse.Add(nameof(PlaceHedge));
        b.RefuseCode = Planner.NotEnoughMoney;

        var actions = b.MoveTo(1.16050);                       // below H: a stop hedge can be placed, but the broker refuses it
        Assert.Equal(2, actions.OfType<PlaceHedge>().Count()); // once per leg, not every round
        Assert.All(b.Positions, p => Assert.Equal(1.16300, p.StopLoss!.Value, 9));

        b.Refuse.Clear();
        b.Now = b.Now.AddSeconds(30);
        Assert.Empty(b.Run().OfType<PlaceHedge>());           // still waiting
        b.Now = b.Now.AddSeconds(31);
        b.Run();
        Assert.All(new[] { TmLabel.HedgeMain, TmLabel.HedgeMirror }, role => Assert.Equal(1.16100, b.Order(role)!.Price, 9));
        Assert.All(b.Book.Trades[0].Legs(), l => Assert.Null(l.Leg.NoMoneyUtc));
    }

    [Fact]
    public void Below_the_warning_level_a_new_trade_gets_hedges_but_no_mirror()
    {
        var b = new FakeBroker();
        b.Account = Level(b, 250);
        b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();

        Assert.Null(b.Find(TmLabel.Mirror));
        Assert.DoesNotContain(b.Log, a => a is OpenMirror);
        Assert.Contains("MirrorSkipped", Events(b.Log));
        Assert.NotNull(b.Order(TmLabel.HedgeMain));
        Assert.True(b.Book.Trades[0].NoMirror);

        var pending = new FakeBroker { Account = Level(b, 250) };
        pending.PlaceManualStop(-1, 1.15950, Half, stopLoss: 1.16050);
        pending.Run();
        Assert.DoesNotContain(pending.Log, a => a is PlaceMirrorOrder);
        Assert.Single(pending.Orders);                         // the owner's own order only
    }

    [Fact]
    public void Above_the_warning_level_or_without_a_status_mirrors_are_opened_as_before()
    {
        foreach (var account in new AccountStatus?[] { null, new(1, DateTime.UtcNow, 1000, 1000, 100, 2, 0) }) // unknown; 1,000 %
        {
            var b = new FakeBroker { Account = account };
            b.OpenManual(-1, Half, stopLoss: 1.16100);
            b.Run();
            Assert.NotNull(b.Find(TmLabel.Mirror));
        }
    }

    [Fact]
    public void Below_the_rescue_level_TM_closes_the_trade_holding_the_most_margin_one_at_a_time()
    {
        var b = new FakeBroker();
        var small = b.OpenManual(-1, Half, stopLoss: 1.16100);
        var big = b.OpenManual(-1, 2 * Half, stopLoss: 1.16100);
        b.Run();
        Assert.Equal(4, b.Positions.Count);                    // two mains, two mirrors

        b.Account = Level(b, 90);
        b.RescueAllowed = true;
        var actions = b.Run();

        var rescue = Assert.Single(actions.OfType<Notify>(), n => n.Event == "MarginRescue");
        Assert.Equal(NotificationLevel.Critical, rescue.Level);
        Assert.Equal($"P{big.Id}", rescue.Trade!.Name);
        Assert.DoesNotContain(b.Positions, p => p.Id == big.Id);
        Assert.Contains(b.Positions, p => p.Id == small.Id);   // the next one waits for a fresh account status
        Assert.Equal(2, b.Positions.Count);
        Assert.DoesNotContain(b.Orders, o => o.Label.Contains($"P{big.Id}-"));
        Assert.Single(b.Book.Trades);
    }

    [Fact]
    public void Above_the_rescue_level_or_while_waiting_for_a_fresh_status_nothing_is_closed()
    {
        var b = new FakeBroker();
        b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();

        b.Account = Level(b, 150);
        b.RescueAllowed = true;
        Assert.DoesNotContain("MarginRescue", Events(b.Run()));

        b.Account = Level(b, 90);
        b.RescueAllowed = false;
        Assert.DoesNotContain("MarginRescue", Events(b.Run()));
        Assert.Equal(2, b.Positions.Count);
    }

    [Fact]
    public void A_restart_reads_TMs_backstop_as_a_backstop_not_as_a_new_hedge_level()
    {
        var b = new FakeBroker();
        var main = b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();
        b.Book.Trades.Clear();                                 // state file lost: the trade is rebuilt from its orders
        b.Run();
        Assert.Equal(1.16100, b.Book.Trades[0].H, 9);
        Assert.Equal(1.16300, b.Book.Trades[0].Main.Backstop!.Value, 9);
        Assert.DoesNotContain("HedgeMoved", Events(b.Log));
        Assert.Equal(1.16300, b.Positions.Single(p => p.Id == main.Id).StopLoss!.Value, 9);
    }

    [Fact]
    public void A_backstop_whose_confirmation_was_missed_is_still_not_a_new_hedge_level()
    {
        var b = new FakeBroker();
        var main = b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();
        b.Book.Trades[0].Main.Backstop = null;                 // the broker set it, but TM never saw the reply
        b.Run();
        Assert.Equal(1.16100, b.Book.Trades[0].H, 9);
        Assert.DoesNotContain("HedgeMoved", Events(b.Log));
        Assert.Equal(1.16300, b.Positions.Single(p => p.Id == main.Id).StopLoss!.Value, 9);
    }

    [Fact]
    public void Margin_levels_must_be_in_order()
    {
        var accounts = new[] { new TmAccount("brokerA", 1, ["EURUSD"]) };
        Assert.Throws<TmConfigException>(() => new TmConfig(accounts, MarginWarnLevel: 100, MarginRescueLevel: 100).Validated());
        Assert.Throws<TmConfigException>(() => new TmConfig(accounts, MarginRescueLevel: 0).Validated());
        new TmConfig(accounts).Validated();
    }
}
