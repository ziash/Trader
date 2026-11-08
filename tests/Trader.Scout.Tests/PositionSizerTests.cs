using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>Sizing from the money at risk (scout.md §2.8, review P-02).</summary>
public sealed class PositionSizerTests
{
    private static readonly SizingSettings On = new(true);

    private static SymbolInfo Sym(string quote = "USD", long lotUnits = 100_000, long min = 100_000, long step = 100_000, long max = long.MaxValue, CommissionRule? commission = null) =>
        new(1, "X", "X", "EUR", quote, 5, 4, lotUnits, true, ProtoOATradingMode.Enabled, TradingSchedule.AlwaysOpen, min, step, max, commission);

    [Fact]
    public void Worked_example_from_the_review_rounds_down_to_the_step()
    {
        // $4,467 × 0.5 % = $22.34 for two legs; a 15-pip stop plus 1 pip of costs at $10 a pip = $160 per lot → 0.0698 → 0.06 lot
        var d = PositionSizer.Decide(Sym(), On, 4467, 1.1000, 1.0985, 0.0001, 1);
        Assert.Null(d.Refusal);
        Assert.Equal(0.06, Sym().LotsForVolume(d.Volume!.Value), 9);
        Assert.Equal(2 * 0.06 * 160, d.RiskCash, 6);
    }

    [Fact]
    public void A_loss_at_the_stop_never_exceeds_the_budget()
    {
        foreach (var (entry, stop, rate) in new[] { (1.1, 1.095, 1.0), (150.0, 149.5, 0.0067), (26000.0, 25950.0, 1.08), (2000.0, 2010.0, 1.0) })
        {
            var d = PositionSizer.Decide(Sym(), On, 10_000, entry, stop, entry * 0.00005, rate);
            Assert.True(d.Volume is null || d.RiskCash <= 10_000 * 0.005 + 1e-9, $"{entry}: {d.RiskCash}");
        }
    }

    [Fact]
    public void The_quote_currency_is_converted()
    {
        // USDJPY-style: 50 yen stop distance per unit price; 1 JPY = $0.0067
        var d = PositionSizer.Decide(Sym("JPY"), new SizingSettings(true, SlippageSpreads: 0), 10_000, 150, 149.5, 0.01, 0.0067);
        var perLot = 0.5 * 100_000 * 0.0067; // $335
        Assert.Equal(Math.Floor(50 / (2 * perLot) * 100) / 100, Sym().LotsForVolume(d.Volume!.Value), 9);
    }

    [Fact]
    public void A_minimum_above_the_budget_is_refused_not_raised()
    {
        var gas = Sym(lotUnits: 10_000, min: 500_000, step: 10_000); // minimum 0.5 lot
        var d = PositionSizer.Decide(gas, On, 5000, 3.0, 2.9, 0.001, 1);
        Assert.Null(d.Volume);
        Assert.Contains("below the broker's minimum", d.Refusal);
    }

    [Fact]
    public void The_size_stops_at_the_brokers_maximum()
    {
        var d = PositionSizer.Decide(Sym(max: 250_000), On, 1_000_000, 1.1, 1.095, 0.0001, 1);
        Assert.Equal(200_000, d.Volume); // whole steps only
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1.1)] // the stop is the entry
    public void No_valid_stop_is_refused(double? stop) =>
        Assert.Contains("stop-loss", PositionSizer.Decide(Sym(), On, 5000, 1.1, stop, 0.0001, 1).Refusal);

    [Fact]
    public void No_conversion_is_refused() =>
        Assert.Contains("converts JPY", PositionSizer.Decide(Sym("JPY"), On, 5000, 150, 149, 0.01, null).Refusal);

    [Fact]
    public void A_stop_inside_a_few_spreads_is_refused() =>
        Assert.Contains("spreads away", PositionSizer.Decide(Sym(), On, 5000, 1.1, 1.0999, 0.0001, 1).Refusal);

    [Fact]
    public void Costs_make_the_size_smaller()
    {
        var charged = Sym(commission: new CommissionRule(ProtoOACommissionType.UsdPerLot, 3 * 100_000_000L, 0, ProtoOAMinCommissionType.Currency));
        var settings = new SizingSettings(true, RiskPercent: 1, MaxLossPercent: 5);
        var with = PositionSizer.Decide(charged, settings, 100_000, 1.1, 1.098, 0.0001, 1);
        var without = PositionSizer.Decide(Sym(), settings, 100_000, 1.1, 1.098, 0.0001, 1);
        Assert.NotNull(with.Volume);
        Assert.True(with.Volume < without.Volume);
    }

    [Fact]
    public void The_hard_cap_at_the_loss_limit_refuses_a_loose_setting()
    {
        var d = PositionSizer.Decide(Sym(), new SizingSettings(true, RiskPercent: 1, MaxLossPercent: 2), 100_000, 1.1, 1.098, 0.0001, 1);
        Assert.Contains("3R limit", d.Refusal);
    }

    [Fact]
    public void One_leg_gets_twice_the_size_of_two()
    {
        var two = PositionSizer.Decide(Sym(), On, 100_000, 1.1, 1.095, 0, 1);
        var one = PositionSizer.Decide(Sym(), new SizingSettings(true, Legs: 1), 100_000, 1.1, 1.095, 0, 1);
        Assert.Equal(two.Volume * 2, one.Volume);
    }

    [Fact]
    public void Commission_rules_follow_the_brokers_types()
    {
        Assert.Equal(6, new CommissionRule(ProtoOACommissionType.UsdPerLot, 300_000_000, 0, ProtoOAMinCommissionType.Currency).RoundTripPerLotUsd(110_000, 1)!.Value, 9);
        Assert.Equal(6.6, new CommissionRule(ProtoOACommissionType.UsdPerMillionUsd, 30L * 100_000_000, 0, ProtoOAMinCommissionType.Currency).RoundTripPerLotUsd(110_000, 1)!.Value, 9);
    }
}
