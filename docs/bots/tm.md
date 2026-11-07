# TM — Trade Manually

**You open every position by hand in cTrader; TM mirrors it and hedges both legs**, manages the hedges, protects each leg at 1R, and cleans up whatever you close. All trading decisions stay yours; TM does the hedge bookkeeping that is impossible by hand across many positions. A project of its own; it uses the [Open Code Pieces](../open-code-pieces.md).

**Related:** [Scout](scout/scout.md) (TM manages its orders) · [Dashboard](../dashboard/dashboard.md) (TM's trades) · [issue log](../issue-log.md) · [Rewind](https://github.com/ziash/Rewind) (replayed prices) · [all projects](../../README.md#projects)

| Item | Value |
|---|---|
| Status | **Built** — steps 1–6 done 20-Oct-2026, tested on both demos; runs as the systemd user service `trader-tm` (§10); from 03-Nov-2026 also manages [Scout](scout/scout.md)'s orders (`takeOver`, decision 12); from v1.9 reads the account's margin and acts on it (§5.1, [issue-log.md #1](../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)) |
| Document version | v1.14 (08-Nov-2026) — history in §13 |
| Code | [`src/Trader.TM`](../../src/Trader.TM) (`Planner.cs` = the rules, `AccountManager.cs` = the loop, `TradeState.cs` = memory, `Alerts.cs` = news / Friday / disconnect pop-ups) |
| Tests | [`tests/Trader.TM.Tests`](../../tests/Trader.TM.Tests) — the rules run against a simulated broker |
| Config | [`config/tm.json`](../../config/tm.json) (accounts, symbols, rule values, pop-up timing) — live copy `~/tradebot/config/tm.json`; news dates: `news-releases.json` (shared with the Audit Bot); pop-ups: `notifications.json` |
| Memory | `~/tradebot/run/tm/<broker>-<ctid>.json` (trades TM is managing; survives restarts) |
| Accounts | IC Markets demo 49123893, Pepperstone demo 49124666 — each separately; demo only |
| Symbols | Per account in `tm.json`. IC Markets (brokerA), from 04-Nov-2026: Scout's 18 ([scout.md](scout/scout.md) §7) plus EURGBP. Pepperstone (brokerB), from 07-Nov-2026: the same 19 (Scout moved there, [scout.md](scout/scout.md) §7), five under Pepperstone's names via `symbolNames` (XTIUSD = SpotCrude, XBRUSD = SpotBrent, XNGUSD = NatGas, USTEC = NAS100, DE40 = GER40); before: AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD |
| Bot name / labels | `TM` — `TM-<6hex>-P<mainPositionId>-MIR` (mirror), `-HMAIN`, `-HMIR` (hedges); `TM-<6hex>-O<yourOrderId>-MIR` (mirror order of your pending order) |
| Takes over | Positions and pending orders whose **label is in `takeOver`** (§2; default: **without a label** = your manual trades: market entries, and buy/sell limit and stop orders) on a **listed symbol**. Unlisted symbols and positions with other labels are never touched |
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
| You place a pending order (buy/sell limit, buy/sell stop) | A **mirror order** of the same type, side, lots and price beside it, with a stop-loss at H and your take-profit; it follows your changes and is cancelled with yours. Hedges once both fill (they cannot be placed before: §3.1) |
| Your main has a **trailing stop** | Removed with the stop-loss while hedged (the hedge protects); at 1R both legs' break-even stops are set as **trailing stops** on the server. Switching trailing off or on in cTrader afterwards is followed on both legs |
| TM was offline when you opened | Hedges only, no mirror, plus a warning |
| 10 min before NFP/CPI or the Friday close | Pop-up listing your open TM trades |

Risk limits (leverage 1:10, margin level 300 %) never block TM; it warns and follows the rules. Every leg always has a protective order on the server.

*Agreed with the owner on 19-Oct-2026 (20 questions, one at a time). TM is the **first trading bot**.*

## 1. Idea

The owner opens every position **by hand in the cTrader app** (desktop, web or mobile). **TM** (`src/Trader.TM`, bot name `TM`) listens to the account; as soon as a new manual position appears on a listed symbol, TM **mirrors** it (opens a second position of the same side and size) and **hedges both**, then manages the hedges by the rules below. Closing trades is the owner's job; TM cleans up around whatever the owner closes. TM uses the [Open Code Pieces](../open-code-pieces.md) (`Trader.Core`): connection, reconnect, tokens, orders, logging, instance registry. **Demo accounts only** until a go-live is decided ([shared rules](../shared.md#4-safety-rules-for-every-bot)).

| Term | Meaning |
|---|---|
| Main | The position TM takes over: the owner's manual position (no label), or one whose label is in `takeOver` (§2) |
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
- TM takes over **positions whose label is in the take-over list, on a listed symbol**. Positions on unlisted symbols stay fully the owner's; positions with any other label (e.g. Audit Bot probes, while `AuditBot-` is not in the list) are never touched.
- **The owner's pending orders** (buy-limit, sell-limit, buy-stop, sell-stop, label in the list) get a **mirror order** (§3.1); TM never changes the owner's order itself.

### 2.1 Take-over list (v1.6, owner, 24-Oct-2026)

`"takeOver"` in `tm.json` lists the labels TM takes over; positions and pending orders are matched the same way.

| Entry | Matches | Example |
|---|---|---|
| `""` | No label — the owner's manual trades | |
| any other text | Labels that **start with** it (case-sensitive). Bot labels end with an instance ID that changes on every restart, so an exact match would stop working | `"AuditBot-"` matches `AuditBot-77fbd8` |

| You want | `"takeOver"` |
|---|---|
| Only manual trades (the default when the entry is missing) | `[""]` |
| Manual trades and ABC | `["", "ABC"]` |
| Only ABC | `["ABC"]` |

- TM refuses to start if the list is empty or an entry would match TM's own labels (`TM-…`), so TM never takes over its own mirrors and hedges. It logs the list at start (`Takes over: …`).
- A main with a label is handled exactly like a manual one (§3–§5): mirror, hedges, its stop-loss replaced by the backstop once hedged (§5.1), TP copied, 3R close. This is the ownership-rule exception below, extended to the listed labels.
- **Live setting: `[""]`** (unchanged behaviour). Adding another bot's label is a separate decision: that bot's own closing (e.g. Audit Bot probes close within ~1 s) then interacts with TM's rules (§12).
- **Exception to the ownership rule ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)), owner's decision:** TM modifies the main (also a main with a listed label, §2.1) (removes or copies its stop-loss and take-profit) — the only case in which a bot touches a position it did not open. TM never closes the main except under the 3R rule (§5).

## 3. Trade start (Q3, Q4, Q7)

| Step | Event | TM action |
|---|---|---|
| 1 | Owner opens a main, e.g. 0.5 lot short, with or without a stop-loss | Seen from the account's execution events (within about a second) |
| 2 | | **H:** the main's stop-loss if set; otherwise E + D, where **D = 15 % of the average daily range of the last 3 completed daily candles** (ATR(3) on D1 from the broker's own candles), at least **5 × the current spread**; if the history cannot be fetched, a fixed per-symbol fallback from the config (and a warning) |
| 3 | | Opens the **mirror** at market: same side and volume, with a server-side stop-loss at H so it is never unprotected |
| 4 | | Places **two hedge stop orders at H**, one per leg, each of that leg's volume |
| 5 | Both hedges confirmed by the server | Replaces the stop-loss of the main and of the mirror by the **backstop** at the 3R loss limit (§5.1; until v1.8: removed) |

