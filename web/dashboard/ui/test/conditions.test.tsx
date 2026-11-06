import { describe as group, expect, it } from "vitest";
import { marginFor, whatIf } from "@/screens/ConditionsScreen";
import { periodLabel } from "@/screens/AuditScreen";
import type { ConditionsRow } from "@/api/types";

// EURUSD at IC Markets on 06-Nov-2026 (dashboard.md §2.5).
const eurusd = {
  symbol: "EURUSD", market_open: true, account_leverage: "1:500", symbol_leverage: "1:1000", tiers: ["1:1000"], lot_units: 100000, lot_asset: "EUR",
  min_lots: 0.01, step_lots: 0.01, max_lots: 1000, margin_buy_per_lot_usd: 222.99, margin_sell_per_lot_usd: 222.99, pip_size: 0.0001, digits: 5,
  pip_value_per_lot_usd: 10, bid: 1.11493, ask: 1.11494, spread_pips: 0.1, spread_per_lot_usd: 1, commission_per_lot_usd: 6.69, commission_type: "UsdPerMillionUsd",
  commission_rate: 30, swap_type: "Pips", swap_long: -0.81, swap_short: 0.14, swap_long_per_lot_usd: -8.1, swap_short_per_lot_usd: 1.4, triple_swap_day: "Wednesday",
  largest_lots_now: 1.24, margin: [{ lots: 1, buy: 222.99, sell: 222.99 }, { lots: 10, buy: 2229.9, sell: 2229.9 }],
} as ConditionsRow;

group("Trading conditions and Audit pages", () => {
  it("reads the margin off the broker's curve", () => {
    expect(marginFor(eurusd.margin, 0.5, "buy")).toBeCloseTo(111.495, 3);
    expect(marginFor(eurusd.margin, 20, "sell")).toBeCloseTo(4459.8, 1);   // past the last point: straight on
    expect(marginFor([], 1, "buy")).toBeNull();
  });

  it("works out the what-if figures for a size and a stop-loss in pips or in price", () => {
    const w = whatIf(eurusd, 0.3, "buy", 20, "pips");
    expect([w.pipValue, w.commission, w.swapNight, w.risk]).toEqual([3, expect.closeTo(2.007, 3), expect.closeTo(-2.43, 3), 60]);
    expect(whatIf(eurusd, 0.3, "sell", 0.0020, "price").risk).toBeCloseTo(60, 6);
    expect(whatIf(eurusd, 0.3, "sell", 0, "pips").risk).toBeNull();
  });

  it("names the audit periods in words", () => {
    expect(["day", "offpeak", "LondonOpen", "NewsRelease"].map(periodLabel)).toEqual(["Whole day", "Off-peak", "London open", "News Release"]);
  });
});
