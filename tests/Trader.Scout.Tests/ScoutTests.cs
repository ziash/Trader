using Trader.Core.MarketData;
using Trader.Scout;

namespace Trader.Scout.Tests;

public sealed class ScoutTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Repo_config_loads_trade_mode_on_H1_for_IC_Markets_with_sizes_per_symbol()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", ScoutConfig.FileName);
        var c = ScoutConfig.Load(path);
        Assert.Equal([49123893L], c.Accounts.Select(a => a.AccountId));
        Assert.Equal(18, c.Accounts[0].Charts.Count);
        Assert.All(c.Accounts.SelectMany(a => a.Charts), ch => Assert.Equal(("H1", ScoutMode.Trade), (ch.Timeframe, ch.Mode)));
        Assert.Equal((0.1, 16, LevelMode.Smart), (c.Lots, c.ExpiryCandles, c.SetupSettings.Level));
        Assert.Equal((0.1, 0.01, 20.0), (c.LotsFor("EURUSD"), c.LotsFor("XAUUSD"), c.LotsFor("SOLUSD")));
    }

    [Fact]
    public void Sizes_per_symbol_must_be_positive_and_on_a_chart()
    {
        ScoutConfig With(Dictionary<string, double> lots) => new([new ScoutAccount("brokerA", 1, [new ScoutChart("XAUUSD", "H1")])], LotsBySymbol: lots);
        Assert.Equal(0.01, With(new() { ["XAUUSD"] = 0.01 }).Validated().LotsFor("XAUUSD"));
        Assert.Throws<ScoutConfigException>(() => With(new() { ["XAUUSD"] = 0 }).Validated());
        Assert.Throws<ScoutConfigException>(() => With(new() { ["XAUUSD"] = 101 }).Validated());
        Assert.Throws<ScoutConfigException>(() => With(new() { ["XAGUSD"] = 0.01 }).Validated());
    }

    /// <summary>A size below the broker's minimum is raised to it (SOLUSD at IC Markets: 1 lot), one between steps rounded down.</summary>
    [Fact]
    public void Real_size_follows_the_brokers_minimum_and_step()
    {
        SymbolInfo Sym(long lotUnits, long min, long step) =>
            new(1, "X", "X", "X", "USD", 2, 2, lotUnits, true, ProtoOATradingMode.Enabled, TradingSchedule.AlwaysOpen, min, step);
        var sol = Sym(1, 100, 1);                // 1 coin per lot; minimum 1 lot, step 0.01 lot
        Assert.Equal(1, sol.LotsForVolume(sol.VolumeForLots(0.1)));
        Assert.Equal(20, sol.LotsForVolume(sol.VolumeForLots(20)));
        var de40 = Sym(1, 10, 10);               // minimum and step 0.1 lot
        Assert.Equal(0.1, de40.LotsForVolume(de40.VolumeForLots(0.15)), 9);
    }

    [Fact]
    public void Config_refuses_a_chart_twice_and_unknown_timeframes()
    {
        var chart = new ScoutChart("EURUSD", "H1");
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig([new ScoutAccount("brokerA", 1, [chart, chart])]).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig([new ScoutAccount("brokerA", 1, [new ScoutChart("EURUSD", "M5")])]).Validated());
        Assert.Throws<ScoutConfigException>(() => new ScoutConfig([new ScoutAccount("brokerA", 1, [chart])], Lots: 101).Validated());
    }

    [Fact]
    public void Keys_name_the_pause_and_the_sibling_is_the_other_order_of_the_pair()
    {
        var key = ScoutState.Key("EURUSD", "H1", new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc), reversal: false);
        Assert.Equal("EURUSD|H1|202611031400|M", key);
        Assert.Equal("EURUSD|H1|202611031400|R", ScoutState.Sibling(key));
        Assert.Equal(key, ScoutState.Sibling(ScoutState.Sibling(key)));
    }

    [Fact]
    public void State_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-state-{Guid.NewGuid():N}.json");
        try
        {
            var s = ScoutState.Load(path);
            s.Roles["EURUSD|H1|202611031400|M"] = new RoleRecord { FirstSeenUtc = DateTime.UtcNow, OrderId = 42, Result = "placed" };
            s.Save(DateTime.UtcNow);
            Assert.Equal(42, ScoutState.Load(path).Roles["EURUSD|H1|202611031400|M"].OrderId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The 4H candle that closes at the same moment as a 1H candle is fed first; one that closes later is not seen (Scout spec §4.4 I1).</summary>
    [Fact]
    public void Higher_timeframe_tag_never_looks_ahead()
    {
        var t = new ChartTracker(new ScoutChart("EURUSD", "H1", ScoutMode.Trade), new DetectorSettings());
        var h1 = Enumerable.Range(0, 8).Select(i => new Candle(T0.AddHours(i), 1, 1.1, 0.9, 1)).ToList();
        var h4 = new List<Candle> { new(T0, 1, 1.1, 0.9, 1), new(T0.AddHours(4), 1, 1.1, 0.9, 1) };

        t.Feed(h1.Take(4).ToList(), h4, T0.AddHours(4));
        Assert.Equal((T0.AddHours(3), T0), (t.Main.LastCandleUtc!.Value, t.Higher!.LastCandleUtc!.Value));

        t.Feed(h1, h4, T0.AddHours(8).AddSeconds(5));
        Assert.Equal((T0.AddHours(7), T0.AddHours(4)), (t.Main.LastCandleUtc!.Value, t.Higher.LastCandleUtc!.Value));
        Assert.Equal(8, t.Main.Count);
    }

    [Fact]
    public void The_candle_still_forming_is_skipped()
    {
        var t = new ChartTracker(new ScoutChart("EURUSD", "H1"), new DetectorSettings());
        t.Feed([new Candle(T0, 1, 1, 1, 1), new Candle(T0.AddHours(1), 1, 1, 1, 1)], [], T0.AddHours(1).AddMinutes(30));
        Assert.Equal(1, t.Main.Count);
    }
}
