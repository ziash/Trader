using Trader.Core.Notifications;
using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>TM's rules (tm.md) run against <see cref="FakeBroker"/>. EURUSD, 0.5 lot; spread 0.2 pip.</summary>
public sealed class PlannerTests
{
    private const long Half = 5_000_000; // 0.5 lot in Open API units

    private static List<string> Events(IEnumerable<TmAction> actions) => actions.OfType<Notify>().Select(n => n.Event).ToList();

    /// <summary>Owner shorts at 1.16000 with a stop-loss at 1.16100: H = 1.16100, 1R = 10 pips.</summary>
    private static (FakeBroker B, Pos Main) ShortWithStop()
    {
        var b = new FakeBroker();
        var main = b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();
        return (b, main);
    }

    [Fact]
    public void Take_over_mirrors_hedges_both_and_removes_the_stops_once_hedged()
    {
        var (b, main) = ShortWithStop();

        var mirror = b.Find(TmLabel.Mirror)!;
        Assert.Equal((-1, Half, 1.16000), (mirror.Dir, mirror.Volume, mirror.Entry));
        Assert.All(new[] { TmLabel.HedgeMain, TmLabel.HedgeMirror }, role =>
        {
            var o = b.Order(role)!;
            Assert.Equal((1, Half, 1.16100), (o.Dir, o.Volume, o.Price)); // buy-stops at H
        });
        Assert.Null(b.Positions.Single(p => p.Id == main.Id).StopLoss);   // removed after its hedge was confirmed
        Assert.Null(mirror.StopLoss);
        Assert.Contains("TradeTakenOver", Events(b.Log));
        var t = Assert.Single(b.Book.Trades);
        Assert.Equal(0.00100, t.Main.R, 9);

        // Order matters: the mirror opened with a stop at H, and the stops went only after the hedges.
        var kinds = b.Log.Where(a => a is not Notify).Select(a => $"{a.GetType().Name}:{a.Role}").ToList();
        Assert.True(kinds.IndexOf("PlaceHedge:HMAIN") < kinds.IndexOf("SetProtection:MAIN"));
        Assert.True(kinds.IndexOf("PlaceHedge:HMIR") < kinds.IndexOf("SetProtection:MIR"));
    }

    [Fact]
    public void Without_a_stop_loss_the_hedge_sits_at_the_default_distance_and_waits_for_it_if_unknown()
    {
        var b = new FakeBroker { Distance = null };
        b.OpenManual(-1, Half);
        Assert.IsType<NeedDistance>(Assert.Single(b.Run()));
        Assert.Empty(b.Book.Trades);

        b.Distance = 0.0012;
        b.Run();
        Assert.Equal(1.16120, b.Order(TmLabel.HedgeMain)!.Price, 9);
        Assert.Equal(1.16120, b.Order(TmLabel.HedgeMirror)!.Price, 9);
    }

    [Fact]
    public void Longs_are_mirrored_with_sell_stop_hedges_below()
    {
        var b = new FakeBroker();
        b.OpenManual(1, Half); // buys at the ask 1.16002
        b.Run();
        var o = b.Order(TmLabel.HedgeMain)!;
        Assert.Equal((-1, 1.15902), (o.Dir, Math.Round(o.Price, 5)));
        Assert.Equal(1, b.Find(TmLabel.Mirror)!.Dir);
    }

    [Fact]
    public void Scenario_1_wick_hedge_fills_arms_exits_plus_one_pip_rehedges_above_then_back_at_H()
    {
        var (b, _) = ShortWithStop();

        b.MoveTo(1.16098);                                     // ask 1.16100: both hedges fill
        var hedge = b.Find(TmLabel.HedgeMain)!;
        Assert.Equal(1.16100, hedge.Entry, 9);
        Assert.Equal(1.16050, hedge.StopLoss!.Value, 9);       // B = ½R behind the fill
        Assert.Contains("HedgeFilled", Events(b.Log));

        b.MoveTo(1.16200);                                     // hedge 1R in profit: armed
        Assert.Equal(1.16110, b.Find(TmLabel.HedgeMain)!.StopLoss!.Value, 9); // fill + 1 pip
        Assert.Contains("HedgeArmed", Events(b.Log));

        b.MoveTo(1.16110);                                     // the wick reverses: hedge exits at +1 pip
        Assert.Null(b.Find(TmLabel.HedgeMain));
        Assert.Equal(1.16212, b.Order(TmLabel.HedgeMain)!.Price, 9); // re-hedged 1R above the ask (1.16112)
        Assert.True(b.Book.Trades[0].Main.Raised);
        Assert.Equal(0.00010, b.Book.Trades[0].Main.Realized, 9);

        b.MoveTo(1.16050);                                     // back below H: hedge returns to H
        Assert.Equal(1.16100, b.Order(TmLabel.HedgeMain)!.Price, 9);
        Assert.False(b.Book.Trades[0].Main.Raised);
    }

