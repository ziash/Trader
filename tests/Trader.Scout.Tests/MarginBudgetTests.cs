using Trader.Core.OpenApi;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>Scout's margin budget (scout.md §2.5, issue-log.md #1): no order the account could not carry once filled, mirrored and hedged.</summary>
public sealed class MarginBudgetTests
{
    private static readonly ScoutConfig Config = new([new ScoutAccount("brokerA", 1, [new ScoutChart("EURUSD", "H1", ScoutMode.Trade)])]);

    private static AccountStatus Account(double equity, double used) => new(1, DateTime.UtcNow, equity, equity, used, 4, 0);

    [Fact]
    public void Orders_are_placed_while_four_times_their_margin_fits_within_half_the_equity()
    {
        var budget = new MarginBudget(Account(5000, 500), Config, waitingMargin: 100); // 2,500 − 500 − 4 × 100 = 1,600 left
        Assert.Equal(1600, budget.Left, 6);
        Assert.True(budget.TryTake(300));                     // 1,200 → 400 left
        Assert.True(budget.TryTake(100));                     // exactly fits
        Assert.False(budget.TryTake(0.01));
        Assert.Equal(0, budget.Left, 6);
    }

    [Fact]
    public void An_account_like_IC_on_06_Nov_2026_places_nothing()
    {
        var budget = new MarginBudget(Account(2491.84, 2707.70), Config, waitingMargin: 0);
        Assert.False(budget.TryTake(5));
        Assert.Equal("needs $20.00 (4 × $5.00), -$1,461.78 left of 50 % of equity $2,491.84 (in use $2,707.70, waiting orders $0.00)", budget.Explain(5));
    }

    [Fact]
    public void The_margin_settings_are_checked()
    {
        var accounts = Config.Accounts;
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, MarginUsePercent: 0).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, MarginUsePercent: 101).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig(accounts, MarginFactor: 0.5).Validated());
        Assert.Equal((50, 4), (new ScoutConfig(accounts).Validated().MarginUsePercent, new ScoutConfig(accounts).MarginFactor));
    }
}
