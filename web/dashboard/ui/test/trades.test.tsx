import { describe as group, expect, it } from "vitest";
import { levelTone, money } from "@/screens/AccountsScreen";
import { tradeChartUrl } from "@/screens/TradesScreen";

group("Accounts and Trades pages", () => {
  it("colours the margin level by TM's rules and says why in words", () => {
    expect(levelTone(87, 100, 300)).toEqual({ tone: "red", text: "Below 100 % · TM closes trades (margin rescue)" });
    expect(levelTone(122, 100, 300).tone).toBe("amber");
    expect(levelTone(539, 100, 300).tone).toBe("green");
    expect(levelTone(null, 100, 300).text).toBe("No positions");
  });

  it("writes money with thousands and a real minus", () => {
    expect([money(3394.35), money(-368.18), money(null)]).toEqual(["$3,394.35", "−$368.18", "—"]);
  });

  it("asks the API for trade pictures, never a path of its own", () => {
    expect(tradeChartUrl("brokerA/49123893/Dashboard/2026-11-05/charts/trade-P692692856.svg"))
      .toBe("/api/trades/chart?file=brokerA%2F49123893%2FDashboard%2F2026-11-05%2Fcharts%2Ftrade-P692692856.svg");
  });
});