    [Fact]
    public void Scenario_2_hedge_stop_closes_it_behind_its_fill_and_the_hedge_returns_to_H()
    {
        var (b, _) = ShortWithStop();
        b.MoveTo(1.16098);                                     // hedges fill at 1.16100
        b.MoveTo(1.16050);                                     // falls back before arming: hedge stop at 1.16050 hit
        Assert.Null(b.Find(TmLabel.HedgeMain));
        Assert.Equal(1.16100, b.Order(TmLabel.HedgeMain)!.Price, 9);
        Assert.Equal(-0.00050, b.Book.Trades[0].Main.Realized, 9); // the cycle cost ½R
    }

    [Fact]
    public void Break_even_at_1R_sets_the_stop_first_then_removes_the_hedge()
    {
        var (b, main) = ShortWithStop();
        var actions = b.MoveTo(1.15890);                       // ask 1.15892: 10.8 pips in profit

        Assert.Equal(1.16000, b.Positions.Single(p => p.Id == main.Id).StopLoss!.Value, 9);
        Assert.Equal(1.16000, b.Find(TmLabel.Mirror)!.StopLoss!.Value, 9);
        Assert.Empty(b.Orders);
        Assert.True(b.Book.Trades[0].Main.Protected);
        var kinds = actions.Where(a => a.Role is "MAIN" or TmLabel.HedgeMain && a is not Notify).Select(a => a.GetType().Name).ToList();
        Assert.Equal(["SetProtection", "CancelOrder"], kinds);
    }

    [Fact]
    public void A_gap_past_2R_warns_and_past_3R_closes_the_leg_and_its_hedge()
    {
        var (b, main) = ShortWithStop();
        b.MoveTo(1.16250);                                     // gap: hedges fill at the ask 1.16252, ~2.5R lost
        Assert.Contains("LossWarning", Events(b.Log));
        Assert.Equal(2, b.Positions.Count(p => p.Label.EndsWith("HMAIN") || p.Label.EndsWith("HMIR")));

        var b2 = ShortWithStop().B;
        b2.MoveTo(1.16400);                                    // ~4R in one jump
        Assert.Contains("LossLimit", Events(b2.Log));
        Assert.Empty(b2.Positions);                            // main, mirror and both hedges closed
        Assert.Empty(b2.Book.Trades);
        Assert.Equal(NotificationLevel.Critical, b2.Log.OfType<Notify>().First(n => n.Event == "LossLimit").Level);
    }

    [Fact]
    public void Owner_closing_the_main_removes_only_its_hedge_the_mirror_carries_on()
    {
        var (b, main) = ShortWithStop();
        b.Close(main.Id);
        b.Run();
        Assert.Null(b.Order(TmLabel.HedgeMain));
        Assert.NotNull(b.Order(TmLabel.HedgeMirror));
        Assert.NotNull(b.Find(TmLabel.Mirror));
        Assert.Contains("LegClosed", Events(b.Log));

        b.Close(b.Find(TmLabel.Mirror)!.Id);
        b.Run();
        Assert.Empty(b.Orders);
        Assert.Empty(b.Book.Trades);
        Assert.Contains("TradeDone", Events(b.Log));
    }

    [Fact]
    public void Take_profit_is_copied_to_the_mirror_and_follows_changes()
    {
        var b = new FakeBroker();
        var main = b.OpenManual(-1, Half, stopLoss: 1.16100, takeProfit: 1.15500);
        b.Run();
        Assert.Equal(1.15500, b.Find(TmLabel.Mirror)!.TakeProfit!.Value, 9);
        Assert.Equal(1.15500, b.Positions.Single(p => p.Id == main.Id).TakeProfit!.Value, 9); // the main keeps its own

        b.SetTakeProfit(main.Id, 1.15400);
        b.Run();
        Assert.Equal(1.15400, b.Find(TmLabel.Mirror)!.TakeProfit!.Value, 9);
    }

