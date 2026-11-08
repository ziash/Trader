using Trader.Core.OpenApi;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>Scout's margin budget (scout.md §2.5, issue-log.md #1, review P-05): what the account could come to hold, by the broker's own margin rule.</summary>
public sealed class MarginBudgetTests
{
    private const long Sym = 1;
    private static readonly ScoutConfig Config = new([new ScoutAccount("brokerA", 1, [new ScoutChart("EURUSD", "H1", ScoutMode.Trade)])]);
    private static readonly MarginStress NoStress = new(1, 0, 1);

    private static MarginBudget Make(ProtoOATotalMarginCalculationType how, double equity, MarginStress? stress = null, bool stressed = false) =>
        new(how, equity, 50, stress ?? NoStress, _ => stressed);

    private const ProtoOATotalMarginCalculationType Sum = ProtoOATotalMarginCalculationType.Sum, Larger = ProtoOATotalMarginCalculationType.Max, Net = ProtoOATotalMarginCalculationType.Net;

    [Fact]
    public void A_trade_with_its_hedges_costs_twice_on_sum_margin_and_once_on_larger_side_margin()
    {
        var sum = Make(Sum, 1000);
        sum.AddTrade("a", Sym, 1, 100); // order and mirror together
        Assert.Equal(200, sum.ProjectedMargin, 6);

        var larger = Make(Larger, 1000);
        larger.AddTrade("a", Sym, 1, 100);
        Assert.Equal(100, larger.ProjectedMargin, 6);
    }

    [Fact]
    public void On_net_margin_the_worst_moment_is_before_the_hedges_fill()
    {
        var net = Make(Net, 1000);
        net.AddTrade("a", Sym, 1, 100);
        Assert.Equal(100, net.ProjectedMargin, 6);
    }

    [Fact]
    public void A_filled_trade_still_reserves_its_waiting_hedges_on_sum_margin()
    {
        var sum = Make(Sum, 1000);
        sum.AddPosition(Sym, 1, 100);
        sum.AddHedgeOrder(Sym, -1, 100);
        Assert.Equal(200, sum.ProjectedMargin, 6);

        var larger = Make(Larger, 1000);
        larger.AddPosition(Sym, 1, 100);
        larger.AddHedgeOrder(Sym, -1, 100);
        Assert.Equal(100, larger.ProjectedMargin, 6);
    }

    [Fact]
    public void The_two_orders_of_one_pause_count_once()
    {
        var sum = Make(Sum, 1000);
        sum.AddTrade("EURUSD|H1|202611031400", Sym, 1, 100);
        sum.AddTrade("EURUSD|H1|202611031400", Sym, -1, 60);
        Assert.Equal(200, sum.ProjectedMargin, 6);
    }

    [Fact]
    public void A_refused_trade_leaves_the_budget_as_it_was_and_the_next_one_can_fit()
    {
        var b = Make(Sum, 1000); // limit $500
        Assert.True(b.TryTake("a", Sym, 1, 150));
        Assert.False(b.TryTake("b", Sym, 1, 150));
        Assert.Equal(300, b.ProjectedMargin, 6);
        Assert.True(b.TryTake("c", Sym, 1, 100));
        Assert.Equal(500, b.ProjectedMargin, 6);
    }

    [Fact]
    public void The_second_order_of_a_pause_adds_only_what_it_adds()
    {
        var b = Make(Sum, 1000);
        Assert.True(b.TryTake("a", Sym, 1, 200));
        Assert.True(b.TryTake("a", Sym, -1, 200));
        Assert.Equal(400, b.ProjectedMargin, 6);
    }

    [Fact]
    public void The_stress_doubles_margin_on_its_symbols_and_takes_equity_and_spread()
    {
        var b = Make(Larger, 1000, new MarginStress(), stressed: true);
        b.AddPosition(Sym, 1, 400);
        b.AddStressLoss(50);
        Assert.Equal(800, b.StressedMargin, 6);
        Assert.Equal(850, b.StressedEquity, 6);
        Assert.False(b.StressBreached);
        Assert.False(b.TryTake("a", Sym, 1, 60)); // 460 inside the 500 limit, but 920 against 850 under stress
    }

    [Fact]
    public void An_account_like_IC_on_06_Nov_2026_places_nothing()
    {
        var b = Make(Larger, 2491.84, new MarginStress());
        b.AddPosition(Sym, 1, 2707.70);
        Assert.True(b.StressBreached);
        Assert.False(b.TryTake("a", Sym, 1, 5));
        Assert.Equal("projected margin $2,712.70 of $1,245.92 allowed (50 % of equity $2,491.84); under stress $2,712.70 against equity $2,242.66", b.Explain("a", Sym, 1, 5));
    }

    [Fact]
    public void The_stress_covers_metals_and_crypto_unless_told_otherwise()
    {
        Assert.True(new MarginStress().Applies("XAUUSD"));
        Assert.True(new MarginStress().Applies("SOLUSD"));
        Assert.False(new MarginStress().Applies("EURUSD"));
        Assert.True(new MarginStress(Symbols: ["EURUSD"]).Applies("EURUSD"));
    }

    [Fact]
    public void The_margin_settings_are_checked()
    {
        var accounts = Config.Accounts;
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, MarginUsePercent: 0).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, MarginUsePercent: 101).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, Stress: new MarginStress(0.5)).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, Stress: new MarginStress(EquityFallPercent: 100)).Validated());
        Assert.Equal(50, new ScoutConfig(accounts).Validated().MarginUsePercent);
    }
}