- If the main has a **take-profit**, TM copies it to the mirror and follows later changes (Q14).
- If the mirror is rejected, the main still gets its hedge (and a warning). Risk limits (leverage 1:10, as in [Auto Bot §5](auto-bot.md#5-risk-guardrails)) do **not** block TM (Q12, owner's decision: "Trade Manually — if I am opening, just follow the rules"); **the margin level does since v1.9**: below 300 % no mirror, below 100 % the margin rescue (§5.1, decision 13).
- Every further manual position is a **separate trade** with its own mirror and hedges; TM never opens adds of its own and never rotates (rules R4, R5 of [Auto Bot §7.3](auto-bot.md#73-lifecycle-rules) are not used by TM) (Q6).

### 3.1 Entry by a pending order (owner, 22-Oct-2026)

| Step | Event | TM action |
|---|---|---|
| 1 | Owner places a buy/sell **limit** or **stop** order, e.g. 0.5 lot sell-stop at 1.0920 with SL 1.0935 | **H** = the order's stop-loss, otherwise the order price ± D (§3 step 2, from the order price) |
| 2 | | Places the **mirror order**: same type, side, volume and price, **stop-loss at H**, the owner's take-profit; label `TM-<6hex>-O<orderId>-MIR` |
| 3 | Owner moves the order, its stop-loss or take-profit | The mirror order follows; without a stop-loss H keeps its distance from the order price |
| 4 | Owner cancels the order (or it expires) | Cancels the mirror order; the trade ends |
| 5 | Both orders fill (same type and price: the same tick) | The trade starts as in §3 from step 4: both **hedges at H**, then the stop-losses removed; no mirror at market |

- **No hedges before the fill.** A hedge stop at H often cannot be placed while the entry waits — for a sell-stop entry below the price, H lies below the price too, where the broker refuses a buy-stop — and one that could be placed might fill without the entry. Until the hedges are in (about a second after the fill), the server protects both legs: the main by the owner's own stop-loss (none if the order has none, as for a market entry), the mirror by its stop-loss at H.
- If the mirror order was refused, or did not fill with the owner's order (it is then cancelled), the mirror opens **at market** after the fill, as in §3. If the order filled while TM was down (more than `mirrorWithinSeconds` ago) and its mirror order did not fill: hedges only, no mirror (§6).
- TM finds the main from the fill's execution event; after a restart from its look (same symbol, side and volume, opened after TM saw the order). A mirror order left over without its trade (state file lost) is cancelled.

## 4. Hedge management (Q8, Q9, Q10)

| Situation | TM action |
|---|---|
| A hedge fills at Fh | Puts a stop on the hedge at **Fh − B** (½R below its fill). The leg's loss is now frozen |
| Price reaches **Fh + A** (1R beyond the fill) | Moves the hedge's stop up to **X = Fh + 1 pip** (the hedge exit is armed; Scenario 1) |
| Hedge closed by its **B stop** (price fell back before arming; Scenario 2) | Places a new hedge stop order at the original **H**. Cost of the cycle ≈ B + spread |
| Hedge closed at **X** (the wick reversed) | Places a new hedge stop order **1R above the current price** (a buy-stop cannot sit below the price); as soon as price is back below the original H, moves it down to **H** |
| Owner sets a **new stop-loss** on the main, or **drags a hedge order** | Moves both legs' pending hedges to that level (and replaces the stop-loss by the new backstop, §5.1). Accepted only on the protective side of the entry; ignored while a hedge of that trade is filled (Q15) |

Every leg always has a protective order on the server: the hedge order (pending), the hedge's own stop (filled), or a stop-loss (after 1R).

## 5. Protection, limits and closing (Q4, Q5, Q11, Q19)

| Rule | Trigger | TM action |
|---|---|---|
| Break-even (R2) | A leg is **1R in profit** from its own entry | **First** a server-side stop-loss at its entry on that leg, **then** cancels its hedge order. The leg can no longer lose |
| Owner's trailing stop | The main has a trailing stop (set with the order, on the position, or switched on later) | While the leg is hedged it is replaced like any stop-loss (§5.1) (a trailing stop at H would close the main exactly when its hedge fills). TM remembers it: the break-even stops of **both legs** are then set as **server-side trailing stops** (`trailingStopLoss = true`), which trail about 1R behind the best price. TM follows the main's switch whenever the main has a stop-loss: switched off (or on) after break-even, the mirror follows. Owner's decision 22-Oct-2026 |
| Trailing (R3) | Per-symbol switch, **off at first** | When switched on: [Auto Bot §7.3](auto-bot.md#73-lifecycle-rules) R3 (M5 lower highs). Needs the swing definition (open item) |
| Loss warning | A leg's loss (its own open P/L plus its hedges' realised and open P/L) reaches **2R** | Warning and pop-up |
| Loss limit | It reaches **3R** | Closes that leg and its hedge (cancels a pending one), accepting the loss; pop-up |
| Owner closes a leg (or its stop-loss / take-profit closes it) | | Removes that leg's hedge (cancel if pending, close if filled) and stop; the **other leg carries on**, still managed. Closing both = full exit; closing one = half off |

The 2R/3R values, 15 %, A, B and the spread floor are config values, to be tuned later by a backtest (§12).

### 5.1 Margin and the backstop (v1.9, 06-Nov-2026)

After the IC Markets margin call of 06-Nov-2026 ([issue-log.md #1](../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)), TM reads the account's balance, equity and margin with every full reconcile (every 30 s; read-only, [Open Code Pieces](../open-code-pieces.md) v1.9) and follows these rules:

| Rule | Trigger | TM action |
|---|---|---|
| **Backstop** | A leg whose own stop-loss TM has replaced (a hedge order was placed), while **no hedge is filled** | A server-side stop-loss at the **3R loss limit** (entry ∓ `closeR` × 1R). The leg then always has a stop on the server, even if a hedge is refused or TM is down. A filled hedge replaces it (the loss is frozen); it comes back when the hedge closes. If the price is already past it, the 3R rule closes the leg. TM recognises its own backstop, so it is never read as the owner moving the hedge level, nor as switching trailing off. Replaces "the stop-loss is removed once hedged" (v1.0) |
| **No money for a hedge** | The broker refuses a hedge with `NOT_ENOUGH_MONEY` | A stop hedge is sent again after **one minute**, not every second; the backstop protects meanwhile. **If the price is already past H** (a market hedge would be needed and was refused), TM **closes the leg at market** (Critical pop-up `HedgeNoMoney`). That freezes the loss as the hedge would, and frees margin instead of needing it |
| **No mirror** | Margin level below **`marginWarnLevel`** (300 %) when a trade starts | Hedges only, no mirror and no mirror order (`MirrorSkipped`): a mirror doubles the exposure |
| **Margin rescue** | Margin level below **`marginRescueLevel`** (100 %: IC's margin call; it stops accounts out at 50 %) | TM closes **one whole trade** (main, mirror, hedges, and cancels its hedge orders), the one whose positions hold the most margin. It closes the next only after a fresh account status, at least 20 s later, still shows the level too low (Critical pop-up `MarginRescue`). TM chooses before the broker's stop-out closes positions in its own order |
| **Seeing it** | — | An `AccountStatus` line in `events.csv` every 15 min (balance, equity, margin used / free, level); the `MarginLow` pop-up at most once an hour below 300 % (Critical below 100 %). A refused action pops up once per 10 minutes, not on every retry (every refusal stays a row in `actions.csv`) |

If the broker does not answer the status (e.g. a replay broker without it), the margin rules wait and TM asks again after 5 minutes. Hedging, the backstop and the 3R rule work without it.

### 5.2 Spread guard and trigger on the other side (planned, 08-Nov-2026)

**Status: live since 07-Nov-2026 19:39 UTC** (owner's OK; build `TM-63cc45c`, instance `TM-03d420`). At the restart TM gave the open trades' existing hedge orders and its stops the new trigger once, at unchanged prices: **34 changes, all accepted** (IC Markets: 12 hedge orders, 18 stops; Pepperstone: 4 hedge stops), nothing sent again after. It was Saturday, so the 16 non-crypto symbols per account were in the rollover window (market closed) and crypto was not — as designed. After [issue-log.md #2](../issue-log.md#2-hedges-and-loss-limit-hit-by-the-daily-rollover-spread-05-nov-2026): at the daily rollover the spread widens many times over for minutes; buy-stop hedges filled on the spiking ask and TM's own rules then acted on the spike prices, up to the 3R loss limit. Owner's choice (08-Nov-2026): three parts.

**Terms:** *bid* — the price you can sell at (the chart shows it); *ask* — the price you can buy at; *spread* — ask minus bid. *Trigger method* — which price makes a stop order fire; cTrader's default is the trade's own side (a buy-stop on the ask, a sell-stop on the bid).

**A. Hedges triggered on the other side.** TM's hedge orders are placed with the trigger method **`DOUBLE_OPPOSITE`**: a buy-stop fires when the **bid** reaches it, a sell-stop when the **ask** does, and only on **two prices in a row** beyond the level. A spread blow-out (one side jumps) or a single odd price no longer fills a hedge; a real move still does, one spread and one price later. The same for the stops TM sets on positions — the hedge's own stop, the backstop (§5.1) and the break-even stop (§5) — with the stop-loss trigger method `DOUBLE_OPPOSITE`. Hedge orders already on the server get it at TM's next start (amended once). In code: an optional trigger-method argument on `CTraderSession`'s stop-order, amend and position-protection calls ([Open Code Pieces](../open-code-pieces.md)); default unchanged for other bots.

**B. Spread guard.** TM keeps each symbol's **usual spread**: the median of the spreads it saw in the last 2 hours (from the price feed it already has; sampled every 10 s). While the current spread is more than **`spreadGuardFactor` × usual** (5×), that symbol is *guarded*:

| TM rule | While guarded |
|---|---|
| Loss limit 3R, loss warning 2R (§5) | Not judged — the spike price is not the market |
| Close a hedge whose stop the price has passed (§4) | Waits; the hedge's server-side stop still works |
| Re-hedge at market / 1R beyond the price (§4) | Waits |
| Hedge orders at the fixed level H, the backstop, break-even, orders the owner places | Unchanged — they are on the server and protect |
| Mirroring a new trade | Waits |

The guard ends when the spread is back under the threshold, or after **`spreadGuardMaxMinutes`** (15) — then TM acts as usual, with a Warning that the spread is still wide. One `SpreadGuard` event at its start and end (symbol, spread, usual spread); a pop-up only when it lasts the full 15 minutes.

**C. Rollover window (backup).** The same guard for every symbol from **5 minutes before each close to 15 minutes after each reopening** in its trading hours from the broker (the daily break — 17:00 New York for forex — and the weekend), whatever the spread looks like. It covers the case where the usual spread is not known yet (just after TM starts). Symbols traded around the clock (crypto) have no window; B covers them.

**Config (`tm.json`, new):** `spreadGuardFactor` 5, `spreadGuardMaxMinutes` 15, `rolloverBeforeMinutes` 5, `rolloverAfterMinutes` 15, `stopTrigger` `"DoubleOpposite"` (or `"Opposite"`, `"Trade"` = today's behaviour).

**What it does not change:** the protection rule — every leg keeps a protective order on the server at all times (shared §4); the hedge level H, 1R, the 2R/3R values.

**Built (08-Nov-2026):** `SpreadGuard.cs` (usual spread, guard, rollover window), the planner's guarded rules and trigger (`Planner.cs`), the trigger on `CTraderSession`'s stop-order, amend and position-protection calls. A guard start and end is a `SpreadGuard` line in `events.csv`; `SpreadGuardLong` pops up when it runs out after 15 minutes. Not guarded: the margin rescue (§5.1) — the account's survival comes first.

**Tests (12 new, TM 69):** the 05-Nov GBPUSD sequence (ask 1.34644, bid 1.34484): without the guard TM closes the hedges at the spike price and warns at −2.2R, as on 05-Nov; with it TM waits, and once the spread is back the trade carries on at about −1.1R instead of −3R; with `DoubleOpposite` the spike fills no hedge while a real move does; hedge orders and TM's stops reported with another trigger are set again once; a new mirror waits while guarded; the guard on at 5×, off when back, ended after 15 minutes; the rollover window from a forex schedule (none for crypto); config checks. Still to do: a rollover replayed in Rewind (it does not send the trigger fields back yet) — or watch the first live rollover after the deploy.

**Open for the owner:** Scout's entry orders with the same trigger (they can be filled falsely at the rollover too); Scout's picture tagging the whole trade's result.

## 6. Start-up, restarts and news (Q13, Q20)

- **TM's own orders** (mirrors, hedges) are found again after a restart from their labels and linked to their main by its `positionId` (labels below).
- **A main TM did not see opened** (TM was down): **hedges only, no mirror**, plus a warning; if price is already beyond H, the hedge opens at market at once.
- **News and weekend:** a pop-up (Warning) **10 minutes before each NFP/CPI release** (`news-releases.json`, shared with the Audit Bot) and **10 minutes before the Friday close** (17:00 New York), listing the open TM trades ("No open TM trades" when there are none). Sent once each, also when TM starts inside the 10 minutes. No automatic action; hedge fills beyond H are logged with their slippage (Scenario 5). Lead time: `alertMinutesBefore` in `tm.json`.
- **Connection down:** a pop-up (Critical, stays on screen) when TM has had no working connection to cTrader for **60 s** (`disconnectAlertSeconds`), and a pop-up "TM connected again" (Warning) when it is back. A normal reconnect takes about 7 s and gives no pop-up. While down, positions keep their hedges and stops on the server, but nothing is managed.
- **News dates must be kept up to date:** TM logs a warning at start when `news-releases.json` has no future release.

## 7. Labels, logs and notifications (Q17)

- **Labels** ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)): `TM-<6hex>-P<mainPositionId>-MIR` (mirror), `-HMAIN` (hedge of the main), `-HMIR` (hedge of the mirror), e.g. `TM-3f9a1c-P123456789-HMAIN`. The main's `positionId` in the label links every order to its trade across restarts; reconcile by bot name, as for every bot. A **mirror order** (§3.1) is placed before the main has a `positionId`, so it carries the owner's `orderId` instead: `TM-3f9a1c-O123456789-MIR`; its position keeps that label (a label cannot change). The `trade` column of `actions.csv` reads `O<orderId>` while the main is a pending order, `P<positionId>` after. New actions: `PlaceMirrorOrder`, `AmendMirrorOrder`.
- **Logs:** per account and instance as for the Audit Bot ([Audit Bot §3](audit-bot.md#3-logs-on-disk)): `events.csv`, plus `actions.csv` — one row per order TM sends or changes. Columns: `time_utc`, `trade` (P + main's positionId), `symbol`, `role` (MAIN, MIR, HMAIN, HMIR), `action` (OpenMirror, PlaceHedge, MoveOrder, CancelOrder, SetProtection, ClosePosition), `side`, `lots`, `price_req`, `price_fill`, `latency_ms`, `result` (`ok` or the broker's code), `detail`.
- **Notifications:** a **reusable module in the [Open Code Pieces](../open-code-pieces.md#7-notifications)**: one call for every bot; channels plug in behind it — **desktop pop-ups** on Ubuntu now (`notify-send`), Telegram or others later as one new class; a config file routes events to channels. Logs (journal, `events.csv`) always get everything. Pop-ups for: 2R warning, 3R close, mirror skipped or rejected, hedge rejected, TM stopped or disconnected, news / Friday warnings.

- **Built (step 1):** `Trader.Core/Notifications` — `bot.Notify.Info/Warning/Critical(event, title, message, account)`; every call is logged (journal and `events.csv`), then queued to the channels whose filter in `~/tradebot/config/notifications.json` accepts it (`minLevel`, `bots`, `events`, `excludeEvents`, `enabled`; no file = desktop pop-ups from Warning). The same message within 60 s is sent once; a failing channel is logged once and never blocks the bot. Desktop = `notify-send`; Critical stays on screen. Any bot can test with `--test-notification`.

## 8. Accepted risks

| Risk | Effect | Bounded by |
|---|---|---|
| Price jumps past H (news, gaps) | Hedge fills far beyond H; one jump can pass 3R | Pop-up before news / Friday close; slippage logged |
| Chop around H | Each hedge cycle costs ≈ B + spread, or adds up to 1R after an armed exit | 2R warning, 3R limit |
| TM down when the owner opens a trade without a stop-loss | Main unprotected until TM is back | Set a stop-loss on manual orders; TM hedges at once on start |
| Many trades for the account | Mirrors and hedges hold margin; a full account refuses hedges | Since v1.9 (§5.1): no mirrors below 300 %, a backstop on every unhedged leg, legs closed when a hedge has no money, margin rescue below 100 %. Sizes themselves are Scout's and the owner's (Scout's margin budget, [scout.md §2.5](scout/scout.md#25-margin-budget-v17-06-nov-2026)) |
| Mirror entry differs from the main's | A fraction of a pip | Logged in `actions.csv` |

## 9. How TM is built, build steps and live findings

**How TM is built:** one loop per account, every second (at once after an order event). Each run reads the account's positions and orders (a reconcile, refreshed after every order event and at least every 30 s), and a rule function (`Planner`) compares them with the rules of §3–§5 and lists what to change: open the mirror, place / move / cancel a hedge, set or remove a stop-loss / take-profit, close a position, notify. Anything that did not work is simply planned again next second, so missed messages, reconnects and restarts heal themselves. What only TM knows — H, each leg's R, hedge results so far, steps done, warnings sent — is saved in `~/tradebot/run/tm/<broker>-<ctid>.json` after every change; TM orders found without a saved trade are rebuilt from the server. Config: `~/tradebot/config/tm.json` (accounts, symbols, rule values, fallback distances). Every order: a row in `actions.csv`.

**Found while testing live:**
- The API **can** change the stop-loss and take-profit of a manual position on both brokers (open item closed).
- In a fast market the price can pass a hedge's stop (Fh − B) before the stop is set; the broker refuses it (`TRADING_BAD_STOPS`). TM then closes the hedge at market — what the rule intends.
- A hedge level only a few spreads from the entry makes every hedge cycle cost about 1R (test: BTCUSD with 1R = $10 against a ~$5 spread reached −2R in a minute). TM warns (`TightHedge`) when 1R is under 5 × the spread.
- TM recognises the orders of an earlier TM copy (labels carry the bot name; reconcile by bot name, [Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)).
- **Mirror orders (v1.5), on Rewind (22-Oct-2026):** a manual sell-stop (0.5 lot, SL, TP) got its mirror sell-stop at the same price with SL at H and the TP; a manual buy-limit without SL got its mirror with SL at the default distance, which followed the owner's move and new TP and was cancelled with it. Both sell-stops filled at 1.09200 and became one trade (no mirror at market), hedges at H, stop-losses removed; Rewind's `tm-trades.csv` had both legs. 8 orders, all accepted.
- **Pending orders and trailing stops (v1.4), on Rewind (22-Oct-2026):** a manual sell-limit with SL (trailing) and TP and a manual buy-stop were left alone while pending; the sell-limit's fill was taken over (mirror, both hedges at its stop-loss, TP copied); at 1R each leg got a trailing break-even stop, which trailed with the price and closed the mirror at +0.37R and the main at +2.61R (Rewind's `tm-trades.csv`). 10 orders, all accepted. Not yet seen on a cTrader demo (§12).

Each step only on the owner's go; each tested on the demo accounts before the next.

| Step | Content | Status |
|---|---|---|
| 1 | Notifications module in `Trader.Core` (§7) | **Done 20-Oct-2026** — pop-ups tested from a terminal and from a systemd user service; both seen on screen by the owner |
| 2–5 | *(owner: "just implement it — we are on demo")* Watcher, trade start, hedge management, break-even, 2R/3R limits, clean-up — built together as one engine (`src/Trader.TM`) | **Done 20-Oct-2026** — 22 scenario tests on a simulated broker; live on both demos: take-over with and without stop-loss (ATR distance), long and short, TP copy, hedge level moved by a new stop-loss, owner closing one leg, restart with saved state, hedge fill → hedge stop → re-hedge → 2R warning (BTCUSD); both accounts flat afterwards |
| 6 | News and Friday pop-ups, disconnect pop-ups (§6); systemd service and deploy script | **Done 20-Oct-2026** — 8 new tests (news lead time, start inside it, Friday close in summer and winter time, disconnect after 60 s and back, short drops silent); live on both demos: a test release popped up at T−10 min, forced drops gave "disconnected" and "connected again" pop-ups, clean stop; service started from the published build and stopped again; both accounts flat afterwards |

## 10. Run and operate

As a background service (from `~/code/Trader`; runs the published build in `~/tradebot/bin/TM`, so code changes do not affect it until redeployed and restarted):

```bash
scripts/deploy-tm.sh                       # publish + install the service (does not start or restart it)
systemctl --user enable --now trader-tm    # start now and at every login
systemctl --user status trader-tm          # running?
journalctl --user -u trader-tm -f          # live output
systemctl --user restart trader-tm         # switch to a newly deployed build
systemctl --user stop trader-tm            # stop (positions keep their hedges and stops on the server)
```

Run only one TM: stop the service before running it by hand in a terminal (`dotnet run --project src/Trader.TM`, Ctrl+C stops it). The service restarts TM 30 s after a crash, but not after a fatal problem it reported itself (token rejected, bad config). User services run while you are logged in (shared.md §3).

**On past prices (Rewind):** [Rewind](https://github.com/ziash/Rewind) (separate project) replays past prices in the browser with a simulated broker that speaks the Open API. TM runs against it unchanged — start Rewind, then:

```bash
~/rewind/tm/run-tm.sh        # TM with Rewind's secrets, certificate and tm.json (account 9000001); Ctrl+C stops it
```

On Windows: `powershell -ExecutionPolicy Bypass -File "$HOME\rewind\tm\run-tm.ps1"` (Rewind's certificate is trusted through `CTRADER_CA_FILE`, open-code-pieces.md §3).

Trades on Rewind are kept apart from the demo accounts by the broker name `rewind` (state `~/tradebot/run/tm/rewind-9000001.json`, logs `~/tradebot/logs/rewind/`). The demo service can run at the same time. TM's news and Friday pop-ups follow the real clock, not the replay's. On Rewind you open the positions in its browser page instead of cTrader.

- **Exact mode** (Rewind's default): while TM is connected, each replayed price step of a symbol in a trade waits until TM has had its look (about 5 s per 5-minute candle), so TM acts as it would live at any replay speed.
- **Results:** Rewind writes `tm-trades.csv` per session (`~/rewind/sessions/<name>/`): one row per leg with 1R, hedge cycles, break-even, the largest loss in R, who closed it (owner, TM's 3R rule, stop-loss, take-profit) and the result in R and USD — measured with TM's own formulas (Rewind spec §8).

Then open positions in cTrader on a listed symbol, as usual. Pop-ups show warnings (2R, mirror skipped, refused orders, tight hedge, news, Friday close, connection down); everything is in the console and the logs.

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
| 4 | Risk limits never block TM; it warns and follows the rules (owner: "Trade Manually — if I am opening, just follow the rules"). **Changed by decision 13** for margin | Your trades are your decision |
| 5 | Trailing off at first; you close | "Trade Manually"; the swing definition is still open |
| 6 | Built without a dry-run stage (owner: "we are on demo already") | Speed; demo money |
| 7 | One loop per account that compares the server with the rules every second, plus a saved state file (§9) | Missed messages, reconnects and restarts heal themselves |
| 8 | Connection-down pop-up only after 60 s, Critical; "connected again" as Warning. News / Friday pop-ups also when no TM trade is open | Routine reconnects (~7 s) would be noise; a long outage leaves trades unmanaged. A heads-up before news is useful before opening a trade too |
| 9 | A trailing stop on the main makes both legs' break-even stops trail; before 1R the hedge protects, as for any stop-loss (owner, 22-Oct-2026) | Trailing is kept as the owner's wish without a stop that would close the main at the hedge level |
| 10 | The owner's pending order gets a mirror order of the same type at the same price (stop-loss at H, the owner's take-profit), following the owner's changes; hedges once both fill (owner, 22-Oct-2026; replaces v1.4's "pending orders left alone, mirror at market after the fill") | Main and mirror enter at the same price, as with a market entry; hedges cannot sit at H before the entry (§3.1) |
| 11 | Which labels TM takes over is configurable: `"takeOver"` in `tm.json`, `""` = no label, other entries match by the start of the label; default and live `[""]` (owner, 24-Oct-2026) | The owner decides per label, without code changes; prefix matching survives bot restarts (instance IDs change) |
| 12 | **`"Scout-"` in the live `takeOver`** (`["", "Scout-"]`, owner, 03-Nov-2026): Scout places the first stop order of each setup, TM manages it like the owner's own pending order (mirror order, hedges at its stop-loss, break-even, trailing — Scout sets the trailing switch). Scout never closes a position; it only cancels its own unfilled orders, and TM then cancels the mirror (§3.1) | The owner's rule: Scout makes the entry, TM does the rest ([scout.md §2.4](scout/scout.md#24-with-tm)). The open item below about bots that close their own mains does not arise for Scout |
| 13 | **The account's margin is a rule, not only a warning** (§5.1, owner, 06-Nov-2026: "improve the API … let bots handle the issues and resolve them"): backstop stop-loss at the 3R limit while no hedge is filled; a hedge refused for money is retried after a minute, and past H the leg is closed; no mirror below 300 %; below 100 % TM closes the trade holding the most margin, one at a time | The margin call of 06-Nov-2026 ([issue-log.md #1](../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)): hedges need margin, so a full account left five trades without protection, and TM retried the same refused hedge 5,490 times |

## 12. Open items

- **Before adding another bot's label to `takeOver`** (e.g. `AuditBot-`; done for `Scout-`, decision 12): decide what TM does when that bot closes its own main — today the mirror and its hedge carry on as a TM trade (§5). Audit Bot probes close within ~1 s, so each would leave a mirror.

- Keep `news-releases.json` up to date (it lists releases to Feb 2027; shared with the Audit Bot).
- Parameters to tune with a backtest: 15 % of the 3-day range, spread floor 5 ×, A = 1R, B = ½R, 2R / 3R limits. Rewind's `tm-trades.csv` (§10) gives the per-leg data for this (replayed trades with TM unchanged); rule values can be tried in `~/rewind/tm/tm.json` without touching the demo config.
- Trailing (R3) needs the exact M5 swing definition ("last lower high") and the stop buffer — the same open question as [Auto Bot §9](auto-bot.md#9-open-questions).
- A partial close of a main does not yet resize its pending hedge (the hedge keeps the original volume).
- Mirror orders (§3.1) are tested on Rewind only (Scout's trial from 03-Nov-2026 is the first demo use): check once on a cTrader demo that a limit / stop order with an absolute stop-loss and take-profit is accepted, that an amend with price, volume, stop-loss and take-profit replaces them, and that the fill's execution event carries the order's type and the new `positionId`.
- Trailing stops set over the Open API (§5, decision 9) are tested on Rewind only: check once on a cTrader demo that `trailingStopLoss` on an amended stop-loss is accepted, shown in the reconcile, and trails at the distance it had when set.

## 13. History

| Version | Date | Change |
|---|---|---|
| — | 19 Oct 2026 | (as part of the old single plan, v1.18) Spec agreed with the owner, 20 questions one at a time |
| — | 20 Oct 2026 | (old plan v1.20–v1.22) Step 1: notifications ([Open Code Pieces](../open-code-pieces.md)); steps 2–5: the trading engine, tested live on both demos |
| v1.0 | 20 Oct 2026 | Own project document, split out of the old plan ([archive](../archive/plan-v1.22.md), Section 10.9) and the former TM page. Content unchanged |
| v1.1 | 20 Oct 2026 | Step 6 done: news / Friday / disconnect pop-ups (§6, decision 8), systemd service `trader-tm` and `scripts/deploy-tm.sh` (§10). TM is built |
| v1.2 | 21 Oct 2026 | TM tested unchanged on Rewind's replayed prices (§10): take-over with mirror and both hedges, SL removed once hedged, hedges filled at H, hedge stops at ½R, re-hedge, 2R warning; clean-up after close all |
| v1.3 | 22 Oct 2026 | §10: Rewind's exact mode and its per-leg results file `tm-trades.csv`. A replayed week (17–21 Mar 2025, 11 trades on EURUSD, GBPJPY, BTCUSD) with TM unchanged: 150 orders, all accepted; Rewind's results matched TM's logs leg for leg (hedge cycles 0–5, break-even, 2R warnings, one 3R close). Seen there: chop around H (accepted risk, §8) — 3 hedge cycles cost 2.6R on one EURUSD trade, 5 cycles led to the 3R close on another. §12: Rewind as the backtest tool for the rule values |
| v1.4 | 22 Oct 2026 | Owner's pending orders (buy/sell limit and stop) and trailing stops (§2, §5, decision 9): pending orders untouched, a fill taken over like a market entry; a main's trailing stop makes both legs' break-even stops server-side trailing stops, following the main's switch. Code: `Pos.Trailing`, `TradeState.Trailing`, `SetProtection.Trailing`; Trader.Core's SL call takes the trailing flag ([Open Code Pieces](../open-code-pieces.md) v1.3); 4 new tests. Tested on Rewind (§9) |
| v1.5 | 22 Oct 2026 | **Mirror orders** (§3.1, decision 10, owner): the owner's buy/sell limit or stop order gets a mirror order of the same type at the same price, stop-loss at H, the owner's take-profit; it follows the owner's changes and is cancelled with the order; hedges once both fill (they cannot be placed at H before). Labels `TM-<6hex>-O<orderId>-MIR` (§7). Code: `Planner` (pending trades), `TradeState.MainOrderId`, `Ord` with type and protection; Trader.Core `PlacePendingOrderAsync` / `AmendPendingOrderAsync` ([Open Code Pieces](../open-code-pieces.md) v1.4); TM tests: 36 (3 new, 2 rewritten). Tested on Rewind (§9) |
| v1.6 | 24 Oct 2026 | **Take-over list** (§2.1, decision 11, owner): `"takeOver"` in `tm.json` sets which labels TM takes over (`""` = no label; others by prefix); default and live `[""]`, so behaviour is unchanged. TM refuses a list that is empty or matches its own labels; logs the list at start. Code: `TmConfig.TakeOver` / `TakesOver`, `Planner` (mains); TM tests: 47 (11 new) |
| v1.7 | 03 Nov 2026 | **TM manages Scout's orders** (decision 12, owner): live `takeOver` = `["", "Scout-"]` in `tm.json`; TM restarted with it on both demos for the Scout trial ([scout.md](scout/scout.md)). No TM code change |
| v1.8 | 04 Nov 2026 | **IC Markets symbols for Scout** (owner): brokerA's list in `tm.json` = Scout's 18 symbols (top three per market) plus EURGBP; TM also takes over the owner's unlabelled positions on them there. Pepperstone unchanged. TM restarted with it 04-Nov-2026 18:14:30Z (`TM-38ade9`; all 19 symbols found, 6 + 6 remembered trades picked up). No TM code change |
| v1.9 | 06 Nov 2026 | **Margin rules** (§5.1, decision 13, after the IC Markets margin call, [issue-log.md #1](../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)): TM reads balance, equity and margin (Trader.Core `AccountStatus`, [Open Code Pieces](../open-code-pieces.md) v1.9); backstop stop-loss at 3R replaces "stop-loss removed once hedged"; `NOT_ENOUGH_MONEY` on a hedge → retry after 1 min, past H the leg is closed; no mirror below `marginWarnLevel` 300 %; margin rescue below `marginRescueLevel` 100 %; `AccountStatus` line every 15 min, `MarginLow` pop-up; a refused action pops up once per 10 min. Code: `Planner` (`Rescue`, backstop, no-money branch), `AccountManager.ReadStatusAsync`, `LegState.Backstop` / `NoMoneyUtc`, `Pos.Margin`, `TmConfig.MarginWarnLevel` / `MarginRescueLevel`. TM tests: 57 (10 new in `MarginTests` and `PlannerTests`, 6 updated for the backstop) |
| v1.10 | 07 Nov 2026 | Documents reorganised (owner, [shared](../shared.md) v1.13): links to Scout and the Dashboard follow their new folders; Related line |
| v1.11 | 07 Nov 2026 | **Pepperstone symbols for Scout** (owner): brokerB's list in `tm.json` = Scout's 18 plus EURGBP, with `symbolNames` for Pepperstone's names; brokerA's list unchanged so the IC Markets trades stay managed. Restarted 17:27Z (`TM-590e67`) |
| v1.12 | 08 Nov 2026 | **§5.2 planned** (owner, after [issue #2](../issue-log.md#2-hedges-and-loss-limit-hit-by-the-daily-rollover-spread-05-nov-2026)): hedges and TM's stops triggered on the other side (`DOUBLE_OPPOSITE`), a spread guard on TM's own actions (5× the usual spread, at most 15 min), a rollover window from the broker's hours as backup. Design only |
| v1.13 | 08 Nov 2026 | **§5.2 built** (owner's OK): hedges and TM's stops trigger `DoubleOpposite`; spread guard (5× the usual spread, at most 15 min) on the loss limit and warning, closing a hedge whose stop was passed, re-hedging and new mirrors; rollover window from the broker's hours. 12 new tests (TM 69). Not deployed yet |
| v1.14 | 08 Nov 2026 | **§5.2 live** 07-Nov 19:39 UTC (`TM-03d420`): 34 one-time trigger changes on existing hedge orders and stops, all accepted; live `tm.json` has the new values. First real test: the Sunday reopening and Monday's 22:00 UTC rollover |
