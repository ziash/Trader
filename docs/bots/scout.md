# Scout

**Watches the owner's charts for the "pause, then stop order" setup and places the first stop order of each one; TM manages everything after.** The setup rules, modes and decisions are in Scout's own spec ([github.com/ziash/Scout](https://github.com/ziash/Scout), `docs/spec.md` — the source of truth for *what* Scout looks for); this document covers how the bot runs here. A project of its own; it uses the [Open Code Pieces](../open-code-pieces.md).

| Item | Value |
|---|---|
| Status | **Running on demo — trial from 03-Nov-2026** (Scout spec §4.6): Trade mode, 1H, small sizes per symbol (§7); review after one week (≈ 10-Nov-2026). From 04-Nov-2026: **IC Markets only, 18 symbols** (§7) |
| Document version | v1.6 (04-Nov-2026) — history in §8 |
| Code | [`src/Trader.Scout`](../../src/Trader.Scout) (`Detector.cs` = Scout_Pine's rules, `ChartTracker.cs` = one chart + its higher timeframe, `AccountScout.cs` = candles, orders and logs per account, `OrderChart.cs` = order pictures) |
| Tests | [`tests/Trader.Scout.Tests`](../../tests/Trader.Scout.Tests) — the detector against made-up charts and against Scout's Python prototype on real IC Markets 1H candles (identical events); the order pictures |
| Accounts | IC Markets demo 49123893 (`brokerA`) only from 04-Nov-2026 (Pepperstone demo 49124666, `brokerB`, until then) — demo only ([shared §4](../shared.md#4-safety-rules-for-every-bot)) |
| Symbols | From 04-Nov-2026, top three per market on 1H: EURUSD, GBPUSD, USDJPY · GBPJPY, EURJPY, AUDJPY · XAUUSD, XAGUSD, XPTUSD · US500, USTEC, DE40 · XTIUSD, XBRUSD, XNGUSD · BTCUSD, ETHUSD, SOLUSD — `config/scout.json` (TM must list the same symbols in `tm.json`). 03–04 Nov: AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD on both demos |
| Bot name / labels | `Scout` — `Scout-<6hex>-<tf>-<pause start yyyyMMddHHmm>-M` (main stop) / `-R` (reversal stop), e.g. `Scout-3f9a1c-H1-202611031500-M` |
| Config | [`config/scout.json`](../../config/scout.json) (accounts, charts and modes, `lots` and `lotsBySymbol`, expiry, setup settings) — live copy `~/tradebot/config/scout.json`; pop-ups: `notifications.json` |
| Memory | `~/tradebot/run/scout/<broker>-<ctid>.json` (setups that already got an order or alert; survives restarts) |
| Service | `trader-scout` (systemd user service, from `~/tradebot/bin/Scout`) |

## 1. Goal

The owner takes every pause in a running trend with a stop order beyond it (Scout spec §2). Scout does this on many charts at once: it finds the pause on the broker's own candles and places the order the owner would place by hand, so TM can mirror, hedge, protect and trail it. Scout's job ends with that first order.

## 2. Rules

### 2.1 Modes (Scout spec §4.1)

Per chart (symbol × timeframe) in `scout.json`: `"watch"` sends an alert for each new stop level; `"trade"` places the order.

### 2.2 Candles and the detector

- Every chart's **closed** candles come from the broker (`ProtoOAGetTrendbarsReq`), asked for once a candle should have closed; a candle that does not come (market closed) is asked for every 30 s for 10 minutes, then once per candle length. At start, 30 days of candles rebuild the state (their events are not acted on).
- The detector runs Scout_Pine's rules (Scout spec §3.1, §3.2) unchanged; the next timeframe up (1H ← 4H) runs its own detector for the tag *with / against / unknown* (Scout spec §4.4 I1). The tag is logged and shown, never used to skip.

### 2.3 Orders (Trade mode; Scout spec §4.2, §4.4)

| When | Scout |
|---|---|
| A pause gets its stop level (2nd candle) | Places a **Buy Stop / Sell Stop** at the level: its symbol's size (`lotsBySymbol`, else `lots` = 0.1; the broker's minimum and step apply), **stop-loss at the far side of the pause**, **trailing switch on**, **expiry 16 candles** later on the server (good till date), label as above |
| The pause rolls over (reversal stop, Scout spec §2.1 rule 5) | Places the reversal stop the same way, its stop-loss at the main level |
| One order of a pause fills | Cancels the other one (one cancels the other) |
| The setup ends unfilled (trend turned, pause too long or too deep) | Cancels its own unfilled order(s) |
| A level moves on later candles | Nothing: Scout never moves or amends an order |
| A position opened from its order | Nothing: TM manages it (mirror, hedges at H = the stop-loss, break-even at 1R, trailing) |

- Each setup role (pause × main/reversal) gets **at most one order, ever**: the memory file keeps it across restarts. A refused order (e.g. the price is already beyond the level) is logged and not retried.
- After a restart, Scout's pending orders are found by label (`Scout-` + timeframe + pause start), whatever instance placed them.
- Acts only while Primary on the account (open-code-pieces.md §6); a second copy only watches.

### 2.4 With TM

TM takes over orders whose label starts with `"Scout-"` (`takeOver` in `tm.json`, [tm.md §2.1](tm.md#21-take-over-list-v16-owner-24-oct-2026), decision 12): a mirror order of the same size beside Scout's, hedges once both fill. When Scout cancels its order or it expires, TM cancels the mirror ([tm.md §3.1](tm.md#31-entry-by-a-pending-order-owner-22-oct-2026)). Scout never closes a position, so TM's open item about bots that close their own mains does not arise.

## 3. Logs and outputs

`~/tradebot/logs/<broker>/<ctid>/<instanceId>/<date>/`:

| File | One row per |
|---|---|
| `setups.csv` | detector event: `NewStop`, `StopMoved`, `NewReversal`, `ReversalMoved`, `StopFilled`, `ReversalFilled`, `Cancelled` (with reason), `TrendTurned` — with level, far side, pause start, pause candles, pause height and move in ATR, pull-back, ATR, higher-timeframe trend and *with / against* |
| `orders.csv` | order action or result: `place` (ok / refused), `cancel` (with reason), `filled` (fill price, slippage in pips, position), `expired`, `cancelled` (not by Scout), `rejected`, `closed` (gross and net profit of each closing deal of a Scout position), `alert` (Watch mode), `chart` (a picture drawn later for an earlier order, §3.1). Column `chart`: the order's picture (§3.1), on its `place` or `chart` row |
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
| `--candles <SYMBOL> <TF> <from> <file.csv>` | Writes the first account's closed candles to a file (read-only); used for the tests and the review |
| `--replay <file.csv>` | Runs the detector on such a file and prints its events (no connection) |

## 6. Build steps

| Step | Content | Status |
|---|---|---|
| 1 | Detector (Scout_Pine's rules) + higher-timeframe tag; tests: made-up charts, identical events to the prototype on IC Markets EURUSD, GBPJPY, BTCUSD 1H (Aug–Nov 2026) | **Done 03-Nov-2026** — 14 tests |
| 2 | Trader.Core: pending orders with trailing switch and expiry ([Open Code Pieces](../open-code-pieces.md) v1.7) | **Done 03-Nov-2026** — `--test-order` on IC Markets demo: Buy Stop with SL, trailing `True`, `GoodTillDate`, expiry kept by the server; cancelled |
| 3 | Live loop, orders, one-cancels-other, memory, logs, pop-ups; service and deploy script | **Done 03-Nov-2026** — dry run on both demos: 30 days rebuilt on all 12 charts, the same GBPJPY Buy Stop found at both brokers |
| 4 | Trial run with TM (Scout spec §4.6) | **Running from 03-Nov-2026** |
| 5 | Order pictures (§3.1) | **Done 04-Nov-2026** — tests on real EURUSD and BTCUSD candles; the trial's earlier orders drawn at the next start |

## 7. Decisions and open items

Decisions are in Scout spec §4 and §7 (v1.2). Open here:

- **Seen on both demos, 03-Nov-2026 17:46Z:** the first Scout orders (GBPJPY 1H Buy Stop 212.686 / 212.683) filled and TM took them over: mirror filled at the same price, both hedges at Scout's stop-loss, "trailing stops from break-even".
- **Network drop 03-Nov-2026 ≈ 17:57–17:59Z:** Scout and TM stopped with exit code 3 (`ChannelClosedException` from a request in flight); systemd restarted both after 30 s and both picked up their state (TM's two GBPJPY trades, Scout's orders). Fixed in Trader.Core ([Open Code Pieces](../open-code-pieces.md) v1.8): such a request now fails as an I/O error and is retried after the reconnect.
- **Crash on stop, 04-Nov-2026 06:10:39 PKT (01:10:39Z):** stopping the old Scout (`Scout-94a83f`) right after `deploy-scout.sh` had published the new build over its files ended in `Internal CLR error (0x80131506)` and a core dump (exit by signal 6). It was already being stopped; nothing was lost and the new build started cleanly. Cause: the deploy script renamed the running build's folder away and put the new build at its path, so files the old process loaded while stopping came from the new build (same family as AuditBot's 03-Nov crash, audit-bot.md v1.4). **Fixed 04-Nov-2026** ([shared §3](../shared.md#3-machine-and-services) v1.6): each build now goes into its own folder `~/tradebot/bin/Scout-<git hash>` and the link `~/tradebot/bin/Scout` is switched to it; the running Scout keeps its folder until restarted. Tested on Scout the same day: deployed while running, restarted, it runs the new folder and the old one stopped cleanly.
- **IC Markets only, 18 symbols, from 04-Nov-2026 18:14:50Z** (owner, mid-trial; instance `Scout-ca1c1d`, all 18 symbols found; first new orders at once on GBPUSD, DE40 and SOLUSD, mirrored by TM): the top three per market — the Audit Bot's 18 symbols (audit-bot.md §2.1), with EURJPY instead of EURGBP. Pepperstone dropped. TM's IC list has the same symbols plus EURGBP (TM v1.8). Scout's pending orders left on Pepperstone were not cancelled: they expire on the server within 16 candles, and TM still manages any that fill there. The trial review (≈ 10-Nov) therefore covers two set-ups: 03–04 Nov six symbols on both demos, then 18 symbols on IC Markets.
- **Size per symbol, from 04-Nov-2026** (owner: fixed lots per symbol for now, small test sizes; a Scout screen showing risk per trade comes later). 0.1 lot was worth very different money per symbol: a typical 1H move (ATR) was ≈ $15 on forex but $169 on gold, $39 on BTC and $1.70 on ETH; SOLUSD's minimum is 1 lot and natural gas's 0.5. Now `lotsBySymbol` in `scout.json` sets each non-forex symbol so a 1H ATR is worth ≈ $15 per order (forex stays 0.1); the values and their reasoning are in the file. **XNGUSD** cannot go below the broker's minimum 0.5 lot (≈ $135 per 1H ATR, 9× the others) — owner, 04-Nov-2026: stays in trade mode at 0.5 lot; weigh its bigger size at the review. TM's mirror doubles each. The log and pop-up now show the size really sent, as `orders.csv` already did (before, they showed the configured 0.1 — the first SOLUSD order on 04-Nov 18:15Z went out as 1 lot); at start Scout lists every symbol's size and warns (`LotsAdjusted`) when the broker's minimum or step changes one.
- The laptop suspends: no candles, no orders while asleep; pending orders stay on the server and expire. Same as TM (audit-bot.md §9 keeps-awake item).
- A Buy Stop triggers on the ask, the detector reads bid candles: a real fill can come a little before the detector sees the level reached. One cancels the other uses the real fill.

## 8. History

| Version | Date | Change |
|---|---|---|
| v1.0 | 03 Nov 2026 | First issue: Scout built (detector, Trade/Watch modes, orders with stop-loss, trailing and expiry, one cancels the other, logs) and started on both demos for a one-week trial with TM; `"Scout-"` added to TM's `takeOver` ([TM](tm.md) v1.7) |
| v1.1 | 03 Nov 2026 | First fills seen and taken over by TM (§7); Scout saves its memory right after a fill (before: only with the next order); dropped-connection fix in Trader.Core (open-code-pieces v1.8) deployed with TM and Scout |
| v1.2 | 04 Nov 2026 | Order pictures (§3.1, owner): an SVG chart of every Scout order next to the logs, linked from the new `chart` column of `orders.csv`; earlier orders drawn at start (`chart` rows), their higher-timeframe tag worked out again when it was not logged. The trial's 9 earlier orders were drawn twice on 04-Nov (the first set, in `Scout-6af57b`, shows *unknown* for two tags and overlapping weekend dates); the second set is current |
| v1.3 | 04 Nov 2026 | Crash on stop after a deploy (§7) fixed in the deploy scripts ([shared](../shared.md) v1.6): versioned build folders behind the link `~/tradebot/bin/Scout`; tested on Scout |
| v1.4 | 04 Nov 2026 | **IC Markets only, 18 symbols** (owner, §7): top three per market on 1H — forex majors and crosses, metals, indices, energies, crypto; Pepperstone dropped. TM lists the same ([TM](tm.md) v1.8); Scout spec §4.6 updated (Scout v1.3). Open item: one lot size for every symbol |
| v1.5 | 04 Nov 2026 | **Order size per symbol** (owner, §7): `lotsBySymbol` in `scout.json` (≈ $15 per 1H ATR on every symbol; forex 0.1 lot; XNGUSD at its 0.5-lot minimum); log and pop-up show the size really sent; start log lists every symbol's size, `LotsAdjusted` warning. `lots` limit raised from 1 to 100 (SOLUSD needs 20). Tests: 24 (2 new, 1 updated for the IC-only config) |
| v1.6 | 04 Nov 2026 | XNGUSD stays in trade mode at its 0.5-lot minimum (owner, §7); its bigger size is weighed at the review |
