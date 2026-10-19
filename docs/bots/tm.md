# TM — Trade Manually

The first trading bot. **You open every position by hand in cTrader; TM mirrors it and hedges both legs**, manages the hedges, protects each leg at 1R, and cleans up whatever you close. All trading decisions stay yours; TM does the hedge bookkeeping that is impossible by hand across many positions.

| Item | Value |
|---|---|
| Status | **Building** — step 1 (notifications) done 20-Oct-2026; step 2 next on the owner's go. Spec: plan v1.18 |
| Spec (authoritative) | [plan Section 10.9](../plan.md#109-tm--trade-manually-bot-v118) (decision 15; ownership exception in [5.3](../plan.md#53-one-framework-many-bots-v110)) |
| Code | `src/Trader.TM` (to be created), shared framework [`src/Trader.Core`](../../src/Trader.Core) |
| Accounts | IC Markets demo 49123893, Pepperstone demo 49124666 — each separately; demo only until Phase 5 |
| Symbols | Per account in TM's config; at first AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD |
| Bot name / labels | `TM` — `TM-<6hex>-P<mainPositionId>-MIR` (mirror), `-HMAIN`, `-HMIR` (hedges) |
| Takes over | Positions **without a label** on a **listed symbol** (your manual trades). Unlisted symbols and other bots' positions are never touched |
| Directions | Longs and shorts (for a long, everything is mirrored: hedge is a sell-stop below) |

## What it does (example: you open 0.5 lot short)

| When | TM does |
|---|---|
| You open | Opens a **mirror** 0.5 short at market and **2 × 0.5 hedge buy-stops** at H. **H** = your stop-loss if set, otherwise **15 % of the 3-day average daily range** (at least 5 × spread). Removes your stop-loss only after both hedges are confirmed. Copies your take-profit to the mirror |
| A hedge fills | Stop on the hedge **½R below its fill**; once price is **1R beyond the fill**, the stop moves to **fill + 1 pip** |
| A hedge closes | Re-hedges at H; or, after a +1 pip exit, 1R above the price, moved back to H when price allows |
| A leg is **1R in profit** | Stop-loss at break-even first, then cancels that leg's hedge. Trailing: off at first (per-symbol switch) |
| A leg loses **2R** / **3R** | Pop-up warning / closes that leg and its hedge |
| You close a leg (or its TP hits) | Removes that leg's hedge and stop; the other leg carries on |
| You set a new stop-loss or drag a hedge | Both hedges move there (protective side only; not while a hedge is filled) |
| You open another position | A separate trade with its own mirror and hedges — TM never adds or rotates on its own |
| TM was offline when you opened | Hedges only, no mirror, plus a warning |
| 10 min before NFP/CPI or the Friday close | Pop-up listing your open TM trades |

Risk limits (leverage 1:10, margin level 300 %) never block TM; it warns and follows the rules. Every leg always has a protective order on the server.

## Notifications

Through a reusable module in `Trader.Core` ([usage](../../src/Trader.Core/README.md#notifications-to-the-owner); config [`config/notifications.json`](../../config/notifications.json), live copy `~/tradebot/config/notifications.json`; test with `--test-notification`), usable by any bot: desktop pop-ups on Ubuntu now, more channels (e.g. Telegram) later as one new class; a config file decides which events go where. Logs always get everything.

## Build steps (proposed)

| Step | Content | Status |
|---|---|---|
| 1 | Notifications module in `Trader.Core` (desktop pop-ups, routing config) | **Done 20-Oct-2026** — pop-ups tested from a terminal and from a systemd user service; both seen on screen by the owner |
| 2 | Watcher: detect mains and TM's own legs; reconnect and reconcile; log what it *would* do, no orders | Next |
| 3 | Trade start: mirror, hedges, stop-loss removal, TP copy, ATR distance | — |
| 4 | Hedge management: B stop, armed exit, re-hedge, hedge level from the chart | — |
| 5 | Break-even at 1R, 2R/3R limits, clean-up when you close | — |
| 6 | Start-up and news: mains found late, news / Friday pop-ups; systemd service | — |

## Outputs (planned)

Per account and instance, as for the Audit Bot: `events.csv` and `actions.csv` (one row per order TM sends or changes: trade, leg, action, prices, slippage, latency, result).

## History

| Plan | Date | Change |
|---|---|---|
| v1.18 | 19 Oct 2026 | Spec agreed with the owner (20 questions) |
| v1.20 | 20 Oct 2026 | Step 1 done: notifications module |
