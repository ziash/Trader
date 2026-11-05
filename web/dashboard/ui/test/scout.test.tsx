import { describe as group, expect, it } from "vitest";
import { chartUrl, distance, market, orderState, tfLabel, usd } from "@/screens/ScoutScreen";
import type { ScoutOrder } from "@/api/types";

const order = (o: Partial<ScoutOrder>): ScoutOrder => ({
  order_id: "1", symbol: "GBPJPY", timeframe: "H1", setup_key: "GBPJPY-H1-202611041400", role: "M", side: "Buy",
  state: "pending", state_utc: "2026-11-05 09:41:00", ...o,
});

group("Scout page", () => {
  it("measures the stop-loss distance with the level's digits", () => {
    expect(distance("110.844", "110.966")).toBe("0.122");
    expect(distance("26229.20", "26418.70")).toBe("189.50");
    expect(distance(null, "1")).toBe("—");
  });

  it("says where an order stands in words, not colour alone", () => {
    expect(orderState(order({ state: "filled" }), "utc")).toEqual({ tone: "accent", text: "Filled 09:41 · managed by TM" });
    expect(orderState(order({ state: "closed", net_usd: -8.15 }), "utc")).toEqual({ tone: "red", text: "Closed −$8.15" });
    expect(orderState(order({ state: "cancelled", reason: "trend turned" }), "utc").text).toBe("Cancelled 09:41 · trend turned");
    expect(orderState(null, "utc").text).toBe("No order");
  });

  it("names markets, timeframes and money as the design does", () => {
    expect([market("XAUUSD"), market("EURJPY"), market("US500"), market("BTCUSD"), market("XNGUSD")]).toEqual(["Metals", "Forex", "Indices", "Crypto", "Energies"]);
    expect([tfLabel("H1"), tfLabel("D1")]).toEqual(["1H", "1D"]);
    expect([usd(12.4), usd(0), usd(null)]).toEqual(["+$12.40", "+$0.00", ""]);
  });

  it("asks the API for pictures, never a path of its own", () => {
    expect(chartUrl("brokerA/1/Scout-a/2026-11-05/charts/X.svg")).toBe("/api/scout/chart?file=brokerA%2F1%2FScout-a%2F2026-11-05%2Fcharts%2FX.svg");
  });
});
