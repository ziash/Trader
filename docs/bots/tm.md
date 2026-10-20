# TM — Trade Manually

**You open every position by hand in cTrader; TM mirrors it and hedges both legs**, manages the hedges, protects each leg at 1R, and cleans up whatever you close. All trading decisions stay yours; TM does the hedge bookkeeping that is impossible by hand across many positions. A project of its own; it uses the [Open Code Pieces](../open-code-pieces.md).

| Item | Value |
|---|---|
| Status | **Working on demo** (run by hand) — steps 1–5 done 20-Oct-2026; step 6 (news / Friday / disconnect pop-ups, systemd service) next (§9) |
| Document version | v1.0 (20-Oct-2026) — history in §13 |
| Code | [`src/Trader.TM`](../../src/Trader.TM) (`Planner.cs` = the rules, `AccountManager.cs` = the loop, `TradeState.cs` = memory) |
| Tests | [`tests/Trader.TM.Tests`](../../tests/Trader.TM.Tests) — the rules run against a simulated broker |
| Config | [`config/tm.json`](../../config/tm.json) (accounts, symbols, rule values) — live copy `~/tradebot/config/tm.json`; pop-ups: `notifications.json` |
| Memory | `~/tradebot/run/tm/<broker>-<ctid>.json` (trades TM is managing; survives restarts) |
| Accounts | IC Markets demo 49123893, Pepperstone demo 49124666 — each separately; demo only |
| Symbols | AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD (per account in `tm.json`) |
| Bot name / labels | `TM` — `TM-<6hex>-P<mainPositionId>-MIR` (mirror), `-HMAIN`, `-HMIR` (hedges) |
| Takes over | Positions **without a label** on a **listed symbol** (your manual trades). Unlisted symbols and other bots' positions are never touched |
| Directions | Longs and shorts (for a long everything is mirrored: the hedge is a sell-stop below) |

## At a glance (example: you open 0.5 lot short)

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

*Agreed with the owner on 19-Oct-2026 (20 questions, one at a time). TM is the **first trading bot**.*

## 1. Idea

The owner opens every position **by hand in the cTrader app** (desktop, web or mobile). **TM** (`src/Trader.TM`, bot name `TM`) listens to the account; as soon as a new manual position appears on a listed symbol, TM **mirrors** it (opens a second position of the same side and size) and **hedges both**, then manages the hedges by the rules below. Closing trades is the owner's job; TM cleans up around whatever the owner closes. TM uses the [Open Code Pieces](../open-code-pieces.md) (`Trader.Core`): connection, reconnect, tokens, orders, logging, instance registry. **Demo accounts only** until a go-live is decided ([shared rules](../shared.md#4-safety-rules-for-every-bot)).

| Term | Meaning |
|---|---|
| Main | The owner's manual position (no label) |
| Mirror | TM's copy: same symbol, side and volume, opened at market just after the main is seen |
| Hedge | A pending stop order on the opposite side, one per short/long leg (main and mirror each have their own), same volume as its leg; becomes a position when it fills |
| E | A leg's own entry price (main and mirror differ by the fill difference) |
| H | Hedge level — the price of both hedge orders |
| 1R | Per leg: \|H − E\| |
| Fh | Price a hedge actually filled at |
| A | Arming distance = **1R** (Q8) |
| B | Hedge stop distance before arming = **½R** (Q10) |

Directions are mirrored: for a **short** the hedge is a **buy-stop above**; for a **long** a **sell-stop below** (Q2). Below, "up/above" is written for a short.

## 2. Scope (Q1, Q16, Q18)

- Accounts: **both demos**, IC Markets 49123893 and Pepperstone 49124666, each managed separately with its own symbol list in TM's config.
- Symbols: per account in the config; at first the Audit Bot's six (AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD).
- TM takes over **positions without a label on a listed symbol**. Positions on unlisted symbols stay fully the owner's. Positions with another bot's label (e.g. Audit Bot probes) are never touched.
- **Exception to the ownership rule ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)), owner's decision:** TM modifies the main (removes or copies its stop-loss and take-profit) — the only case in which a bot touches a position it did not open. TM never closes the main except under the 3R rule (§5).

## 3. Trade start (Q3, Q4, Q7)

