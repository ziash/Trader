using Trader.Core.Csv;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;
using Trader.DashboardExport;

namespace Trader.DashboardExport.Tests;

/// <summary>The Trading conditions page's figures (dashboard.md §2.5, build step 5b), with the brokers' values of 06-Nov-2026.</summary>
public sealed class ConditionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-cond-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProtoOASymbol Eurusd(ProtoOACommissionType type, long rate) => new()
    {
        SymbolId = 1, Digits = 5, PipPosition = 4, LotSize = 10_000_000, MinVolume = 100_000, StepVolume = 100_000, MaxVolume = 10_000_000_000,
        CommissionType = type, PreciseTradingCommissionRate = rate, SwapCalculationType = ProtoOASwapCalculationType.Pips,
        SwapLong = -0.81, SwapShort = 0.14, SwapRollover3Days = ProtoOADayOfWeek.Wednesday, LeverageId = 52531,
    };

    private static readonly Quote Q = new(1, 1, 1.11493, 1.11494, DateTime.UtcNow);
    private static ExpectedMargin[] Linear(double perLot, params long[] volumes) => volumes.Select(v => new ExpectedMargin(v, perLot * v / 10_000_000, perLot * v / 10_000_000)).ToArray();

    [Fact]
    public void Ic_markets_eurusd_per_lot_in_usd()
    {
        var lev = new ProtoOADynamicLeverage { LeverageId = 52531 };
        lev.Tiers.Add(new ProtoOADynamicLeverageTier { Volume = 100_000_000, Leverage = 100_000 });
        var r = ConditionsMath.Compute(Eurusd(ProtoOACommissionType.UsdPerMillionUsd, 3_000_000_000), "EURUSD", "EUR", "USD", 50_000, lev, Q, Q.Mid, 1,
            Linear(222.99, 10_000_000, 100_000_000, 1_000_000_000, 10_000_000_000), freeMargin: 276.74);
        Assert.Equal(("1:500", "1:1000", 100_000, "EUR"), (r.AccountLeverage, r.SymbolLeverage, r.LotUnits, r.LotAsset));
        Assert.Equal((0.01, 0.01, 1000), (r.MinLots, r.StepLots, r.MaxLots));
        Assert.Equal(10, r.PipValuePerLotUsd!.Value, 6);
        Assert.Equal(0.1, r.SpreadPips!.Value, 6);
        Assert.Equal(6.69, r.CommissionPerLotUsd!.Value, 2);      // $30 per million per side, open and close, on ≈ $111,494
        Assert.Equal((-8.10, 1.40), (Math.Round(r.SwapLongPerLotUsd!.Value, 2), Math.Round(r.SwapShortPerLotUsd!.Value, 2)));
        Assert.Equal("Wednesday", r.TripleSwapDay);
        Assert.Equal(1.24, r.LargestLotsNow);                       // $276.74 ÷ $222.99, down to the 0.01 step
        Assert.Equal(222.99, r.MarginBuyPerLotUsd);
        Assert.True(r.MarketOpen);
    }

    [Fact]
    public void Pepperstone_charges_per_lot_and_jpy_pairs_convert_to_usd()
    {
        var jpy = Eurusd(ProtoOACommissionType.UsdPerLot, 300_000_000);
        jpy.PipPosition = 2;
        jpy.Digits = 3;
        var r = ConditionsMath.Compute(jpy, "GBPJPY", "GBP", "JPY", 20_000, null, new Quote(1, 1, 211.559, 211.564, DateTime.UtcNow), 211.56, 1 / 158.245, [], 1000);
        Assert.Equal(6.00, r.CommissionPerLotUsd!.Value, 6);       // $3 per lot per side
        Assert.Equal(6.3193, r.PipValuePerLotUsd!.Value, 4);       // 1,000 JPY per pip ÷ 158.245
        Assert.Equal(3.16, r.SpreadPerLotUsd!.Value, 2);
        Assert.Equal("", r.SymbolLeverage);
        Assert.Equal(0, r.LargestLotsNow);                         // no margin figures: nothing claimed
    }

    [Fact]
    public void Crypto_swap_is_a_yearly_percentage_and_a_closed_market_has_no_spread()
    {
        var btc = new ProtoOASymbol
        {
            SymbolId = 2, Digits = 2, PipPosition = 2, LotSize = 100, MinVolume = 1, StepVolume = 1, MaxVolume = 1000,
            CommissionType = ProtoOACommissionType.UsdPerMillionUsd, SwapCalculationType = ProtoOASwapCalculationType.Percentage, SwapLong = -20, SwapShort = 0,
        };
        var r = ConditionsMath.Compute(btc, "BTCUSD", "BTC", "USD", 50_000, null, null, 75_868, 1, [], 0);
        Assert.Equal(-42.15, r.SwapLongPerLotUsd!.Value, 2);       // 1 BTC × $75,868 × 20 % ÷ 360
        Assert.Equal(0, r.CommissionPerLotUsd);
        Assert.False(r.MarketOpen);
        Assert.Null(r.SpreadPips);
    }

    [Fact]
    public void The_largest_size_follows_the_brokers_margin_curve_step_minimum_and_maximum()
    {
        MarginPoint P(double lots, double m) => new(lots, m, m * 0.99);
        // Leverage falls after 10 lots: the second stretch costs twice as much per lot.
        MarginPoint[] curve = [P(1, 100), P(10, 1000), P(100, 19000)];
        Assert.Equal(5.55, ConditionsMath.LargestLots(curve, 555, 0.01, 0.01, 1000));
        Assert.Equal(15, ConditionsMath.LargestLots(curve, 2000, 0.01, 0.01, 1000));
        Assert.Equal(200, ConditionsMath.LargestLots(curve, 38000, 0.01, 0.01, 1000)); // past the last point: straight on
        Assert.Equal(150, ConditionsMath.LargestLots(curve, 38000, 0.01, 0.01, 150));  // capped by the largest order
        Assert.Equal(5.5, ConditionsMath.LargestLots(curve, 555, 0.5, 0.5, 1000));     // XNGUSD: 0.5-lot steps
        Assert.Equal(0, ConditionsMath.LargestLots(curve, 40, 0.5, 0.5, 1000));       // not even the smallest order fits
        Assert.Equal(0, ConditionsMath.LargestLots(curve, -180.09, 0.01, 0.01, 1000)); // IC on 06-Nov: negative free margin
        Assert.Equal([10_000_000L, 100_000_000, 1_000_000_000, 10_000_000_000], ConditionsMath.MarginVolumes(Eurusd(ProtoOACommissionType.UsdPerLot, 0)));
    }

    [Fact]
    public void A_symbol_gets_a_row_once_a_day_and_again_only_when_a_fixed_value_changes()
    {
        var file = Path.Combine(_dir, "Dashboard/2026-11-06/conditions.csv");
        var now = new DateTime(2026, 11, 6, 17, 0, 0, DateTimeKind.Utc);
        ConditionsRow Row(double swapLong, double bid)
        {
            var f = Eurusd(ProtoOACommissionType.UsdPerMillionUsd, 3_000_000_000);
            f.SwapLong = swapLong;
            return ConditionsMath.Compute(f, "EURUSD", "EUR", "USD", 50_000, null, new Quote(1, 1, bid, bid + 0.00001, now), bid, 1, [], 100);
        }
        Assert.Equal(1, ConditionsMath.AppendChanged(file, [Row(-0.81, 1.1149)], now));
        Assert.Equal(0, ConditionsMath.AppendChanged(file, [Row(-0.81, 1.1200)], now.AddMinutes(30))); // only the price moved
        Assert.Equal(1, ConditionsMath.AppendChanged(file, [Row(-0.95, 1.1200)], now.AddHours(1)));   // the swap rate changed
        var t = CsvTable.Read(file);
        Assert.Equal(ConditionsRow.Header, t.Header);
        Assert.Equal(["-0.81", "-0.95"], t.Rows.Select(r => r["swap_long"]));
    }

    [Fact]
    public void The_site_gets_the_catalogue_and_the_configured_charts_never_more()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        Assert.Null(ScoutOptions.Build(Path.Combine(_dir, "config")));            // no catalogue: no controls on the site
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/scout-controls.json")), Path.Combine(_dir, "config/scout-controls.json"));
        File.WriteAllText(Path.Combine(_dir, "config/scout.json"), """
            { "accounts": [ { "broker": "brokerA", "accountId": 49123893, "charts": [ { "symbol": "EURUSD", "timeframe": "H1", "mode": "trade" } ] } ],
              "lots": 0.1 }
            """);
        var o = ScoutOptions.Build(Path.Combine(_dir, "config"))!;
        Assert.Equal(4, o["catalogue"]!["options"]!.AsArray().Count);
        var chart = o["accounts"]!["brokerA-49123893"]![0]!;
        Assert.Equal(("EURUSD", "H1", "trade"), (chart["symbol"]!.GetValue<string>(), chart["timeframe"]!.GetValue<string>(), chart["mode"]!.GetValue<string>()));
        Assert.DoesNotContain("lots", o.ToJsonString());                         // only what the controls need
    }

    [Fact]
    public void The_rows_are_the_symbols_scout_and_tm_work_on()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config/scout.json"), """
            { // comments allowed
              "accounts": [ { "broker": "brokerA", "accountId": 49123893, "charts": [ { "symbol": "EURUSD", "timeframe": "H1" }, { "symbol": "SOLUSD" } ] } ] }
            """);
        File.WriteAllText(Path.Combine(_dir, "config/tm.json"), """
            { "accounts": [ { "broker": "brokerA", "accountId": 49123893, "symbols": ["EURUSD", "EURGBP"] },
                            { "broker": "brokerB", "accountId": 49124666, "symbols": ["BTCUSD"] } ] }
            """);
        var m = ConditionsMath.ConfiguredSymbols(Path.Combine(_dir, "config"));
        Assert.Equal(["EURUSD", "SOLUSD", "EURGBP"], m["brokerA-49123893"]);
        Assert.Equal(["BTCUSD"], m["brokerB-49124666"]);
    }
}
