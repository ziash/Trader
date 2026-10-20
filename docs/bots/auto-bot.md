# Auto Bot — semi-automated trend trading

The original trading idea of this repo: **you read the trend and decide on TradingView; the bot executes and manages the trade** through the cTrader Open API — hedge instead of stop-loss, a 4-leg structure, scale-out, trailing and pyramiding. It is a project of its own; much of it is still open.

| Item | Value |
|---|---|
| Status | **Concept** — exit logic Scenario 1 and the position structure are defined; entry rules, Scenarios 2–5 and many parameters are open (§9). Not built |
| Document version | v1.0 (20-Oct-2026) — history in §10 |
| Code | Not started. Will be `src/Trader.AutoBot`, on the [Open Code Pieces](../open-code-pieces.md) |
| Accounts | Demo only until a go-live is decided ([shared rules](../shared.md#4-safety-rules-for-every-bot)) |
| Related | [TM](tm.md) already runs a hedge-and-recover logic for manually opened trades; its answers to Scenarios 2–5 (TM §4–§5) are a starting point here |

## 1. Purpose

Resume short-to-mid-term trend trading with a semi-automated setup. The human reads the market and makes decisions on TradingView; the bot executes and manages trades through the cTrader Open API, using raw-spread broker accounts (chosen with the [Audit Bot](audit-bot.md)).

## 2. Decisions

| # | Area | Decision | Why |
|---|---|---|---|
| 1 | Human interaction | TradingView (browser) | Best charts; works on Ubuntu; no install |
| 2 | Trading style | Naked price action; real trades are shorts | Owner's own method (ex-Chris Capre student) |
| 3 | Loss control | Opposite-side hedge order instead of a stop-loss (§6) | Owner's design; specified scenario by scenario |
| 4 | Position structure | Every trade = 2 half-size shorts + 2 half-size hedges, with numbered legs (§7) | Clean scale-out; each pair has its own exit |
| 5 | Trade management | Runner leg trails on M5; rotating leg closes when the next pyramid add opens; unlimited adds within an exposure cap (§7) | Ride the trend while bounding total exposure |

## 3. How the pieces fit

```
YOU ──► TradingView (browser) : read trend, decide, (later) send alerts
              │ optional webhook (Phase 4)
              ▼
        Ubuntu machine
┌───────────────────────────────────────────────┐
│ Bot engine (C# / .NET worker service)         │
│  • own price & candle feed from broker        │
│  • rules, risk sizing, hedge logic, exits     │
│  • per-broker / per-instance logs on disk     │
└───────────────┬───────────────────────────────┘
                │ cTrader Open API (TLS, Protobuf)
                ▼
   Broker A (cTrader)        Broker B (cTrader)
```

> **Important:** the bot never reads TradingView charts. It gets its own prices from the broker — the same prices it trades on. TradingView is the human's window, and later an optional signal source.

## 4. Human interaction via TradingView

### 4.1 What the human does

- Reads the higher-timeframe trend (D1 / H4) and picks candidate markets.
- Marks levels, decides direction and when to engage (opens Trade 1 of each sequence).
- Monitors open positions (manually, or via TradingView-broker chart integration where the broker supports it).

### 4.2 How a human decision reaches the bot (staged)

| Stage | Method | Notes |
|---|---|---|
| A | Bot config file / small local dashboard | Human sets symbol, direction, risk %; bot executes |
| B | TradingView alert → webhook → bot | Needs a paid TradingView plan with webhooks and a secure public HTTPS endpoint (e.g. Cloudflare Tunnel). Bot validates a shared secret before acting. |

> **Guardrail:** a TradingView alert can only request an action. The bot re-checks risk limits, spread and account state before placing any order.

> After the human opens Trade 1, the bot itself opens pyramid adds (§7.3, rule R4). This moves trade management from semi-automated toward automated; every add still passes the same risk checks.

## 5. Risk guardrails

- **Risk per trade:** 1% of equity to the hedge trigger level (adjustable, never above 2%). Applies to every pyramid add individually.
- **Daily loss limit:** bot stops new entries (including pyramid adds) for the day after a set loss.
- Maximum open positions and maximum total exposure caps.
- **Exposure cap for pyramiding:** no new add if, after the add, effective leverage would exceed **1:10** or margin level would fall below **300%**. Both values are parameters. There is no cap on the number of adds.
- No trade if spread exceeds a per-symbol threshold.
- Effective leverage typically under 1:10 even if the account allows 1:500.
- **Kill switch:** one command closes all bot positions (including hedges) and stops the service.

## 6. Exit logic: hedge-and-recover (no stop-loss for risk)

Scenario 1 is defined below and illustrated with a real AUD/JPY chart (Figure 1). §7 adds the position structure, leg naming, scale-out and pyramiding rules. Scenarios 2–5 are open (§6.7).

### 6.1 Concept

- The real trade is always a **SHORT** position.
- Instead of a stop-loss, the bot places a pending **BUY-STOP** order (the hedge) above the short entry. If price rises to that level, the hedge opens and the short's loss stops growing.
- The LONG side is used only as a hedge, never as a real trade.
- Once the hedge is safely in profit, the bot protects it with an exit just above its own entry. If the move against the short turns out to be a wick, the hedge closes at about break-even and the short continues.
- "No stop-loss" applies to **risk**. Once a short leg is 1R in profit, a stop-loss is used to **lock in** gains (§7).

### 6.2 Terms and parameters

| Symbol | Meaning | Set by |
|---|---|---|
| E | Short entry price (real trade) | Trader / strategy |
| H | Hedge trigger level: price of the pending buy-stop. Working assumption: H = E + ~10 pips | Trader (working assumption, to be tested) |
| Fh | Actual hedge fill price (H plus any slippage) | Market |
| A | Arming distance: how far price must rise above Fh before the hedge exit is placed ("a bit more") | Parameter — value to be set in backtest |
| X | Hedge exit = Fh + 1 pip (a sell-stop on the hedge) | Rule |
| T | Profit target / exit of the short | Trader / strategy |
| Size | Each hedge leg = the size of its own short leg (each = ½ of trade size). Full hedge, per pair. | Confirmed |
| 1R | Profit equal to the distance risked: (H − E). With H = E + 10 pips, 1R = 10 pips | Rule |

### 6.3 Scenario 1 — wick against the short, then the down-move resumes

Market path: price rises through H, makes a spike (wick) higher, then reverses and falls back below H and on toward the short's target. Only one whipsaw against the main position.

![Figure 1 — AUD/JPY daily chart](../images/audjpy-scenario1.png)

*Figure 1 — AUD/JPY daily (OANDA, TradingView). Blue line = short entry E ≈ 114.62 (July high retested in September). The early-September wick to ≈ 114.97 is the single whipsaw; price then fell to ≈ 109.7 in mid-September.*

| Step | Market event | Bot action | Position after the step |
|---|---|---|---|
| 1 | Trader takes the short | Open SHORT at E. Place BUY-STOP hedge at H (same size). | Short open; hedge pending |
| 2 | Price rises to H | Hedge fills at Fh. Bot does nothing else yet — it waits. | Short + long = net zero. Short's loss frozen at (Fh − E). |
| 3 | Price rises a further A above Fh | Arm the hedge exit: place SELL-STOP on the hedge at X = Fh + 1 pip. | Hedge now protected at about break-even |
| 4 | Spike reverses (the wick) | Price falls to X → hedge closes at about +1 pip. | Only the short remains open |
| 5 | Down-move resumes | Short is managed by the rules in §7 (trailing, adds) until closed. | Flat |

**Result of Scenario 1:** hedge ≈ +1 pip (before slippage); short = (E − T) pips profit. A normal stop-loss at H would have closed the short at a loss on the wick and missed the move.

### 6.4 Worked example — real AUD/JPY levels from Figure 1 (1 lot)

| Item | Value |
|---|---|
| Short entry E (blue line) | 114.62 |
| Hedge buy-stop H = E + 10 pips (fill Fh) | 114.72 |
| Arming distance A (example) | 15 pips → hedge exit armed when price reaches 114.87 |
| Wick high | 114.97 (25 pips beyond Fh, so the exit arms) |
| Hedge exit X = Fh + 1 pip | 114.73 → hedge closes +1 pip on the way down |
| Short target T (example) | 110.00 → short closes +462 pips |
| Total (before costs) | +463 pips ≈ $3,100 at ~$6.76 per pip (USD/JPY ≈ 148 assumed) |
| Costs to subtract | Spread on short and on hedge (zero-commission account: cost is in the spread), plus swap for nights held |

> **Key constraint shown by this chart:** Scenario 1 only happens when the wick extends at least A beyond the hedge fill. Here the wick gave 25 pips of room above Fh, so A must be below about 25 pips. With a larger A, or a smaller wick, the hedge exit never arms and the trade falls into Scenario 2.

### 6.5 Equivalent rule on a single net position (for backtesting)

Economically, Scenario 1 is the same as: close the short at H, then re-open the short when price falls back to Fh + 1 pip after having risen at least A above Fh. Exposure, profit/loss and spread paid are the same at every moment. The backtest will run both versions side by side; matching results confirm the logic, and the live version can use whichever the trader prefers. The difference that remains is swap and margin while both legs are open.

### 6.6 Requirements for Scenario 1 to work

- The cTrader account must be a **hedging** account (long and short on the same symbol at the same time).
- The hedge buy-stop is held on the broker's server from the moment the short opens, so the short stays protected if the bot or home internet goes down.
- Every order carries a label linking the short and its hedge, so the bot can reconcile both legs after a reconnect (naming in §7.2).
- Pip size, spread and slippage are taken from the broker's symbol data, not hard-coded.

### 6.7 Scenarios still to be defined

- **Scenario 2** — hedge fills, then price falls back before the hedge exit is armed (price never reaches Fh + A).
- **Scenario 3** — chop around H (whipsaw): hedge opens and closes repeatedly; each cycle costs spread.
- **Scenario 4** — sustained trend against the short: hedge never closes; both legs stay locked. When and how to unwind.
- **Scenario 5** — gaps and news spikes: hedge buy-stop fills far beyond H. (Its effect is multiplied by pyramiding — see §7.6.)

TM has answered these for its own case (TM §4–§5): hedge stop ½R behind its fill, re-hedge rules, 2R warning and 3R close, pop-ups before news. They can be adopted or changed here.

## 7. Position structure, scale-out and pyramiding

Applies after a trade is on track: either it never needed its hedge, or Scenario 1 completed and the hedge closed at about break-even. The trader accepts the risk of whipsaw and of the hedge exit not arming (Scenarios 2–3) as part of this design.

### 7.1 Split structure

- Every trade is opened as **two half-size shorts and two half-size hedge buy-stops**. Example: a 1-lot trade = 2 × 0.5-lot shorts + 2 × 0.5-lot buy-stops.
- Both shorts sit at the same entry E. Both hedges sit at the same trigger level H.
- Each short is paired with its own hedge, so closing a pair never requires resizing an order.
- Minimum trade size is therefore 2 × the broker's minimum volume (e.g. 0.02 lots), and the trade size must split into two valid volume steps.

### 7.2 Leg naming convention

Leg numbers keep counting up across trades. Trade *n* uses the odd/even pair of numbers (2n − 1, 2n). The label is written into each order's label/comment field and is used to rebuild pairs after a reconnect. Full label = bot instance ID + leg, e.g. `TrendBot-3f9a1c-T1-S1` ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)).

| Trade | Runner pair (odd) | Rotating pair (even) |
|---|---|---|
| T1 | T1-S1-L1 | T1-S2-L2 |
| T2 | T2-S3-L3 | T2-S4-L4 |
| T3 | T3-S5-L5 | T3-S6-L6 |
| Tn | Tn-S(2n−1)-L(2n−1) | Tn-S(2n)-L(2n) |

S = short leg, L = its hedge (long buy-stop). Odd legs are **runners**; even legs are **rotating** legs.

### 7.3 Lifecycle rules

| Rule | Trigger | Bot action |
|---|---|---|
| **R1** Entry | Trader decides to short (Trade 1 of a sequence) | Open both shorts at E; place both hedge buy-stops at H. Size = 1% risk to H. |
| **R2** Protect at 1R | A short leg reaches 1R profit (price ≤ E − (H − E)) | First place a server-side stop-loss at E (break-even) or better on that leg; **then** cancel its hedge buy-stop (or close it if filled). Order matters: the leg is never left unprotected. |
| **R3** Trailing | Each M5 candle that closes below the 21 EMA | Move each protected leg's stop to just above the most recent M5 lower high (plus a spread buffer). Stops only move down, never up. |
| **R4** Pyramid add | An M5 candle closes back at the 21 EMA (retracement), **and** the latest trade has already reached 1R | Bot opens the next trade Tn+1 with the same 4-leg structure at the new entry; its hedges at new E + 10 pips; size = 1% risk to its own H. |
| **R5** Rotation | Trade Tn+1 is filled | Close the even (rotating) leg of the previous trade Tn, e.g. T2 opening closes T1-S2. Because of R2 this leg is already in profit when it closes. |
| **R6** Runners | Ongoing | Odd legs (S1, S3, S5…) stay open on trailing stops until stopped out. If no further add occurs, the latest even leg also keeps trailing. |
| **R7** Scope | Any add | Same symbol, short only. |
| **R8** Guards | Before every add | Previous trade ≥ 1R; spread below threshold; daily loss limit not hit; exposure cap respected (§5: effective leverage ≤ 1:10 and margin level ≥ 300% after the add). No limit on the number of adds. |

Effect: the position pyramids by half a trade per add. Older runners sit on stops in profit, while only the newest trade carries fresh risk, so open risk stays close to one trade's 1%.

### 7.4 Illustration (levels from Figure 1, 1-lot trade)

| Event | Legs open | Protection |
|---|---|---|
| T1 opened at E = 114.62 | T1-S1, T1-S2 (0.5 each) | L1, L2 buy-stops at 114.72 |
| Price reaches 114.52 (1R = 10 pips) | T1-S1, T1-S2 | Stops at ≤ 114.62 placed, then L1 and L2 cancelled |
| Trend continues; M5 closes below 21 EMA | T1-S1, T1-S2 | Stops trail above each new M5 lower high |
| M5 retraces to 21 EMA → T2 opens | T1-S1, T2-S3, T2-S4 | T1-S2 closed in profit; L3, L4 at T2 entry + 10 pips |
| T2 reaches 1R | T1-S1, T2-S3, T2-S4 | All on trailing stops; next add allowed |

*Exact prices for adds depend on where the retracement happens; the table shows the sequence, not a backtest result.*

### 7.5 Implementation notes for the bot

- Build M5 trendbars and the 21 EMA from the broker's own candle feed (same prices the bot trades on).
- "Last lower high" = the highest high of the most recent completed M5 swing below the 21 EMA; the exact swing definition is fixed in the Phase 3 spec and tested.
- Track each leg's state: `pending-hedge → hedged → protected (stop in profit) → trailing → closed`.
- On reconnect, rebuild all pairs from labels and re-check that every open short has either a hedge or a stop on the server.
- Stop modifications on many legs share the API rate budget; batch them per M5 candle close.

### 7.6 Accepted risks and worst case

| Accepted risk | Worst case | How it is bounded |
|---|---|---|
| Gap or news spike through all trailing stops at once | All runners fill with heavy slippage on a large combined size; locked profit can turn into a loss | Exposure cap (leverage ≤ 1:10, margin level ≥ 300%); news filter to be added with Scenario 5 |
| Retracement deeper than expected after an add | New trade hits its hedge; older runners give back part of their open profit | Each add risks only 1%; older legs are at break-even or better |
| Spread paid on every add and every leg | Higher cost in choppy trends | Spread filter; adds only after 1R |
| M5 trailing exits early in a healthy pullback | Runner closes before the trend ends | Accepted; next retracement add re-enters the trend |
| Whipsaw / hedge exit not arming (Scenarios 2–3) | Small loss or locked position on a trade | Accepted by trader; rules to follow in Scenarios 2–3 |

## 8. Roadmap

| Step | Content | Status |
|---|---|---|
| 1 | Spec: entry rules, Scenarios 2–5, swing definition, parameters (§9) | Open |
| 2 | Backtest with broker data incl. costs; hedge version vs net-position version compared (§6.5) | — |
| 3 | Build on demo: semi-auto execution, hedge-and-recover, 4-leg structure, trailing and pyramiding, config / dashboard (§4.2 stage A) | — |
| 4 | TradingView webhook (§4.2 stage B) | — |
| 5 | Go-live (small), if decided ([shared rules](../shared.md#4-safety-rules-for-every-bot)) | — |

## 9. Open questions

- Price-action entry rules (to be written as a spec).
- Hedge design: test H = E + ~10 pips as the working assumption; choose the arming distance A (must be smaller than typical wick extensions beyond Fh). Full-size hedge per pair: confirmed.
- Hedge design: define Scenarios 2–5 (§6.7) — TM's answers are a starting point.
- Fix the exact M5 swing definition for "last lower high" and the stop buffer (pips above the high). Also needed before TM's trailing switch is turned on.
- Confirm the 21 EMA retracement trigger: candle close at/above the EMA vs. a touch of the EMA.
- Confirm exposure-cap values (effective leverage 1:10, margin level 300 %) after the Audit Bot shows real margin requirements.
- Account type: zero-commission (Standard, spread-only) vs raw-spread + commission for the hedge design — compare using Audit Bot data. Both audited demos are raw spread + commission (IC Markets Raw Spread, Pepperstone Razor); a Standard account is not being audited.
- Protection if power/internet drops: confirm the server-side hedge order (before 1R) and stop-loss (after 1R) are sufficient, or add a wide emergency stop-loss as a last-resort backstop.
- Short-only approach: confirm instruments, and check swap direction for shorts on each one.
- TradingView plan level (webhooks need a paid tier) — decide before stage B (§4.2).

## 10. History

| Version | Date | Change |
|---|---|---|
| — | 08–10 Oct 2026 | (as part of the old single plan, v1.0–v1.3) Purpose, TradingView, hedge-and-recover exit logic, Scenario 1 with the AUD/JPY example, 4-leg structure, leg naming, lifecycle rules R1–R8, risk guardrails |
| v1.0 | 20 Oct 2026 | Own project document, split out of the old plan ([archive](../archive/plan-v1.22.md), Sections 1–4, 9, 10.1–10.8, open items). Named **Auto Bot**. Content unchanged |