| Step | Event | TM action |
|---|---|---|
| 1 | Owner opens a main, e.g. 0.5 lot short, with or without a stop-loss | Seen from the account's execution events (within about a second) |
| 2 | | **H:** the main's stop-loss if set; otherwise E + D, where **D = 15 % of the average daily range of the last 3 completed daily candles** (ATR(3) on D1 from the broker's own candles), at least **5 × the current spread**; if the history cannot be fetched, a fixed per-symbol fallback from the config (and a warning) |
| 3 | | Opens the **mirror** at market: same side and volume, with a server-side stop-loss at H so it is never unprotected |
| 4 | | Places **two hedge stop orders at H**, one per leg, each of that leg's volume |
| 5 | Both hedges confirmed by the server | Removes the stop-loss from the main and from the mirror |

- If the main has a **take-profit**, TM copies it to the mirror and follows later changes (Q14).
- If the mirror is rejected, the main still gets its hedge (and a warning). Risk limits (leverage 1:10, margin level 300 %, as in [Auto Bot §5](auto-bot.md#5-risk-guardrails)) do **not** block TM: it always mirrors and hedges; when a limit is broken it only warns (Q12, owner's decision: "Trade Manually — if I am opening, just follow the rules").
- Every further manual position is a **separate trade** with its own mirror and hedges; TM never opens adds of its own and never rotates (rules R4, R5 of [Auto Bot §7.3](auto-bot.md#73-lifecycle-rules) are not used by TM) (Q6).

## 4. Hedge management (Q8, Q9, Q10)

| Situation | TM action |
|---|---|
| A hedge fills at Fh | Puts a stop on the hedge at **Fh − B** (½R below its fill). The leg's loss is now frozen |
| Price reaches **Fh + A** (1R beyond the fill) | Moves the hedge's stop up to **X = Fh + 1 pip** (the hedge exit is armed; Scenario 1) |
| Hedge closed by its **B stop** (price fell back before arming; Scenario 2) | Places a new hedge stop order at the original **H**. Cost of the cycle ≈ B + spread |
| Hedge closed at **X** (the wick reversed) | Places a new hedge stop order **1R above the current price** (a buy-stop cannot sit below the price); as soon as price is back below the original H, moves it down to **H** |
| Owner sets a **new stop-loss** on the main, or **drags a hedge order** | Moves both legs' pending hedges to that level (and removes the stop-loss again). Accepted only on the protective side of the entry; ignored while a hedge of that trade is filled (Q15) |

Every leg always has a protective order on the server: the hedge order (pending), the hedge's own stop (filled), or a stop-loss (after 1R).

## 5. Protection, limits and closing (Q4, Q5, Q11, Q19)

| Rule | Trigger | TM action |
|---|---|---|
| Break-even (R2) | A leg is **1R in profit** from its own entry | **First** a server-side stop-loss at its entry on that leg, **then** cancels its hedge order. The leg can no longer lose |
| Trailing (R3) | Per-symbol switch, **off at first** | When switched on: [Auto Bot §7.3](auto-bot.md#73-lifecycle-rules) R3 (M5 lower highs). Needs the swing definition (open item) |
| Loss warning | A leg's loss (its own open P/L plus its hedges' realised and open P/L) reaches **2R** | Warning and pop-up |
| Loss limit | It reaches **3R** | Closes that leg and its hedge (cancels a pending one), accepting the loss; pop-up |
| Owner closes a leg (or its stop-loss / take-profit closes it) | | Removes that leg's hedge (cancel if pending, close if filled) and stop; the **other leg carries on**, still managed. Closing both = full exit; closing one = half off |

The 2R/3R values, 15 %, A, B and the spread floor are config values, to be tuned later by a backtest (§12).

## 6. Start-up, restarts and news (Q13, Q20)

- **TM's own orders** (mirrors, hedges) are found again after a restart from their labels and linked to their main by its `positionId` (labels below).
- **A main TM did not see opened** (TM was down): **hedges only, no mirror**, plus a warning; if price is already beyond H, the hedge opens at market at once.
- **News and weekend:** a pop-up **10 minutes before each NFP/CPI release** (`news-releases.json`, shared with the Audit Bot) and **10 minutes before the Friday close** (17:00 New York), listing the open TM trades. No automatic action; hedge fills beyond H are logged with their slippage (Scenario 5).

## 7. Labels, logs and notifications (Q17)

- **Labels** ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)): `TM-<6hex>-P<mainPositionId>-MIR` (mirror), `-HMAIN` (hedge of the main), `-HMIR` (hedge of the mirror), e.g. `TM-3f9a1c-P123456789-HMAIN`. The main's `positionId` in the label links every order to its trade across restarts; reconcile by bot name, as for every bot.
- **Logs:** per account and instance as for the Audit Bot ([Audit Bot §3](audit-bot.md#3-logs-on-disk)): `events.csv`, plus `actions.csv` — one row per order TM sends or changes. Columns: `time_utc`, `trade` (P + main's positionId), `symbol`, `role` (MAIN, MIR, HMAIN, HMIR), `action` (OpenMirror, PlaceHedge, MoveOrder, CancelOrder, SetProtection, ClosePosition), `side`, `lots`, `price_req`, `price_fill`, `latency_ms`, `result` (`ok` or the broker's code), `detail`.
- **Notifications:** a **reusable module in the [Open Code Pieces](../open-code-pieces.md#7-notifications)**: one call for every bot; channels plug in behind it — **desktop pop-ups** on Ubuntu now (`notify-send`), Telegram or others later as one new class; a config file routes events to channels. Logs (journal, `events.csv`) always get everything. Pop-ups for: 2R warning, 3R close, mirror skipped or rejected, hedge rejected, TM stopped or disconnected, news / Friday warnings.

- **Built (step 1):** `Trader.Core/Notifications` — `bot.Notify.Info/Warning/Critical(event, title, message, account)`; every call is logged (journal and `events.csv`), then queued to the channels whose filter in `~/tradebot/config/notifications.json` accepts it (`minLevel`, `bots`, `events`, `excludeEvents`, `enabled`; no file = desktop pop-ups from Warning). The same message within 60 s is sent once; a failing channel is logged once and never blocks the bot. Desktop = `notify-send`; Critical stays on screen. Any bot can test with `--test-notification`.

## 8. Accepted risks

| Risk | Effect | Bounded by |
|---|---|---|
| Price jumps past H (news, gaps) | Hedge fills far beyond H; one jump can pass 3R | Pop-up before news / Friday close; slippage logged |
| Chop around H | Each hedge cycle costs ≈ B + spread, or adds up to 1R after an armed exit | 2R warning, 3R limit |
| TM down when the owner opens a trade without a stop-loss | Main unprotected until TM is back | Set a stop-loss on manual orders; TM hedges at once on start |
| Risk limits not enforced | Many mirrored trades can exceed leverage 1:10 / margin level 300 % | Warning only (owner's choice) |
| Mirror entry differs from the main's | A fraction of a pip | Logged in `actions.csv` |

## 9. How TM is built, build steps and live findings

**How TM is built:** one loop per account, every second (at once after an order event). Each run reads the account's positions and orders (a reconcile, refreshed after every order event and at least every 30 s), and a rule function (`Planner`) compares them with the rules of §3–§5 and lists what to change: open the mirror, place / move / cancel a hedge, set or remove a stop-loss / take-profit, close a position, notify. Anything that did not work is simply planned again next second, so missed messages, reconnects and restarts heal themselves. What only TM knows — H, each leg's R, hedge results so far, steps done, warnings sent — is saved in `~/tradebot/run/tm/<broker>-<ctid>.json` after every change; TM orders found without a saved trade are rebuilt from the server. Config: `~/tradebot/config/tm.json` (accounts, symbols, rule values, fallback distances). Every order: a row in `actions.csv`.

**Found while testing live:**
- The API **can** change the stop-loss and take-profit of a manual position on both brokers (open item closed).
- In a fast market the price can pass a hedge's stop (Fh − B) before the stop is set; the broker refuses it (`TRADING_BAD_STOPS`). TM then closes the hedge at market — what the rule intends.
- A hedge level only a few spreads from the entry makes every hedge cycle cost about 1R (test: BTCUSD with 1R = $10 against a ~$5 spread reached −2R in a minute). TM warns (`TightHedge`) when 1R is under 5 × the spread.
- TM recognises the orders of an earlier TM copy (labels carry the bot name; reconcile by bot name, [Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)).

Each step only on the owner's go; each tested on the demo accounts before the next.

| Step | Content | Status |
|---|---|---|
| 1 | Notifications module in `Trader.Core` (§7) | **Done 20-Oct-2026** — pop-ups tested from a terminal and from a systemd user service; both seen on screen by the owner |
| 2–5 | *(owner: "just implement it — we are on demo")* Watcher, trade start, hedge management, break-even, 2R/3R limits, clean-up — built together as one engine (`src/Trader.TM`) | **Done 20-Oct-2026** — 22 scenario tests on a simulated broker; live on both demos: take-over with and without stop-loss (ATR distance), long and short, TP copy, hedge level moved by a new stop-loss, owner closing one leg, restart with saved state, hedge fill → hedge stop → re-hedge → 2R warning (BTCUSD); both accounts flat afterwards |
| 6 | News and Friday pop-ups, disconnect pop-ups (§6); systemd service and deploy script | Next |

## 10. Run and operate

Until step 6 makes it a service, run it in a terminal (Ctrl+C stops it; positions keep their hedges and stops on the server):

```bash
dotnet run --project src/Trader.TM             # from ~/code/Trader
dotnet run --project src/Trader.TM -- --help
```

Then open positions in cTrader on a listed symbol, as usual. Pop-ups show warnings (2R, mirror skipped, refused orders, tight hedge); everything is in the console and the logs.

### Outputs

```
~/tradebot/logs/<broker>/<ctid>/<instanceId>/<yyyy-MM-dd>/
  events.csv    take-overs, hedge fills, arming, break-even, warnings, errors
  actions.csv   one row per order TM sends or changes: trade, role, action, side, lots, prices, latency, result
~/tradebot/run/tm/<broker>-<ctid>.json   trades being managed
```

## 11. Decisions

| # | Decision | Why |
|---|---|---|
| 1 | TM is the first trading bot: the owner opens every position by hand in cTrader; TM mirrors it and hedges both legs, manages the hedges, protects at 1R, and cleans up when the owner closes (agreed 19-Oct-2026, 20 questions, §1–§8) | The owner keeps every trading decision; the bot does the hedge bookkeeping that is impossible by hand across many positions |
| 2 | Longs and shorts; both demo accounts; the Audit Bot's six symbols | Free to trade either way; symbols with audit data |
| 3 | Hedge level = your stop-loss, else 15 % of the 3-day average daily range (at least 5 × spread) | You set the level on the chart when you want; a forgotten stop-loss still gets a hedge |
| 4 | Risk limits never block TM; it warns and follows the rules (owner: "Trade Manually — if I am opening, just follow the rules") | Your trades are your decision |
| 5 | Trailing off at first; you close | "Trade Manually"; the swing definition is still open |
| 6 | Built without a dry-run stage (owner: "we are on demo already") | Speed; demo money |
| 7 | One loop per account that compares the server with the rules every second, plus a saved state file (§9) | Missed messages, reconnects and restarts heal themselves |

## 12. Open items

- **Step 6:** pop-ups 10 min before NFP/CPI and the Friday close; a pop-up when the connection is down; systemd service and deploy script.
- Parameters to tune with a backtest: 15 % of the 3-day range, spread floor 5 ×, A = 1R, B = ½R, 2R / 3R limits.
- Trailing (R3) needs the exact M5 swing definition ("last lower high") and the stop buffer — the same open question as [Auto Bot §9](auto-bot.md#9-open-questions).
- A partial close of a main does not yet resize its pending hedge (the hedge keeps the original volume).

## 13. History

| Version | Date | Change |
|---|---|---|
| — | 19 Oct 2026 | (as part of the old single plan, v1.18) Spec agreed with the owner, 20 questions one at a time |
| — | 20 Oct 2026 | (old plan v1.20–v1.22) Step 1: notifications ([Open Code Pieces](../open-code-pieces.md)); steps 2–5: the trading engine, tested live on both demos |
| v1.0 | 20 Oct 2026 | Own project document, split out of the old plan ([archive](../archive/plan-v1.22.md), Section 10.9) and the former TM page. Content unchanged |