    [Fact]
    public void Owners_pending_order_gets_a_mirror_order_and_both_are_hedged_once_filled()
    {
        var b = new FakeBroker();
        var sellStop = b.PlaceManualStop(-1, 1.15950, Half, stopLoss: 1.16050, takeProfit: 1.15500); // a breakdown entry, by hand
        b.Run();
        var mirror = b.Orders.Single(o => o.Id != sellStop.Id);
        Assert.Equal(("sell-stop", Half, 1.15950, 1.16050, 1.15500), (mirror.Name, mirror.Volume, mirror.Price, mirror.StopLoss, mirror.TakeProfit));
        Assert.EndsWith($"-O{sellStop.Id}-MIR", mirror.Label);
        Assert.Null(b.Order(TmLabel.HedgeMain));                         // hedges wait for the fills
        Assert.Contains("PendingMirrored", Events(b.Log));
        Assert.True(b.Book.Trades.Single().Pending);

        b.MoveTo(1.15950);                                               // both fill
        Assert.Contains("TradeTakenOver", Events(b.Log));
        Assert.DoesNotContain(b.Log, a => a is OpenMirror);              // no second mirror at market
        var t = b.Book.Trades.Single();
        var main = b.Positions.Single(p => p.Label.Length == 0);
        Assert.Equal((main.Id, b.Find(TmLabel.Mirror)!.Id), (t.MainId, t.Mirror!.PositionId));
        Assert.All(new[] { TmLabel.HedgeMain, TmLabel.HedgeMirror }, role => Assert.Equal((1, 1.16050), (b.Order(role)!.Dir, b.Order(role)!.Price)));
        Assert.All(new[] { main, b.Find(TmLabel.Mirror)! }, p => Assert.Equal((null, 1.15500), (p.StopLoss, p.TakeProfit))); // SL gone once hedged
    }

    [Fact]
    public void The_mirror_order_follows_the_owners_order_and_a_cancel()
    {
        var b = new FakeBroker();
        var buyLimit = b.PlaceManual(limit: true, 1, 1.15900, Half);     // a pullback entry without a stop-loss
        b.Run();
        Ord Mirror() => b.Orders.Single(o => o.Label.EndsWith("-MIR"));
        Assert.Equal(("buy-limit", 1.15900, 1.15800), (Mirror().Name, Mirror().Price, Mirror().StopLoss!.Value)); // H 10 pips below

        b.AmendManual(buyLimit.Id, 1.15850, null, null);                 // moved: H keeps its distance
        b.Run();
        Assert.Equal((1.15850, 1.15750), (Mirror().Price, Mirror().StopLoss!.Value));
        b.AmendManual(buyLimit.Id, 1.15850, 1.15700, 1.16500);           // a stop-loss and take-profit added
        b.Run();
        Assert.Equal((1.15700, 1.16500), (Mirror().StopLoss!.Value, Mirror().TakeProfit!.Value));
        Assert.Equal(1.15700, b.Book.Trades.Single().H, 9);

        b.Cancel(buyLimit.Id);                                           // the owner changes their mind
        b.Run();
        Assert.Empty(b.Orders);
        Assert.Empty(b.Book.Trades);
        Assert.Contains("PendingDone", Events(b.Log));
    }

    [Fact]
    public void A_refused_mirror_order_means_a_mirror_at_market_after_the_fill()
    {
        var b = new FakeBroker();
        b.Refuse.Add(nameof(PlaceMirrorOrder));
        b.PlaceManualStop(-1, 1.15950, Half, stopLoss: 1.16050);
        b.Run();
        b.Run();
        Assert.Single(b.Log.OfType<PlaceMirrorOrder>());                 // tried once, not every second
        b.Refuse.Clear();
        b.MoveTo(1.15950);
        Assert.Single(b.Log.OfType<OpenMirror>());
        Assert.NotNull(b.Order(TmLabel.HedgeMirror));
    }

