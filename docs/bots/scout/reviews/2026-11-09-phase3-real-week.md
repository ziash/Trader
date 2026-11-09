# Phase 3: the replay against real trading

**On the broker's own prices the replay reproduces the real orders, fills and the margin failure. It passes 7 of the 8 checks. On the Dukascopy history that the baseline uses it passes 2 of 8: right in general, not trade by trade.** Run on 09-Nov-2026. Review reference: [P-06](2026-11-08-v1.0-independent-review.md).

| | Result |
|---|---|
| Stretch compared | IC Markets demo 49123893, 04-Nov-2026 18:15 to 06-Nov-2026 16:12 UTC: 46 hours, the longest stretch with one build and one list of 18 symbols running unchanged |
| Bots | The builds that really ran (Scout `6711a92`, TM `2b56e33`), with the lock-step switch added to throw-away copies. Not today's rules |
| Issue #1, margin ran out | **Reproduced** |
| Issue #2, rollover spike | **Not reproduced.** The first half appears, the chain does not |
| Tolerance | Written down before the first run (below). **It is the reviewer's proposal, and the review asks for an agreed one: the owner still has to accept it** |

## The eight checks

An order counts as the same order if its symbol, setup time and role agree. R is the distance from entry to initial stop for one leg.

| Check | Tolerance | On IC's own candles | On Dukascopy's history |
|---|---|---|---|
| Real orders found in the replay | ≥ 90 % | 96 % (128 of 133) | **75 %** (100 of 133) |
| Replay orders found in the real log | ≥ 90 % | 96 % (128 of 133) | **81 %** (100 of 124) |
| Entry level, median difference (share of stop distance) | ≤ 10 % | 0.0 % | 2.4 % |
| Entry level, 90 % of orders within | ≤ 25 % | 0.0 % | **262 %** |
| Same answer on filled or not | ≥ 85 % | 93 % (119 of 128) | **79 %** (79 of 100) |
| Filled within 5 minutes of the real fill | ≥ 80 % | 86 % (43 of 50) | 82 % (33 of 40) |
| Whole trade, median difference | ≤ 0.25 R | 0.20 R (37 trades) | **0.30 R** (29 trades) |
| Sum of whole-trade results | difference ≤ 10 % of the sum of absolute results | **18 %**: real +$37, replay +$425 | **24 %**: real +$526, replay +$778 |

The Dukascopy run has no SOLUSD: Dukascopy's feed stopped on 22-Aug-2026 (Rewind spec §13). The two sums cover different trades, because each counts only trades finished on both sides.

"IC's own candles" means the 5-minute bid candles read from the demo account (read-only). The broker's history has no ask, so the ask is that bid plus Dukascopy's spread of the same 5 minutes; SOLUSD has IC's quoted spread. One ask comes from the broker's deals: GBPUSD at 22:00 on 05-Nov, 1.34644 (issue #2).

## A bug this found, and the corrected baseline

**Scout acted 5 minutes late in every replay.** A live Scout asks for a new candle a few seconds after it closes. The replay's turn fell exactly on the close, before that moment, so the order went out at the next turn, 5 minutes later. Live, those were minutes in which price could cross the level. The fix is one clock offset in replay mode (`LockStep.CandleReadDelay`), with a test. It changes nothing on the demos. Before the fix, on IC's candles, 92 % of real orders were found and only 76 % filled within 5 minutes. The published baseline had the same fault, so it was run again: [baseline](2026-11-09-phase3-baseline.md). It went from −54 % and −32 % to −57 % and −44 %.

## What explains the differences

**The price feed is the largest cause.** With nothing else changed, IC's candles instead of Dukascopy's raise the orders found from 75 % to 96 %. A setup depends on exact candle shapes, and two feeds differ by a few points, so a pause or a push can exist on one and not on the other. On matched setups the entry levels agree closely, as the median of 2.4 % shows, but where a different setup carries the same name the level can be far off, hence the 262 %.

**The 10 unmatched orders on IC's candles:**

| Number | Cause |
|---|---|
| 3 | Edge of the window. The real order was placed just before 18:15 by the previous Scout (AUDJPY, BTCUSD, ETHUSD) |
| 4 | The replay refused an order whose level was already touched or passed; the real broker accepted it and filled it at once (review F-10). Today's Scout does not send such orders (scout.md §2.3) |
| 3 | A reversal order that exists only if the first order filled at a certain moment, and a different fill changed that |

**The missed check: four trades.** Without them the difference in the sum falls to 7 %. That figure was computed after seeing the results, so it is an explanation and does not count as a pass.

| Trade | Real | Replay | Why |
|---|---|---|---|
| BTCUSD 05-Nov 10:00 | +$28 | +$230 | The real trailing stop was hit at 13:46. The replay's was not touched, and the trade ran on. A small difference in the borrowed spread or in the 5-minute path is enough for that |
| GBPUSD 05-Nov 19:00 | −$95 | +$47 | Issue #2's chain, below |
| GBPJPY 05-Nov 19:00 | +$24 | −$161 | The Dukascopy spread added to IC's bid is wider at the rollover than IC's own, so the replay filled hedges that really stayed untouched |
| XPTUSD 05-Nov 03:00 | −$95 | $0 | The real order filled at once on a level already passed. In the replay it waited 8 hours |

## Issues #1 and #2

**Issue #1 reproduced** by the rule set beforehand: hedges refused for lack of money, first within 6 hours of the real time, and a margin level below 100 %.

| | Real | Replay on IC's candles |
|---|---|---|
| First refusal for lack of money | 06-Nov 00:32:29 | 06-Nov 00:28:20 |
| Refusals in the stretch | 5,666 | 1,994 |
| Margin level | 92 % at 14:47, 86 % at 16:14 | Lowest 89 % at 13:15 (hourly) |
| Equity at the end | $2,289 | $2,287 |
| Balance at the end | $3,426 | $3,945 |
| Positions and orders | 43 and 15 | 61 and 15 |

The closeness of the equity is partly luck. The replay starts without the trades opened before 18:15 and holds more hedged positions open. The refusals fall on other symbols (real: mostly ETHUSD, replay: mostly SOLUSD) because which trade runs out of money first depends on small differences.

**Issue #2 not reproduced** by the rule set beforehand, which asked for the 3R limit within 10 minutes of 22:00 with hedges filled and filled again. With the real ask written in, the first half happens: both GBPUSD hedges fill on the spike and TM closes them at a loss on the bid, −$11.70 each. The chain then stops, because a spike that lasts one of four price steps cannot fill hedges a second time. The second fill needs second-by-second prices that this history does not have. TM's unit tests hold the real sequence.

## What this does and does not show

| Shown | Not shown |
|---|---|
| Rewind handles orders, fills, hedges, margin and refusals close to how the broker did, when given the broker's prices | Anything about today's rules: these were the old builds |
| Results on Dukascopy's history are right in general, not per trade | Pepperstone: the demo there has not traded this way on one build for long enough |
| The cause of each large difference | A full week. The stretch is 46 hours |

Limits that remain in every replay: 5-minute candles with four price steps, no second-by-second spread, and a spread borrowed from Dukascopy.

**Acceptance of P-06 is partly met.** The review asks for a match within an agreed tolerance with the gaps explained. 7 of 8 checks pass on the broker's prices, the eighth is explained, and the owner has not yet agreed the tolerance. Issue #2 stays open for lack of second-by-second prices.

## How to repeat it

```bash
WORK=~/rewind/work/trial-week scripts/trial-week/run.sh
```

It reads IC's candles once, read-only, and sends no order to any broker. The comparison on its own is `scripts/replay-compare.py`.
