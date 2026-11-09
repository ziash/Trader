# Scout

**Watches the owner's charts for the "pause, then stop order" setup and places the first stop order of each one; TM manages everything after.** The setup rules, modes and decisions are in Scout's own spec ([github.com/ziash/Scout](https://github.com/ziash/Scout), `docs/spec.md` — the source of truth for *what* Scout looks for); this document covers how the bot runs here. A project of its own; it uses the [Open Code Pieces](../../open-code-pieces.md).

**Related:** [Scout Lens](scout-lens.md) (AI layer) · [TM](../tm.md) (manages Scout's orders) · [Dashboard](../../dashboard/dashboard.md) (Scout page and controls) · [issue log](../../issue-log.md) · [independent review v1.0](reviews/2026-11-08-v1.0-independent-review.md) (08-Nov-2026) · [all projects](../../../README.md#projects)

| Item | Value |
|---|---|
| Status | **Running on demo — trial from 03-Nov-2026** (Scout spec §4.6): Trade mode, 1H, small sizes per symbol (§7); review after one week (≈ 10-Nov-2026). From 04-Nov-2026: IC Markets only, 18 symbols; **from 07-Nov-2026 17:27Z: Pepperstone only, same 18 symbols** (§7) |
| Document version | v1.40 (09-Nov-2026) — history in §8 |
| Code | [`src/Trader.Scout`](../../../src/Trader.Scout) (`Detector.cs` = Scout_Pine's rules, `ChartTracker.cs` = one chart + its higher timeframe, `AccountScout.cs` = candles, orders and logs per account, `OrderChart.cs` = order pictures) |
| Tests | [`tests/Trader.Scout.Tests`](../../../tests/Trader.Scout.Tests) — the detector against made-up charts and against Scout's Python prototype on real IC Markets 1H candles (identical events); the order pictures |
| Accounts | **Pepperstone demo 49124666 (`brokerB`) only from 07-Nov-2026**; IC Markets demo 49123893 (`brokerA`) only 04–07 Nov; both 03–04 Nov — demo only ([shared §4](../../shared.md#4-safety-rules-for-every-bot)) |
| Symbols | From 04-Nov-2026, top three per market on 1H: EURUSD, GBPUSD, USDJPY · GBPJPY, EURJPY, AUDJPY · XAUUSD, XAGUSD, XPTUSD · US500, USTEC, DE40 · XTIUSD, XBRUSD, XNGUSD · BTCUSD, ETHUSD, SOLUSD — `config/scout.json` (TM must list the same symbols in `tm.json`). 03–04 Nov: AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD on both demos. At Pepperstone five have other names, mapped in `symbolNames`: XTIUSD = SpotCrude, XBRUSD = SpotBrent, XNGUSD = NatGas, USTEC = NAS100, DE40 = GER40 |
| Bot name / labels | `Scout` — `Scout-<6hex>-<tf>-<pause start yyyyMMddHHmm>-M` (main stop) / `-R` (reversal stop), e.g. `Scout-3f9a1c-H1-202611031500-M` |
| Config | [`config/scout.json`](../../../config/scout.json) (accounts, charts and modes, `lots` and `lotsBySymbol`, expiry, setup settings) — live copy `~/tradebot/config/scout.json`; pop-ups: `notifications.json` |
| Memory | `~/tradebot/run/scout/<broker>-<ctid>.json` (setups that already got an order or alert; survives restarts) |
| Service | `trader-scout` (systemd user service, from `~/tradebot/bin/Scout`) |
| AI layer | [Scout Lens](scout-lens.md): an AI layer that takes, skips or shrinks the setups the rules find — **decided: options 1 + 3** (setup scorer + Claude reviewer, owner, 07-Nov-2026); the reviewer in two phases, tuning on the laptop then API calls from Scout; nothing built yet |

## 1. Goal

The owner takes every pause in a running trend with a stop order beyond it (Scout spec §2). Scout does this on many charts at once: it finds the pause on the broker's own candles and places the order the owner would place by hand, so TM can mirror, hedge, protect and trail it. Scout's job ends with that first order.

## 2. Rules

### 2.1 Modes (Scout spec §4.1)

Per chart (symbol × timeframe) in `scout.json`: `"watch"` sends an alert for each new stop level; `"trade"` places the order.

### 2.2 Candles and the detector

- Every chart's **closed** candles come from the broker (`ProtoOAGetTrendbarsReq`), asked for 5 s after a candle should have closed, each chart 0–18 s after the previous ones so 18 symbols don't ask at once. **Market hours from the broker** (each symbol's trading schedule): a candle whose market was closed throughout (weekend, daily break) is not asked for, so a closed market costs no requests. A candle that is late while the market is open is asked for again after 1, 2, 4 and 8 minutes, then once per candle length (at least 8 minutes), with one log line (`ScoutCandleLate`). All candle requests are paced to 2 per second by the connection (open-code-pieces.md §2; Spotware's limit is 5) (owner, 08-Nov-2026: gentle on a live network). At start, 30 days of candles rebuild the state (their events are not acted on).
- The detector runs Scout_Pine's rules (Scout spec §3.1, §3.2) unchanged — from 07-Nov-2026 Scout_Pine is closed for development (owner): new rules go only into this C# detector; the next timeframe up (1H ← 4H) runs its own detector for the tag *with / against / unknown* (Scout spec §4.4 I1). The tag is logged and shown, never used to skip.

### 2.3 Orders (Trade mode; Scout spec §4.2, §4.4)

| When | Scout |
|---|---|
| A pause gets its stop level (2nd candle) | Places a **Buy Stop / Sell Stop** at the level: its symbol's size (`lotsBySymbol`, else `lots` = 0.1; the broker's minimum and step apply), **stop-loss at the far side of the pause**, **trailing switch on**, **expiry on the server 16 candles after the candle that gave the level** (good till date), label as above |
| The pause rolls over (reversal stop, Scout spec §2.1 rule 5) | Places the reversal stop the same way, its stop-loss at the main level |
| One order of a pause fills | Cancels the other one (one cancels the other) |
| The setup ends unfilled (trend turned, pause too long or too deep) | Cancels its own unfilled order(s) |
| A level moves on later candles | Nothing: Scout never moves or amends an order |
| A position opened from its order | Nothing: TM manages it (mirror, hedges at H = the stop-loss, break-even at 1R, trailing) |

- Each setup role (pause × main/reversal) gets **at most one order, ever**: the memory file keeps it across restarts. A refused order (e.g. the price is already beyond the level) is logged and not retried. An order held back for margin (§2.5) is not an order: it is tried again while the setup lasts.
- After a restart, Scout's pending orders are found by label (`Scout-` + timeframe + pause start), whatever instance placed them.
- **Checked before sending** (v1.25, [review](reviews/2026-11-08-v1.0-independent-review.md) F-03, F-10, F-20). The order is not sent when: the configured size is below the broker's minimum (the minimum never raises a size; `skip`, *size below minimum*); the chart's candles are a whole candle late (*stale candles*); its expiry would be under one candle away (*setup too old*, so a late or restarted Scout never gives an old setup a fresh 16 hours); or there is no live price under two minutes old (*no live price*, e.g. the market is closed). These are tried again while the setup lasts. A level the live price has already passed (ask for a Buy Stop, bid for a Sell Stop) is refused for good (`place`, `refused`, `LEVEL_PASSED`): the broker would otherwise fill it at once at a worse price.
- **The broker's state decides** (v1.25, review P-01). Each role has a state in the memory file, saved before and after every broker call:

| State | Means | What happens next |
|---|---|---|
| `Sending` → `Working` | The request went out; the broker accepted it | Waits for fill, expiry or cancel |
| `Unknown` | Timeout or lost connection: the broker may have it | Never sent blindly. Found by label: `Working`. Not on the account or in the broker's history after 30 s and two reconciles: `Intended` |
| `Intended` | May be sent again (the request never arrived, or a temporary refusal such as `CANT_ROUTE_REQUEST` or `NOT_ENOUGH_MONEY`) | Through all the checks again; at most two sends in all, then `Abandoned` or `Refused` |
| `Refused`, `Abandoned` | Final | The setup gets no more orders |
| `CancelRequested` | Scout wants it gone; the broker has not confirmed | Tried after 5 s, 15 s, 60 s, then every 5 min while the order exists; a Critical pop-up after the third failure. Reverts to `Working` if the reason goes away |
| `Cancelled`, `Held`, `CancelledExternally`, `Expired` | Final (`Held`: placed again when released) | |
| `Filled` → `Closed` | Has a position; fully closed | A fill during a cancel wins and is reported |

- **Recovery:** after every (re)connect, and whenever a role is missing from the account, Scout reads the broker's order and deal history (14 days) and writes what it missed to `orders.csv`, once (`detail`: *recovered …*). Orders and positions missing from the memory are rebuilt from their labels. Two orders with one label: the newer is cancelled, with a Critical pop-up.
- Acts only while Primary on the account (open-code-pieces.md §6); a second copy only watches.

### 2.4 With TM

TM takes over orders whose label starts with `"Scout-"` (`takeOver` in `tm.json`, [tm.md §2.1](../tm.md#21-take-over-list-v16-owner-24-oct-2026), decision 12): a mirror order of the same size beside Scout's, hedges once both fill. When Scout cancels its order or it expires, TM cancels the mirror ([tm.md §3.1](../tm.md#31-entry-by-a-pending-order-owner-22-oct-2026)). Scout never closes a position, so TM's open item about bots that close their own mains does not arise.

### 2.5 Margin budget (v1.7, 06-Nov-2026)

After the IC Markets margin call of 06-Nov-2026 ([issue-log.md #1](../../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)), Scout checks the account before each new order. It reads the balance, equity and margin in use (read-only, [Open Code Pieces](../../open-code-pieces.md) v1.9) and the margin the order would hold once filled (`ProtoOAExpectedMarginReq`):

- **What is counted (v1.31, review P-05):** the margin the account could come to hold, worked out per symbol by the broker's own rule (`TotalMarginCalculationType`: SUM both sides, MAX the larger, NET the difference). Open positions count at the margin they hold. Each waiting trade counts as Scout's order and TM's mirror together, asked from the broker for that combined size so a leverage tier counts, with hedges of the same size implied; TM's waiting hedge orders count too. Each symbol is worked out with everything filled but unhedged, and again with the hedges filled, and the larger counts. The two orders of one pause count once.
- Scout places a trade only if that total stays within **`marginUsePercent` = 50 % of equity** (33 % while the limits of §2.9 are on), and the account could still carry it **under stress**: margin × 2 on metals and crypto, equity −10 %, every open position losing 4 more spreads (`stress` in `scout.json`).
- Every 5 minutes, new order or not, the stress is checked again; if the account would not carry what is on it, a `ScoutMarginStress` pop-up at most once an hour. New trades wait. Nothing is closed or cancelled for this.
- Otherwise: one `skip` row in `orders.csv` (result `no margin`, the numbers in `detail`), a `ScoutNoMargin` line in `events.csv`, and a pop-up at most once an hour. The setup is tried again at each check (every minute) while it lasts.
- No account status (the broker did not answer): no new orders, and Scout asks again after 5 minutes.

On IC Markets at 14:47Z on 06-Nov-2026 (equity $2,491.85, margin in use $2,707.70, larger-side margin) the limit was $1,245.92: Scout would have placed nothing.

### 2.6 Controls from the dashboard (v1.8, owner 06-Nov-2026)

The owner can steer Scout from the dashboard ([dashboard.md §3.4](../../dashboard/dashboard.md#34-controls-an-extensible-set-of-options-owner-0405-nov-2026)). The site writes a small control file; the laptop fetches it every minute into `~/tradebot/control/scout-<broker>-<ctid>.json`. Scout reads it every 30 s and before each order check, and applies only what the **catalogue** `~/tradebot/config/scout-controls.json` allows (laptop only; reference copy `config/scout-controls.json`).

| Option (catalogue key) | Scope | Values | Scout |
|---|---|---|---|
| `enabled` | symbol | on / off | **Off:** no new orders on the symbol; its pending orders are cancelled (`cancel`, reason *switched off on the dashboard*); positions stay with TM; setups are still logged. **On again:** orders again at the next check (within a minute) for setups that are still valid |
| `timeframe` | symbol | `H1`, `H4`, `D1` | Swaps the symbol's chart for the new timeframe (its higher timeframe as §2.2: H4 for H1, D1 for H4, W1 for D1), loads its history first, and looks for setups there from then on. **Pending orders from the old chart stay** until filled or expired (owner, 06-Nov-2026); one cancels the other still holds for them. Back to the configured timeframe = the same swap |
| `dailyDirection` | scout (one account) | on / off | §2.10. **Off:** orders on either side of the daily direction, as before v1.40. **On:** waiting orders against it are cancelled at the next check. Not set = `daily.enabled` in `scout.json`, on by default |
| `decision` | setup | `keep`, `skip` | **Skip:** that setup's pending orders are cancelled (reason *skipped on the dashboard*) and none placed for it while skipped. Already filled: nothing, TM manages it. **Keep again:** its order is placed again at the next check if the setup is still valid — the one exception to "at most one order per setup role" (§2.3), because the owner asked for it |

- **Setup key** = `<symbol>-<timeframe>-<pause start yyyyMMddHHmm>`, the setup part of Scout's order label; one key covers the main and the reversal stop. Entries older than 7 days are ignored.
- **Every entry is checked again:** an option not in the catalogue or unknown to this Scout, a value the catalogue does not allow, a symbol not in `scout.json` (or with more than one chart, for `timeframe`), a malformed setup key, or a file for another account is **refused — that entry only**; the rest apply. A file Scout cannot read is refused whole: Scout keeps the last good one and warns (pop-up `ControlFileBad`). No file = everything as configured.
- **Acknowledgement:** after applying a changed file Scout writes `~/tradebot/control/scout-<broker>-<ctid>.ack.json`: the file version, when, each entry *applied* (with the orders it cancelled) or *refused* (why), the options this Scout knows, and the last 50 file versions it applied with when (`applied`, kept across restarts, so the site can show when each change was applied). Values as JSON (`false`, not `False`). Events `ControlApplied`, `ControlRefused`, and `ControlReset` for a control put back as configured, in `events.csv`.
- A restart applies the current file again (it describes the wanted state, so applying it twice changes nothing).
- Controls never move, close or resize anything: they only hold Scout back or change which chart it reads. Order size, mode and the symbol list stay in `scout.json`.

### 2.7 Stopping new entries (v1.25, 08-Nov-2026)

From the independent review (F-02, P-04). A halt **only stops new entries**: Scout cancels its pending orders (TM then cancels their mirrors) and places none. **TM keeps managing and protecting every open position**; nothing is closed. Applies on the next sync, within a minute.

| Trigger | Default (`limits` in `scout.json`, % of equity) | Ends |
|---|---|---|
| Daily loss, from the first equity of the UTC day | `dailyLossPercent` 3 | At the next UTC day |
| Weekly loss, from the first equity of the week (Monday UTC) | `weeklyLossPercent` 6 | Owner's reset |
| Fall from the peak equity | `peakDrawdownPercent` 15 | Owner's reset |
| Owner's command | The file below exists | The file is removed |
| **TM not working on the account** (`requireTm`, default true; review F-14) | No finished TM management cycle for 60 s, or no TM seen since Scout started | TM reports again |

**TM's health:** TM reports each finished management cycle (read the account, plan, act) through the instance registry, so a TM that is running but cannot work counts as down. A restart inside 60 s passes unnoticed. While TM is down a fill would have no mirror or hedges, so this acts like any halt, and setups still valid are placed again when TM returns.

The values are starting points to calibrate on replay. Halts survive restarts (`Equity` in the memory file); a recovery of the equity does not end one. Setups cancelled by a halt are placed again afterwards if still valid and young enough.

**Commands on the laptop** (no website needed):

```bash
touch ~/tradebot/control/scout-halt                      # stop new entries on every account
rm ~/tradebot/control/scout-halt                         # allow them again
touch ~/tradebot/control/scout-halt-brokerB-49124666     # one account only
touch ~/tradebot/control/scout-reset-brokerB-49124666    # end a loss halt; the limits then count from the equity now
```

Pop-ups: `ScoutHalted` (Critical), `ScoutResumed` (Warning). A dashboard switch is a later step (review P-10).

### 2.8 Sizing from the money at risk (v1.27; **on** since v1.28, 08-Nov-2026)

From the independent review (F-01, F-19, F-25, P-02). With `"sizing": { "enabled": true }` in `scout.json`, each order's size comes from its stop distance and the account, replacing the fixed lots of `lots` and `lotsBySymbol`:

**volume = equity × `riskPercent` ÷ (`legs` × the loss of one lot at the stop)**, rounded down to the broker's step. The loss of one lot counts the stop distance in USD (through the symbol that converts its quote currency), `slippageSpreads` spreads, and the broker's commission opened and closed.

| Setting | Default | Means |
|---|---|---|
| `riskPercent` | 0.5 | Cash risk of the whole trade, all legs, in % of equity |
| `legs` | 2 | Scout's order and TM's mirror |
| `slippageSpreads` | 1 | Allowance for spread and slippage |
| `minStopSpreads` | 2 | A nearer stop is refused |
| `limitR` | 3 | TM's loss limit (`closeR` in `tm.json`) |
| `maxLossPercent` | 2 | Refused if the whole trade at `limitR` would lose more than this |

Refused, with a `skip` row (*size refused*) and tried again while the setup lasts: a size below the broker's minimum, no conversion price, no account status, a stop too near, or the cap above. The size is never raised. The margin budget (§2.5) still applies afterwards. Values are starting points to calibrate on replay. In either mode an order with no valid stop-loss is not sent (*no valid stop-loss*).

Preview, read-only, without turning it on: `dotnet ~/tradebot/bin/Scout/Trader.Scout.dll --sizes`.

### 2.9 Limits across the account (v1.33; **on** since v1.35, 09-Nov-2026)

From the independent review (F-01, F-23, P-03). Sizing (§2.8) limits one trade; these limits look at everything on the account together. They **only stop new orders**: nothing already on the account is cancelled or changed, and TM's mirrors, hedges and stops are never held back. On since 09-Nov-2026 (owner) with `"portfolio": { "enabled": true }` in `scout.json`; without that line it is off. See what it would count, at any time and read-only: `--risk` (§5).

**What is counted**, in USD at the loss from the entry, from the broker's own list of positions and orders each time an order is about to go out:

| On the account | Counts as |
|---|---|
| Position with a stop-loss and no hedge | The loss at that stop; nothing once the stop is at break-even or better. A leg with only TM's backstop therefore counts its full 3R |
| Leg with a hedge order waiting | The loss at the hedge level, plus `hedgeCycleR` (half) for the cost of a hedge that fills and is stopped out; never more than the loss at its own stop |
| Leg with a filled hedge | The frozen loss, plus the risk to the hedge's stop (nothing once the hedge's stop is past its fill; half the frozen loss while it has no stop yet) |
| Scout order waiting | As if filled: Scout's order and TM's mirror, costs included. The two orders of one pause count once, at the larger. A mirror order is not counted again |
| The owner's positions and orders | Counted the same way, never touched. An order with no stop-loss takes the one on its mirror order |

**The limits**, starting values to calibrate on replay:

| Setting | Default | Reached |
|---|---|---|
| `tradePercent` | 0.5 % of equity, all legs of one trade | Size reduced; refused if the broker's minimum is still too much (never raised) |
| `totalPercent` | 5 %, everything open and waiting | Refused |
| `symbolPercent` | 1 %, one symbol (caps staircase entries) | Refused |
| `groupPercent` | 2 %, one group below | Refused |
| `maxTrades` | 8, trades open plus pauses waiting (a waiting trade counts as if filled) | Refused |
| `maxPendingOrders` | 12, entry orders waiting; TM's hedge and mirror orders not counted | Refused |
| `marginUsePercent` | 33 %, replaces the 50 % of §2.5 while this is on | Refused (§2.5) |

33 % margin use is TM's 300 % margin level (`marginWarnLevel`), where TM stops mirroring: Scout stops adding trades first, so no trade loses its mirror to this rule. A test fails if the two configuration files in the repository drift apart; the live copies are compared at each deploy.

**Groups** (a static map to start with): USD (EURUSD, GBPUSD, USDJPY, kept apart by the dollar's direction: long EURUSD and short USDJPY are both short dollars and add up; long and short dollars do not offset, each side has its own limit), JPY (USDJPY, GBPJPY, EURJPY, AUDJPY), Metals (XAUUSD, XAGUSD, XPTUSD), US equity (US500, USTEC, DE40 at half), Crypto (BTCUSD, ETHUSD, SOLUSD), Energy (XTIUSD, XBRUSD, XNGUSD at half).

**Refused rather than guessed.** No new orders, with a `ScoutRiskUnknown` pop-up at most once an hour, while: a position or order has neither a stop-loss nor a hedge; one is on a symbol Scout does not follow; no price converts its currency to USD (after a restart at the weekend, until the market reopens); the account status is missing; or what became of a sent order is not yet known. A mirror that filled from an order carries the order's id in its label, so which trade it belongs to is read from TM's saved state (`~/tradebot/run/tm/`); without that, it counts on its own stop-loss.

A refused order gets one `skip` row (result `portfolio limit`, the numbers in `detail`) and a `ScoutRiskRefused` log line, and is tried again at each check while the setup lasts.

**Known limits of the count.** Equity already holds a frozen loss, so counting it again is deliberately cautious: old hedged trades use up room until they end. A waiting order counts its stop; once it fills and TM swaps the stop for a hedge order it counts the extra `hedgeCycleR`, so the counted total can rise by up to half after the orders were approved, without any order going out. The groups are a fixed list, not measured correlation.

### 2.10 Daily direction (v1.40, owner 09-Nov-2026; Scout spec §4.7 D1)

A Scout on a 1H or 4H chart also reads closed daily candles. Before an order is placed, and at every check while it waits:

| Daily direction | Order | Result |
|---|---|---|
| Up or down, same side as the order | Any | Goes on as before |
| Up or down, other side | Any, main or reversal | Not placed; a waiting one is cancelled (*against daily*) |
| Unknown | Buy with daily resistance near, sell with daily support near | Not placed; a waiting one is cancelled (*near daily resistance*, *near daily support*) |
| Unknown | No zone near, or no zones known | Goes on as before |

- **Direction:** the detector's own trend, run on closed daily candles with the setup settings of `scout.json` (the same test as the 4H tag, §2.2). It reads as unknown only until the first strong daily candle.
- **Zone** (`daily` in `scout.json`): at least `zoneTouches` 2 daily swing highs or lows, within `zoneWidthAtr` 0.25 daily ATR of each other. A swing high is the highest candle of `zoneSwing` 3 on either side, in the last `zoneCandles` 250 daily candles; a swing low likewise. Highs and lows count together, so a level can be support, then resistance.
- **Resistance for a buy** is any zone whose top is above the order's level; **support for a sell** is any zone whose bottom is below it. A level beyond the zone has cleared it, which is the owner's entry just past a daily level. **Near** = inside the zone, or within `zoneNearAtr` 0.5 daily ATR of it.
- **On by default** (owner; `enabled` in `daily`, and `dailyDirection` on the dashboard's Settings page, §2.6). The dashboard's choice wins over the file. Daily charts are not filtered.
- Written to `orders.csv` once per setup role as a `skip` row (result *against daily*, *near daily resistance* or *near daily support*), and `ScoutAgainstDaily` in the log. No pop-up. An order cancelled for this is placed again if the direction allows it later and the setup is still valid, as for §2.6's skip.
- The replay uses the same rule (`OrderPlanner`), with `--daily <D1.csv>`; `--daily-rule off` keeps every order and only labels it (`daily_trend`, `with_daily`, `daily_zone`).

## 3. Logs and outputs

`~/tradebot/logs/<broker>/<ctid>/<instanceId>/<date>/`:

| File | One row per |
|---|---|
| `setups.csv` | detector event: `NewStop`, `StopMoved`, `NewReversal`, `ReversalMoved`, `StopFilled`, `ReversalFilled`, `Cancelled` (with reason), `TrendTurned` — with level, far side, pause start, pause candles, pause height and move in ATR, pull-back, ATR, higher-timeframe trend and *with / against* |
| `orders.csv` | order action or result: `place` (ok / refused / unknown), `skip` (the reason in `result`), `cancel` (with reason; `result` ok / refused / no reply), `filled` (fill price, slippage in pips, position), `expired`, `cancelled` (not by Scout), `rejected`, `closed` (gross and net profit of each closing deal of a Scout position), `alert` (Watch mode), `chart` (a picture drawn later for an earlier order, §3.1). Column `chart`: the order's picture (§3.1), on its `place` or `chart` row |
| `events.csv` | every log line with an event name (shared format, open-code-pieces.md §8) |
| `charts/*.svg` | one picture per order Scout placed (§3.1) |

Pop-ups: "order placed" and Watch-mode setups (`notifications.json`: a desktop channel for Scout's `OrderPlaced` and `Setup`), refused orders and stops as warnings. TM's own `actions.csv` shows the mirrors, hedges and stops; its mirror labels carry Scout's order ID (`TM-…-O<orderId>-MIR`), which links the two logs for the trial review.

### 3.1 Order pictures (owner, 04-Nov-2026)

For a later web dashboard that shows Scout's entries by date: when Scout places an order (Trade mode, and in a dry run), it also draws a small picture of the chart as it stood at that moment.

| Item | Value |
|---|---|
| Shows | The last 60 closed candles, the pause (shaded), the stop level and stop-loss (dashed, with price tags), the moment of the order and the room up to its expiry (right edge, grey). Title: symbol, timeframe, side, level, stop-loss and its distance in pips, main or reversal stop; under it: broker, account, placed and expiry times, higher-timeframe trend (*with / against*), order ID |
| Candles | Scout's own, from the broker; only those closed by the moment of the order, so a picture drawn later is the same picture |
| Format | SVG (a vector picture any web browser opens): 720 × 400, ≈ 13 KB, sharp at any size; numbers always with '.' |
| File | `<date placed>/charts/<symbol>-<tf>-<pause start>-<M/R>-<order ID>.svg` in the instance's log folder, e.g. `brokerA/49123893/Scout-99c603/2026-11-03/charts/GBPJPY-H1-202611031500-M-1035054512.svg` |
| Link | `orders.csv` column `chart`: the path relative to `~/tradebot/logs`, on the order's `place` row; the memory file keeps it too (`Chart`) |
| Earlier orders | At start, after the history is loaded, Scout draws the missing pictures of the orders it remembers (from the same candles and the order's `place` row in any Scout log of the account) and writes a `chart` row for each in today's `orders.csv` — earlier log files are never changed. The higher-timeframe tag comes from that setup's `setups.csv` row, or, when it was not logged (a setup found while the history was being rebuilt), is worked out again from the higher timeframe's candles closed by the moment of the order. When an order has more than one picture, its latest `chart` row is the one to use |
| Other Scout orders on the chart (owner, 07-Nov-2026; build step 6) | The picture also marks Scout's **other setups on the same symbol and timeframe** whose pause lies inside the 60 candles shown (main and reversal stops; at most 6, the most recent), each as a solid line at its level from its pause to where its story ended, ▲/▼ where it filled, ✕ where it closed, and a tag of what had become of it **by the moment of this order**: `closed −$27.40` / `closed +$12.10` (red / green; `part closed` while some is left), `filled, with TM` (blue: filled, no close in Scout's log — TM manages it), `waiting` (placed, its expiry time not yet reached), `refused: not enough money` (the broker's reason in plain words), `place refused: …`, `not placed: no margin`, `held back` (amber), `expired` (also when its expiry time passed with nothing logged — it expired while no Scout watched the account, e.g. after Scout moved broker), `cancelled: <reason>`, `cancelled by owner or TM`, `alert only`, `no order` (grey). Tags are placed where they cover no other tag or line. Read from `setups.csv` and `orders.csv` of every Scout instance of the account, only the day folders in view and only rows written before the order, so a picture drawn later is still the same picture (code: `OrderLog.ReadHistory`). Logs that cannot be read: the picture is drawn without them (`ScoutChartError`). Pictures drawn before 07-Nov-2026 are not redrawn (owner). Why: on the GBPUSD 1H picture of order 1036845948 (06-Nov-2026 16:00Z) the two earlier pauses of the same move looked missed; the logs show the detector found both — the first (Sell Stop 1.34498, 05-Nov 21:00Z) filled and was stopped out an hour later, the broker refused the second (1.33816, 06-Nov 13:12Z, `NOT_ENOUGH_MONEY`, during [issue #1](../../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)). The tags answer "why wasn't that one taken?" on the picture itself. The same history goes to Scout Lens as the staircase step ([scout-lens.md](scout-lens.md) §1.1) |
| Safety | Drawn only after the order is placed; a picture that cannot be drawn is logged (`ScoutChartError`) and skipped, never in the way of an order |

TM's actions on the same order join by order ID (TM's mirror label `TM-…-O<orderId>-MIR`), so one picture serves both bots.

## 4. Run and operate

```bash
systemctl --user enable --now trader-scout   # start now and at every login
systemctl --user status trader-scout         # running?
journalctl --user -u trader-scout -f         # live console output
systemctl --user restart trader-scout        # switch to a newly deployed build
systemctl --user stop trader-scout           # stop (pending orders stay and expire by themselves)
scripts/deploy-scout.sh                      # publish a new build into ~/tradebot/bin/Scout-<hash> and link it (does not restart; copies scout.json on the first deploy)
```

By hand (stop the service first, so only one Scout acts): `dotnet run --project src/Trader.Scout -- --dry-run` (no orders, logs what it would do).

## 5. Tools

| Option | Does |
|---|---|
| `--dry-run` | Runs normally but places and cancels nothing |
| `--test-order` | One far-away EURUSD Buy Stop (0.01 lot, stop-loss, trailing, 1-hour expiry) on the first account, read back, cancelled. Run with TM stopped (TM would mirror it) |
| `--sizes` | Prints, read-only, the size §2.8 would give on each symbol of the first account beside today's fixed lots, for a tight, median and wide stop |
| `--risk` | Prints, read-only, the money at risk on the first account, by symbol and group, against the limits of §2.9, whether they are on or off |
| `--export-symbols <file>` | Writes, read-only, the first account's symbol details, swaps, commission and margin rate in Rewind's `symbols.json` format |
| `--lockstep` | A whole-account replay on Rewind, in step with it ([Open Code Pieces](../../open-code-pieces.md) §3). Replay mode only. Run with `scripts/full-replay.sh <from> <to>` and report with `scripts/replay-report.py <run>` |
| `--candles <SYMBOL> <TF> <from> <file.csv>` | Writes the first account's closed candles to a file (read-only); used for the tests and the review |
| `--replay <file.csv>` | Runs the detector on such a file and prints its events (no connection) |
| `--replay <h1.csv> --symbol <SYMBOL> --orders <out.csv> [--higher <h4.csv>] [--timeframe H1]` | Writes the orders Trade mode would place and cancel on those candles (§2.3 rules, setup settings and expiry from `scout.json`; no margin budget or controls), for Scout Lens ([scout-lens.md](scout-lens.md) §8 step 1); `scripts/lens-replay.sh` runs it on Rewind's history and has Rewind play the orders out in R |

## 6. Build steps

| Step | Content | Status |
|---|---|---|
| 1 | Detector (Scout_Pine's rules) + higher-timeframe tag; tests: made-up charts, identical events to the prototype on IC Markets EURUSD, GBPJPY, BTCUSD 1H (Aug–Nov 2026) | **Done 03-Nov-2026** — 14 tests |
| 2 | Trader.Core: pending orders with trailing switch and expiry ([Open Code Pieces](../../open-code-pieces.md) v1.7) | **Done 03-Nov-2026** — `--test-order` on IC Markets demo: Buy Stop with SL, trailing `True`, `GoodTillDate`, expiry kept by the server; cancelled |
| 3 | Live loop, orders, one-cancels-other, memory, logs, pop-ups; service and deploy script | **Done 03-Nov-2026** — dry run on both demos: 30 days rebuilt on all 12 charts, the same GBPJPY Buy Stop found at both brokers |
| 4 | Trial run with TM (Scout spec §4.6) | **Running from 03-Nov-2026** |
| 5 | Order pictures (§3.1) | **Done 04-Nov-2026** — tests on real EURUSD and BTCUSD candles; the trial's earlier orders drawn at the next start |
| 6 | Other Scout orders on the order picture, with what became of each (§3.1, owner 07-Nov-2026) | **Done 07-Nov-2026** — tests on the GBPUSD 05–06 Nov logs (outcomes as known at each moment, across two instances) and on real EURUSD candles (at most 6, not the order itself); redrawn by hand from the real GBPUSD logs: the six outcomes match §7. Takes effect at Scout's next restart |

## 7. Decisions and open items

Decisions are in Scout spec §4 and §7 (v1.2). Open here:

- **Seen on both demos, 03-Nov-2026 17:46Z:** the first Scout orders (GBPJPY 1H Buy Stop 212.686 / 212.683) filled and TM took them over: mirror filled at the same price, both hedges at Scout's stop-loss, "trailing stops from break-even".
- **Network drop 03-Nov-2026 ≈ 17:57–17:59Z:** Scout and TM stopped with exit code 3 (`ChannelClosedException` from a request in flight); systemd restarted both after 30 s and both picked up their state (TM's two GBPJPY trades, Scout's orders). Fixed in Trader.Core ([Open Code Pieces](../../open-code-pieces.md) v1.8): such a request now fails as an I/O error and is retried after the reconnect.
- **Crash on stop, 04-Nov-2026 06:10:39 PKT (01:10:39Z):** stopping the old Scout (`Scout-94a83f`) right after `deploy-scout.sh` had published the new build over its files ended in `Internal CLR error (0x80131506)` and a core dump (exit by signal 6). It was already being stopped; nothing was lost and the new build started cleanly. Cause: the deploy script renamed the running build's folder away and put the new build at its path, so files the old process loaded while stopping came from the new build (same family as AuditBot's 03-Nov crash, audit-bot.md v1.4). **Fixed 04-Nov-2026** ([shared §3](../../shared.md#3-machine-and-services) v1.6): each build now goes into its own folder `~/tradebot/bin/Scout-<git hash>` and the link `~/tradebot/bin/Scout` is switched to it; the running Scout keeps its folder until restarted. Tested on Scout the same day: deployed while running, restarted, it runs the new folder and the old one stopped cleanly.
- **IC Markets only, 18 symbols, from 04-Nov-2026 18:14:50Z** (owner, mid-trial; instance `Scout-ca1c1d`, all 18 symbols found; first new orders at once on GBPUSD, DE40 and SOLUSD, mirrored by TM): the top three per market — the Audit Bot's 18 symbols (audit-bot.md §2.1), with EURJPY instead of EURGBP. Pepperstone dropped. TM's IC list has the same symbols plus EURGBP (TM v1.8). Scout's pending orders left on Pepperstone were not cancelled: they expire on the server within 16 candles, and TM still manages any that fill there. The trial review (≈ 10-Nov) therefore covers two set-ups: 03–04 Nov six symbols on both demos, then 18 symbols on IC Markets.
- **Size per symbol, from 04-Nov-2026** (owner: fixed lots per symbol for now, small test sizes; a Scout screen showing risk per trade comes later). 0.1 lot was worth very different money per symbol: a typical 1H move (ATR) was ≈ $15 on forex but $169 on gold, $39 on BTC and $1.70 on ETH; SOLUSD's minimum is 1 lot and natural gas's 0.5. Now `lotsBySymbol` in `scout.json` sets each non-forex symbol so a 1H ATR is worth ≈ $15 per order (forex stays 0.1); the values and their reasoning are in the file. **XNGUSD** cannot go below the broker's minimum 0.5 lot (≈ $135 per 1H ATR, 9× the others) — owner, 04-Nov-2026: stays in trade mode at 0.5 lot; weigh its bigger size at the review. TM's mirror doubles each. The log and pop-up now show the size really sent, as `orders.csv` already did (before, they showed the configured 0.1 — the first SOLUSD order on 04-Nov 18:15Z went out as 1 lot); at start Scout lists every symbol's size and warns (`LotsAdjusted`) when the broker's minimum or step changes one.
- **Pepperstone only, from 07-Nov-2026 17:27Z** (owner: fresh results; the IC Markets demo's margin was used up — 4 setups waiting for margin at the restart, level 150 %). Instance `Scout-f64704`; the same 18 symbols under our names, five mapped to Pepperstone's (header). Sizes: the same $ per 1H move as at IC Markets (≈ $15); contract sizes are the same except silver (1 lot = 5,000 oz at Pepperstone, 1,000 at IC): **XAGUSD 0.01** (the minimum, ≈ $22) and **XNGUSD 0.06** (≈ $16; IC's minimum was 0.5, ≈ $135), checked with a dry run in a separate folder. Pepperstone is 1:200 (IC 1:500), so each order needs about 2.5× the margin; the margin budget (§2.5) applies as before. Scout's 9 waiting orders at IC Markets were left to expire (≤ 16 h); TM keeps managing the IC Markets trades. TM's Pepperstone list = the same 18 plus EURGBP ([TM](../tm.md) v1.11). First start: 7 orders placed at once from Friday's setups (EURUSD, GBPUSD, AUDJPY, XAUUSD, DE40, XBRUSD main and reversal); placed on Saturday, they expire on Sunday 09:27Z before forex opens. IC Markets stays the broker chosen by the Audit Bot (audit-bot.md §9); this is a Scout trial choice.
- **Daily first (owner, 07-Nov-2026; Scout spec v1.6 §4.7):** daily and monthly charts set the direction and key support/resistance levels; entries come from the pauses on the daily chart down to the chart Scout is set on (on 1H: daily, 4H, 1H — the higher give the first entry, the lower scale up), in that direction only — against it: alerts with a reason, not orders; add-ons the same size; retracement ≤ 0.382 full size, 0.382–0.5 half, > 0.5 skip; one-candle pauses traded; an "Automatic orders" dashboard switch (on for the demo test); 30M planned as a new timeframe — the lowest Scout trades on (owner, 07-Nov-2026); 15M and 5M only for checks, 5M the lowest. Lower-timeframe confirmation (Scout spec v1.7 §4.8): L1 strong trend = no pause yet on the higher chart while a lower one pauses; L2 a pause confirmed on a lower timeframe (bodies within 38.2 %, counter-trend candles answered within 1–2 candles) — the owner's USTEC example is in the Scout repository (`research/setup-examples/USTEC-2026-10`). Direction and levels from one monthly and one daily picture per symbol read by Claude ([scout-lens.md](scout-lens.md) §1.2). Measured with the replay before the rules change; nothing built yet.
- **AI layer — Scout Lens (07-Nov-2026):** the rules miss small caveats the owner sees on the chart. [scout-lens.md](scout-lens.md) compares five options (setup scorer, learn your eye, Claude chart reviewer, auto-tuned thresholds, end-to-end deep learning) and recommends a scorer plus a Claude reviewer, both in shadow mode first. **Decided (owner, 07-Nov-2026): options 1 + 3** (scout-lens.md §8); the Claude reviewer in two phases — a · training: the caveat list tuned on replayed setups with Claude Code on the laptop at night; b · inference: Batch API check over history, then API calls from Scout (scout-lens.md §4.1). Plan for phase a in scout-lens.md §9, waiting for the owner's OK; nothing is built.
- **"Missed" setups that were not missed (owner, 07-Nov-2026):** on GBPUSD 1H (05–06 Nov) two earlier pauses of the move looked skipped on the order picture. The logs show the detector found both: one was filled and stopped out, the broker refused the other for lack of margin (issue #1). The picture did not show them, hence §3.1's *other Scout orders* (build step 6, done 07-Nov-2026). Setups the detector really misses are tracked with Scout Lens's missed-setup check ([scout-lens.md](scout-lens.md) §1.1).
- The laptop suspends: no candles, no orders while asleep; pending orders stay on the server and expire. Same as TM (audit-bot.md §9 keeps-awake item).
- A Buy Stop triggers on the ask, the detector reads bid candles: a real fill can come a little before the detector sees the level reached. One cancels the other uses the real fill.

## 8. History

| Version | Date | Change |
|---|---|---|
| v1.0 | 03 Nov 2026 | First issue: Scout built (detector, Trade/Watch modes, orders with stop-loss, trailing and expiry, one cancels the other, logs) and started on both demos for a one-week trial with TM; `"Scout-"` added to TM's `takeOver` ([TM](../tm.md) v1.7) |
| v1.1 | 03 Nov 2026 | First fills seen and taken over by TM (§7); Scout saves its memory right after a fill (before: only with the next order); dropped-connection fix in Trader.Core (open-code-pieces v1.8) deployed with TM and Scout |
| v1.2 | 04 Nov 2026 | Order pictures (§3.1, owner): an SVG chart of every Scout order next to the logs, linked from the new `chart` column of `orders.csv`; earlier orders drawn at start (`chart` rows), their higher-timeframe tag worked out again when it was not logged. The trial's 9 earlier orders were drawn twice on 04-Nov (the first set, in `Scout-6af57b`, shows *unknown* for two tags and overlapping weekend dates); the second set is current |
| v1.3 | 04 Nov 2026 | Crash on stop after a deploy (§7) fixed in the deploy scripts ([shared](../../shared.md) v1.6): versioned build folders behind the link `~/tradebot/bin/Scout`; tested on Scout |
| v1.4 | 04 Nov 2026 | **IC Markets only, 18 symbols** (owner, §7): top three per market on 1H — forex majors and crosses, metals, indices, energies, crypto; Pepperstone dropped. TM lists the same ([TM](../tm.md) v1.8); Scout spec §4.6 updated (Scout v1.3). Open item: one lot size for every symbol |
| v1.5 | 04 Nov 2026 | **Order size per symbol** (owner, §7): `lotsBySymbol` in `scout.json` (≈ $15 per 1H ATR on every symbol; forex 0.1 lot; XNGUSD at its 0.5-lot minimum); log and pop-up show the size really sent; start log lists every symbol's size, `LotsAdjusted` warning. `lots` limit raised from 1 to 100 (SOLUSD needs 20). Tests: 24 (2 new, 1 updated for the IC-only config) |
| v1.6 | 04 Nov 2026 | XNGUSD stays in trade mode at its 0.5-lot minimum (owner, §7); its bigger size is weighed at the review |
| v1.7 | 06 Nov 2026 | **Margin budget** (§2.5, after the IC Markets margin call, [issue-log.md #1](../../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)): a new order only while margin in use + 4 × (waiting orders + the new one) ≤ 50 % of equity (`marginUsePercent`, `marginFactor` in `scout.json`); otherwise `skip` / `no margin` in `orders.csv`, tried again while the setup lasts. Code: `MarginBudget`, `AccountScout` (Trader.Core `AccountStatus`, `GetExpectedMarginAsync`, [Open Code Pieces](../../open-code-pieces.md) v1.9). Tests: 27 (3 new). Open: sizes against the account size (XNGUSD, SOLUSD) at the trial review |
| v1.8 | 06 Nov 2026 | **Controls from the dashboard** (§2.6, owner; dashboard build step 6): symbol on/off, timeframe per symbol (old chart's pending orders stay, owner), keep/skip per setup; the catalogue and control file, entry-by-entry checks, the acknowledgement file |
| v1.9 | 07 Nov 2026 | Controls checked live (EURUSD off and on again, applied in 1½–3 min). Three fixes (§2.6): the acknowledgement keeps each applied file version with its time (`applied`, last 50, across restarts); switch values written as JSON (`false`); `ControlReset` logged when a control goes back as configured. Tests: 34 (2 new) |
| v1.10 | 07 Nov 2026 | **Scout Lens** proposal added ([scout-lens.md](scout-lens.md)): options for an AI layer after the detector, with example charts; linked from the header and §7. No change to the bot |
| v1.11 | 07 Nov 2026 | Documents reorganised (owner, [shared](../../shared.md) v1.13): this document and [Scout Lens](scout-lens.md) moved to `docs/bots/scout/` (with a [README](README.md)), its charts to `docs/bots/scout/images/`; Related line |
| v1.12 | 07 Nov 2026 | **Scout Lens decided** ([scout-lens.md](scout-lens.md) v1.2, owner): options 1 + 3; the Claude reviewer in two phases (tuning on the laptop, then API calls); plan for the laptop loop. Header row and §7. No change to the bot |
| v1.13 | 07 Nov 2026 | Order pictures (§3.1, owner): planned — mark Scout's other setups on the same chart with what became of each (stopped out, refused, expired …), after the GBPUSD 05–06 Nov pauses looked missed but were found (§7); build step 6. Setups the detector really misses: [Scout Lens](scout-lens.md) v1.3 §1.1. No change to the bot yet |
| v1.14 | 07 Nov 2026 | **Build step 6 done** (owner's OK): order pictures mark Scout's other setups in view with what had become of each (§3.1): line, fill and close marks, tags coloured by outcome; read from the logs up to the order's moment. Old pictures not redrawn. Tests: 37 (3 new) |
| v1.15 | 07 Nov 2026 | **Pepperstone only** (owner, §7): `scout.json` account brokerB with `symbolNames` for five symbols; XAGUSD 0.01, XNGUSD 0.06 lot for the same $ per move; IC Markets' waiting orders left to expire. Restarted 17:27Z as `Scout-f64704`. Repo-config test follows |
| v1.16 | 07 Nov 2026 | Order pictures (§3.1): an order past its expiry time with nothing else logged is tagged `expired`, not `waiting` (seen on the first Pepperstone pictures: Scout's 04-Nov Pepperstone orders expired after it left). Tests: 38 (1 new) |
| v1.17 | 07 Nov 2026 | **Daily first** decided (owner, §7; Scout spec v1.5 §4.7, [Scout Lens](scout-lens.md) v1.5 §1.2). No change to the bot yet |
| v1.18 | 07 Nov 2026 | Daily first completed (owner, §7; Scout spec v1.6): entry ladder from the daily chart down to the set chart; 0.382–0.5 retracement = half size. No change to the bot yet |
| v1.19 | 07 Nov 2026 | Lower-timeframe confirmation decided (owner, §7; Scout spec v1.7 §4.8); 15M ladder includes 15M. No change to the bot yet |
| v1.20 | 07 Nov 2026 | Owner: **Scout_Pine closed for development** (Scout spec v1.8 §3): rule changes go from the Scout spec straight into `Detector.cs`; lower-timeframe check uses 5M at the lowest (no 1M). Trader CLAUDE.md updated. No change to the bot |
| v1.21 | 07 Nov 2026 | Owner: 30M is the lowest chart Scout trades on; 15M and 5M only for checks (Scout spec v1.9) |
| v1.22 | 08 Nov 2026 | **Gentler candle requests** (owner, §2): market hours from the broker (no requests while closed), late candles asked for after 1, 2, 4, 8 min then once per candle, charts spread 0–18 s, all candle requests paced to 2/s (Trader.Core). Tests: 41 (3 new) |
| v1.23 | 08 Nov 2026 | §5: `--replay … --orders` writes the orders Scout would place on past candles, for Scout Lens (scout-lens.md v1.9) |
| v1.37 | 09 Nov 2026 | **Full-system replay built and first baseline** (review P-06, P-07; owner: use Rewind and a switch, no separate simulator): `--lockstep`, `--export-symbols`, `scripts/full-replay.sh`, `scripts/replay-report.py`, Rewind v0.15. [Baseline](reviews/2026-11-09-phase3-baseline.md): −54.3 % (Nov 2024 to Dec 2025) and −32.2 % (Jan to Jun 2026), no edge shown; old hedged trades fill the limit of 8 trades. Locked period unused; not yet matched to a real trial week. Live bots unchanged |
| v1.38 | 09 Nov 2026 | **Replay matched against real trading** (review P-06): [report](reviews/2026-11-09-phase3-real-week.md), `scripts/replay-compare.py`, `scripts/trial-week/`. On the broker's prices 7 of 8 checks pass and issue #1 reproduces; issue #2 does not. Found that Scout acted 5 minutes late in every replay: fixed in replay mode only (`LockStep.CandleReadDelay`), baseline rerun: −57 % and −44 %. No change on the demos |
| v1.39 | 09 Nov 2026 | **Replay tolerance agreed** (owner, review P-06): the eight checks in the [report](reviews/2026-11-09-phase3-real-week.md) stand as written. Result unchanged: 7 of 8 on the broker's prices, the sum of results outside tolerance and explained, issue #2 open. No code change |
| v1.40 | 09 Nov 2026 | **Daily direction built** (§2.10; owner, Scout spec v1.11 §4.7 D1): by rule from closed daily candles, on by default, switch on the dashboard's Settings page; when the direction is unknown, no buy near daily resistance and no sell near daily support. Phase 4 of the review started with this, ahead of exits (owner); Phase 3 closed with its gaps recorded (owner). Replay labels every order (`daily_trend`, `with_daily`, `daily_zone`). [Measured](reviews/2026-11-09-phase4-daily-direction.md): misses the pass mark set before the run (−41 % and −49 % against −57 % and −44 %); the zones never acted, as the direction was almost never unknown. Not deployed |
| v1.36 | 09 Nov 2026 | **Phase 3 of the review started** (owner; the off-machine alarm F-13 left for later). Step 1, shared order rules (P-09, F-28): `OrderPlanner` holds which orders a setup wants, the stop-loss rule and the expiry, for the live bot and the replay; `ReplayOrders` has none of its own. No change in behaviour: the replay on all 19 symbols' history gives byte-identical orders before and after, and a new test feeds the live path real candles one at a time and gets the replay's orders exactly. Tests: 159. Not deployed |
| v1.35 | 09 Nov 2026 | **Portfolio limits turned on** on the Pepperstone demo, 01:40 UTC (owner; instance `Scout-0aadfa`, same build). At the start: 4.07 % of equity at risk across the account, 5 trades, 3 entry orders; the old BTCUSD trade (2.58 %) is above the symbol limit and the crypto group above its limit, so new crypto orders wait until it ends. Nothing on the account was changed |
| v1.34 | 09 Nov 2026 | v1.33 live on the Pepperstone demo, 01:39 UTC (owner's OK; build `Scout-8b28fe9`, instance `Scout-a43191`), portfolio limits **off**: Scout counts nothing against them until they are turned on, and `--risk` shows the exposure. TM not restarted (no code change) |
| v1.33 | 09 Nov 2026 | **Portfolio Risk Manager built, off** (§2.9, review P-03, F-01, F-23; owner: build the next item): money at risk from the broker's positions and orders; limits per trade, across trades, per symbol, per group, on trades and on waiting orders; 33 % margin use shared with TM while on; refuses when something cannot be valued; `--risk` read-only view. The review proposed reading TM's protection label; the facts are read from the broker instead, and TM's state only joins a mirror to its trade. Tests: 152 (38 new), and 26 deliberate breakages of the code all caught. Not deployed |
| v1.32 | 08 Nov 2026 | **Margin by scenario live** on the demo, 15:47 UTC (owner's OK; build `Scout-4f1aef3`, live `scout.json` updated). A clean start: no halt, no order placed or cancelled |
| v1.31 | 08 Nov 2026 | **Margin by scenario** (§2.5, review F-09, F-24, P-05): `marginFactor` removed; margin by the broker's rule for each stage of a trade, leverage tier from the trade's full size, a stress test before a new trade and every 5 minutes. Tests: 113 (11 new). Not deployed; the live `scout.json` must lose `marginFactor` at deploy |
| v1.30 | 08 Nov 2026 | **TM health check live** on the demo, 15:35 UTC (owner's OK; build `Scout-8408d9a`). A clean start: TM seen working, no halt, no order placed or cancelled |
| v1.29 | 08 Nov 2026 | **TM health check** (§2.7, review F-14, P-04): no new entries, and pending orders cancelled, while TM has not finished a cycle for 60 s (`requireTm`). Tests: 102 (5 new). Not deployed |
| v1.28 | 08 Nov 2026 | **Sizing turned on** (owner, §2.8): 0.5 % of equity per trade on the Pepperstone demo; `lots` and `lotsBySymbol` no longer used while it is on. At today's equity a median stop refuses XAUUSD, XAGUSD, XPTUSD, USTEC and DE40 (broker minimum above the budget) and SOLUSD (stop too near against its Sunday spread). Start-up log states the sizing in force |
| v1.27 | 08 Nov 2026 | **PositionSizer built, off** (§2.8, review P-02, owner): size from the money at risk, never raised to the broker's minimum; `--sizes` preview; an order with no valid stop-loss is not sent. Commission rule moved to Trader.Core and shared with the dashboard export. Tests: 97 (16 new). Not deployed |
| v1.26 | 08 Nov 2026 | **Review Phase 1 live** on the Pepperstone demo, 13:33 UTC (owner's OK; build `Scout-667ede8`, instance `Scout-c1ced9`). The memory file was converted and its 23 roles checked against the broker's history; no order placed or cancelled by the start. Old records cancelled by Scout show as `CancelledExternally`, because the old memory did not record who cancelled. A first new order through the live-price check is still to be watched |
| v1.25 | 08 Nov 2026 | **Review Phase 1** (owner: implement the audit, [report](reviews/2026-11-08-v1.0-independent-review.md)): order states confirmed by the broker, retries and backoff (§2.3), recovery from the broker's history, checks before sending (no raising to the broker's minimum, stale candles, setup age, live price), expiry counted from the level's candle (Scout spec v1.10), account halt and loss limits (§2.7), `IBrokerGateway` and a fault-injecting fake broker. Tests: 81 (35 new). Not deployed |
| v1.24 | 08 Nov 2026 | **Independent review v1.0** published ([report](reviews/2026-11-08-v1.0-independent-review.md), reviewed at `0a50a52`), with the audit prompt so it can be run again ([reviews/README.md](reviews/README.md)). Recommendations wait for the owner's approval. No change to the bot |