    [Fact]
    public void Snapshot_reads_trailing_stops_and_both_kinds_of_pending_order()
    {
        var res = new ProtoOAReconcileRes { CtidTraderAccountId = 1 };
        res.Position.Add(new ProtoOAPosition
        {
            PositionId = 7, Price = 1.16, StopLoss = 1.161, TrailingStopLoss = true,
            TradeData = new ProtoOATradeData { SymbolId = 1, Volume = Half, TradeSide = ProtoOATradeSide.Sell },
        });
        foreach (var (type, id) in new[] { (ProtoOAOrderType.Limit, 8L), (ProtoOAOrderType.Stop, 9L), (ProtoOAOrderType.StopLimit, 10L) })
            res.Order.Add(new ProtoOAOrder
            {
                OrderId = id, OrderType = type, LimitPrice = 1.162, StopPrice = 1.158, StopLoss = 1.163, TakeProfit = 1.150,
                TradeData = new ProtoOATradeData { SymbolId = 1, Volume = Half, TradeSide = ProtoOATradeSide.Sell },
            });
        var snap = Snapshot.From(res);
        Assert.True(snap.Positions.Single().Trailing);
        Assert.Equal([(8L, true, 1.162), (9L, false, 1.158)], snap.Orders.Select(o => (o.Id, o.Limit, o.Price))); // stop-limits are not handled
        Assert.All(snap.Orders, o => Assert.Equal((1.163, 1.150), (o.StopLoss, o.TakeProfit)));
    }

    [Fact]
    public void Trailing_stop_on_the_main_is_removed_while_hedged_and_returns_on_both_legs_at_break_even()
    {
        var b = new FakeBroker();
        var main = b.OpenManual(-1, Half, stopLoss: 1.16100, trailing: true);
        b.Run();
        Assert.True(b.Book.Trades[0].Trailing);
        Assert.Null(b.Positions.Single(p => p.Id == main.Id).StopLoss);   // the hedge protects until 1R
        Assert.False(b.Positions.Single(p => p.Id == main.Id).Trailing);

        b.MoveTo(1.15890);                                                // 1R in profit
        foreach (var leg in new[] { b.Positions.Single(p => p.Id == main.Id), b.Find(TmLabel.Mirror)! })
            Assert.Equal((1.16000, true), (leg.StopLoss!.Value, leg.Trailing));
        Assert.Contains(b.Log.OfType<SetProtection>(), a => a.Role == TmLabel.Mirror && a.Trailing);

        b.SetTrailing(main.Id, false);                                    // the owner switches it off in cTrader
        b.Run();
        Assert.Contains("TrailingOff", Events(b.Log));
        Assert.Equal((1.16000, false), (b.Find(TmLabel.Mirror)!.StopLoss!.Value, b.Find(TmLabel.Mirror)!.Trailing));
    }

    [Fact]
    public void Trailing_switched_on_after_break_even_reaches_the_mirror()
    {
        var (b, main) = ShortWithStop();
        b.MoveTo(1.15890);
        Assert.False(b.Find(TmLabel.Mirror)!.Trailing);                   // no trailing asked for
        b.SetStopLoss(main.Id, 1.15950);                                  // the owner tightens the main's stop …
        b.SetTrailing(main.Id, true);                                     // … and lets it trail
        b.Run();
        Assert.Contains("TrailingOn", Events(b.Log));
        Assert.Equal((1.15950, true), (b.Positions.Single(p => p.Id == main.Id).StopLoss!.Value, b.Positions.Single(p => p.Id == main.Id).Trailing));
        Assert.Equal((1.16000, true), (b.Find(TmLabel.Mirror)!.StopLoss!.Value, b.Find(TmLabel.Mirror)!.Trailing));
    }

    [Fact]
    public void A_new_stop_loss_on_the_main_moves_both_hedges_and_is_removed_again()
    {
        var (b, main) = ShortWithStop();
        b.SetStopLoss(main.Id, 1.16080);
        b.Run();
        Assert.All(b.Orders, o => Assert.Equal(1.16080, o.Price, 9));
        Assert.Null(b.Positions.Single(p => p.Id == main.Id).StopLoss);
        Assert.Equal(0.00080, b.Book.Trades[0].Main.R, 9);
        Assert.Contains("HedgeMoved", Events(b.Log));
    }

    [Fact]
    public void Dragging_one_hedge_order_moves_the_other_and_a_wrong_side_level_is_ignored()
    {
        var (b, main) = ShortWithStop();
        b.DragOrder(TmLabel.HedgeMain, 1.16150);
        b.Run();
        Assert.All(b.Orders, o => Assert.Equal(1.16150, o.Price, 9));

        b.SetStopLoss(main.Id, 1.15900);                       // below the entry of a short: not protective
        b.Run();
        Assert.All(b.Orders, o => Assert.Equal(1.16150, o.Price, 9));
        Assert.Contains("HedgeMoveIgnored", Events(b.Log));
        Assert.Null(b.Positions.Single(p => p.Id == main.Id).StopLoss);
    }

