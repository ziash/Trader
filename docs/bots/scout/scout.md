# Scout

**Watches the owner's charts for the "pause, then stop order" setup and places the first stop order of each one; TM manages everything after.** The setup rules, modes and decisions are in Scout's own spec ([github.com/ziash/Scout](https://github.com/ziash/Scout), `docs/spec.md` — the source of truth for *what* Scout looks for); this document covers how the bot runs here. A project of its own; it uses the [Open Code Pieces](../../open-code-pieces.md).

**Related:** [Scout Lens](scout-lens.md) (AI layer) · [TM](../tm.md) (manages Scout's orders) · [Dashboard](../../dashboard/dashboard.md) (Scout page and controls) · [issue log](../../issue-log.md) · [all projects](../../../README.md#projects)

| Item | Value |
|---|---|
| Status | **Running on demo — trial from 03-Nov-2026** (Scout spec §4.6): Trade mode, 1H, small sizes per symbol (§7); review after one week (≈ 10-Nov-2026). From 04-Nov-2026: IC Markets only, 18 symbols; **from 07-Nov-2026 17:27Z: Pepperstone only, same 18 symbols** (§7) |
| Document version | v1.18 (07-Nov-2026) — history in §8 |
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

- Each setup role (pause × main/reversal) gets **at most one order, ever**: the memory file keeps it across restarts. A refused order (e.g. the price is already beyond the level) is logged and not retried. An order held back for margin (§2.5) is not an order: it is tried again while the setup lasts.
- After a restart, Scout's pending orders are found by label (`Scout-` + timeframe + pause start), whatever instance placed them.
- Acts only while Primary on the account (open-code-pieces.md §6); a second copy only watches.

### 2.4 With TM

TM takes over orders whose label starts with `"Scout-"` (`takeOver` in `tm.json`, [tm.md §2.1](../tm.md#21-take-over-list-v16-owner-24-oct-2026), decision 12): a mirror order of the same size beside Scout's, hedges once both fill. When Scout cancels its order or it expires, TM cancels the mirror ([tm.md §3.1](../tm.md#31-entry-by-a-pending-order-owner-22-oct-2026)). Scout never closes a position, so TM's open item about bots that close their own mains does not arise.

### 2.5 Margin budget (v1.7, 06-Nov-2026)

After the IC Markets margin call of 06-Nov-2026 ([issue-log.md #1](../../issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)), Scout checks the account before each new order. It reads the balance, equity and margin in use (read-only, [Open Code Pieces](../../open-code-pieces.md) v1.9) and the margin the order would hold once filled (`ProtoOAExpectedMarginReq`):

- An order of Scout's holds no margin while it waits, but once filled TM mirrors it and hedges both legs: up to **`marginFactor` = 4 ×** its own margin.
- Scout places the order only if **margin in use + 4 × (its waiting orders + this one) ≤ `marginUsePercent` = 50 % of equity**.
- Otherwise: one `skip` row in `orders.csv` (result `no margin`, the numbers in `detail`), a `ScoutNoMargin` line in `events.csv`, and a pop-up at most once an hour. The setup is tried again at each check (every minute) while it lasts.
- No account status (the broker did not answer): no new orders, and Scout asks again after 5 minutes.

On IC Markets at 14:47Z on 06-Nov-2026 (equity $2,491.85, margin in use $2,707.70), the budget was −$1,461: Scout would have placed nothing.

### 2.6 Controls from the dashboard (v1.8, owner 06-Nov-2026)

The owner can steer Scout from the dashboard ([dashboard.md §3.4](../../dashboard/dashboard.md#34-controls-an-extensible-set-of-options-owner-0405-nov-2026)). The site writes a small control file; the laptop fetches it every minute into `~/tradebot/control/scout-<broker>-<ctid>.json`. Scout reads it every 30 s and before each order check, and applies only what the **catalogue** `~/tradebot/config/scout-controls.json` allows (laptop only; reference copy `config/scout-controls.json`).

| Option (catalogue key) | Scope | Values | Scout |
|---|---|---|---|
| `enabled` | symbol | on / off | **Off:** no new orders on the symbol; its pending orders are cancelled (`cancel`, reason *switched off on the dashboard*); positions stay with TM; setups are still logged. **On again:** orders again at the next check (within a minute) for setups that are still valid |
| `timeframe` | symbol | `H1`, `H4`, `D1` | Swaps the symbol's chart for the new timeframe (its higher timeframe as §2.2: H4 for H1, D1 for H4, W1 for D1), loads its history first, and looks for setups there from then on. **Pending orders from the old chart stay** until filled or expired (owner, 06-Nov-2026); one cancels the other still holds for them. Back to the configured timeframe = the same swap |
| `decision` | setup | `keep`, `skip` | **Skip:** that setup's pending orders are cancelled (reason *skipped on the dashboard*) and none placed for it while skipped. Already filled: nothing, TM manages it. **Keep again:** its order is placed again at the next check if the setup is still valid — the one exception to "at most one order per setup role" (§2.3), because the owner asked for it |

- **Setup key** = `<symbol>-<timeframe>-<pause start yyyyMMddHHmm>`, the setup part of Scout's order label; one key covers the main and the reversal stop. Entries older than 7 days are ignored.
- **Every entry is checked again:** an option not in the catalogue or unknown to this Scout, a value the catalogue does not allow, a symbol not in `scout.json` (or with more than one chart, for `timeframe`), a malformed setup key, or a file for another account is **refused — that entry only**; the rest apply. A file Scout cannot read is refused whole: Scout keeps the last good one and warns (pop-up `ControlFileBad`). No file = everything as configured.
- **Acknowledgement:** after applying a changed file Scout writes `~/tradebot/control/scout-<broker>-<ctid>.ack.json`: the file version, when, each entry *applied* (with the orders it cancelled) or *refused* (why), the options this Scout knows, and the last 50 file versions it applied with when (`applied`, kept across restarts, so the site can show when each change was applied). Values as JSON (`false`, not `False`). Events `ControlApplied`, `ControlRefused`, and `ControlReset` for a control put back as configured, in `events.csv`.
- A restart applies the current file again (it describes the wanted state, so applying it twice changes nothing).
- Controls never move, close or resize anything: they only hold Scout back or change which chart it reads. Order size, mode and the symbol list stay in `scout.json`.

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
| `--candles <SYMBOL> <TF> <from> <file.csv>` | Writes the first account's closed candles to a file (read-only); used for the tests and the review |
| `--replay <file.csv>` | Runs the detector on such a file and prints its events (no connection) |

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
- **Daily first (owner, 07-Nov-2026; Scout spec v1.6 §4.7):** daily and monthly charts set the direction and key support/resistance levels; entries come from the pauses on the daily chart down to the chart Scout is set on (on 1H: daily, 4H, 1H — the higher give the first entry, the lower scale up), in that direction only — against it: alerts with a reason, not orders; add-ons the same size; retracement ≤ 0.382 full size, 0.382–0.5 half, > 0.5 skip; one-candle pauses traded; an "Automatic orders" dashboard switch (on for the demo test); 30M and 15M planned as new timeframes. Direction and levels from one monthly and one daily picture per symbol read by Claude ([scout-lens.md](scout-lens.md) §1.2). Measured with the replay before the rules change; nothing built yet.
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