    [Fact]
    public void A_main_found_late_gets_hedges_only_and_at_market_if_price_is_already_beyond_H()
    {
        var b = new FakeBroker();
        b.OpenManual(-1, Half, openUtc: b.Now.AddMinutes(-5)); // H = 1.16100 from the default distance
        b.MoveTo(1.16150);                                     // TM starts now; price is already above H
        Assert.Null(b.Find(TmLabel.Mirror));
        Assert.NotNull(b.Find(TmLabel.HedgeMain));             // hedge opened at market
        Assert.Contains("MirrorSkipped", Events(b.Log));
        Assert.Contains("HedgeAtMarket", Events(b.Log));
    }

    [Fact]
    public void A_refused_mirror_leaves_the_main_hedged_without_a_mirror()
    {
        var b = new FakeBroker();
        b.Refuse.Add(nameof(OpenMirror));
        b.OpenManual(-1, Half, stopLoss: 1.16100);
        b.Run();
        Assert.Null(b.Find(TmLabel.Mirror));
        Assert.NotNull(b.Order(TmLabel.HedgeMain));
        Assert.True(b.Book.Trades[0].NoMirror);
    }

    [Fact]
    public void A_trade_is_rebuilt_from_its_orders_when_the_state_is_lost()
    {
        var (b, _) = ShortWithStop();
        b.Book.Trades.Clear();                                 // e.g. the state file was deleted
        b.Run();
        var t = Assert.Single(b.Book.Trades);
        Assert.Equal(1.16100, t.H, 9);
        Assert.NotNull(t.Mirror);
        Assert.Contains("TradeRebuilt", Events(b.Log));

        b.MoveTo(1.15890);                                     // and is managed as before
        Assert.Empty(b.Orders);
    }

    [Fact]
    public void Other_bots_and_unlisted_symbols_are_left_alone()
    {
        var b = new FakeBroker();
        b.Positions.Add(new Pos(1, FakeBroker.SymbolId, -1, Half, 1.16, null, null, "AuditBot-123456", b.Now)); // Audit Bot probe
        b.Positions.Add(new Pos(2, 99, -1, Half, 1.16, null, null, "", b.Now));                                // unlisted symbol
        Assert.Empty(b.Run());
        Assert.Empty(b.Book.Trades);
    }

    [Fact]
    public void A_hedge_whose_stop_the_price_already_passed_is_closed_at_market()
    {
        var (b, _) = ShortWithStop();
        b.Refuse.Add(nameof(SetProtection));                  // the stop is not on yet when ...
        b.MoveTo(1.16098);                                     // ... the hedges fill at 1.16100
        b.Refuse.Clear();
        b.MoveTo(1.16040);                                     // and the price falls past the hedge stop (1.16050)
        Assert.Null(b.Find(TmLabel.HedgeMain));
        Assert.Contains(b.Log, a => a is ClosePosition { Role: TmLabel.HedgeMain });
        Assert.Equal(1.16100, b.Order(TmLabel.HedgeMain)!.Price, 9); // re-hedged at H
    }

    [Fact]
    public void A_hedge_level_within_a_few_spreads_is_warned_about()
    {
        var b = new FakeBroker { Spread = 0.00005 };
        b.OpenManual(-1, Half, stopLoss: 1.16020);             // 1R = 2 pips, spread 0.5 pip
        b.Run();
        Assert.Contains("TightHedge", Events(b.Log));
        Assert.DoesNotContain("TightHedge", Events(ShortWithStop().B.Log));
    }

    [Theory]
    [InlineData("TM-3f9a1c-P123456789-MIR", true, 123456789, "MIR")]
    [InlineData("TM-3f9a1c-P42-HMAIN", true, 42, "HMAIN")]
    [InlineData("TM-3f9a1c-P42-X", false, 0, "")]
    [InlineData("AuditBot-3f9a1c-P42-MIR", false, 0, "")]
    [InlineData("", false, 0, "")]
    public void Labels_link_every_order_to_its_main(string label, bool ok, long mainId, string role)
    {
        Assert.Equal(ok, TmLabel.TryParse(label, "TM", out var id, out var r));
        Assert.Equal((mainId, role), (id, r));
    }
}
