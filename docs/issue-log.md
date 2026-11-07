# Issue log

**What went wrong with the bots, why, and what was changed so it does not happen again.** A knowledge base for the owner, for Claude and for later bots: read it before changing how a bot sizes, hedges or protects trades. Each issue keeps its facts (times in UTC, numbers from the logs and the broker), its causes, the fix and what is still open.

| Item | Value |
|---|---|
| Document version | v1.3 (08-Nov-2026) — history at the end |
| Where the facts come from | `~/tradebot/logs/<broker>/<account>/` (`orders.csv`, `actions.csv`, `events.csv`), `~/tradebot/run/tm/`, and the broker itself (`dotnet run --project src/Trader.ListAccounts -- --positions`, read-only) |
| Adding an issue | A new `## #n` section from the template at the end, a row in the table below, and a history row; link it from the bot documents it changed |

| # | Date | Account | Summary | State |
|---|---|---|---|---|
| [1](#1-ic-markets-demo-in-a-margin-call-06-nov-2026) | 06-Nov-2026 | IC Markets demo | Too much exposure for the account: margin ran out, hedges were refused, five trades were left without protection, margin call at 92 % | Fixed (TM v1.9, Scout v1.7), running since 16:14 UTC 06-Nov: TM's margin rescue lifted the level to 122 % |
| [2](#2-hedges-and-loss-limit-hit-by-the-daily-rollover-spread-05-nov-2026) | 05-Nov-2026 (found 08-Nov) | IC Markets and Pepperstone demos | At the daily rollover (22:00 UTC) the spread widens for minutes; buy-stop hedges filled on the spiking ask and TM's own rules (hedge stop, re-hedge, 3R loss limit) acted on the spike prices — 4 forex trades closed at the loss limit within 10 minutes of 22:00 | Fix designed ([tm.md §5.2](bots/tm.md#52-spread-guard-and-trigger-on-the-other-side-planned-08-nov-2026)), waiting for the owner's OK to build |

## #1 IC Markets demo in a margin call (06-Nov-2026)

### What happened, in general terms

Scout placed orders on 18 symbols, and TM doubled each one with a mirror and then hedged both legs. Every filled setup became four positions, and none of the bots ever looked at how much of the account was already in use. By the night of 05–06 Nov the open positions held all the margin the account had. From then on the broker refused anything that needed new margin, including the **hedges that are TM's protection**. On five trades TM had already removed the stop-loss because a hedge order was waiting. When those hedge orders triggered, the broker refused them, so the legs were left with **no protection on the server at all**. TM sent the same refused hedge again every second for more than four hours (5,490 refusals). It never changed course, and nobody saw the account numbers. The owner found out from the broker's emails.

The demo money lost is not the main lesson. The bots broke the protection rule ([shared §4](shared.md#4-safety-rules-for-every-bot)), and they could not see the one number that would have warned them: the margin level.

### The account at 14:47 UTC, 06-Nov-2026 (read-only, from the broker)

| | IC Markets demo (10124393) | Pepperstone demo (5357135) |
|---|---|---|
| Balance (start $5,000, [setup.md](setup.md)) | $3,810.94 | $4,607.26 |
| Equity (balance + open result) | **$2,491.85** | $4,477.66 |
| Margin used | $2,707.70 | $831.46 |
| Free margin | **−$215.85** | $3,646.20 |
| Margin level (equity ÷ margin) | **92 %**: below IC's 100 % margin call; IC closes positions itself at 50 % | 539 % |
| Positions / pending orders | 47 / 11 | 8 / 0 |

IC Markets, open positions by symbol:

| Symbol | Positions | Margin (both sides) | Open result | Without stop-loss |
|---|---|---|---|---|
| XNGUSD | 6 | $164.86 | **−$994.50** | 4 |
| SOLUSD | 12 | **$4,487.22** | −$182.98 | 6 |
| EURUSD | 4 | $89.68 | −$128.16 | 2 |
| US500 | 4 | $107.46 | −$46.22 | 2 |
| EURJPY | 4 | $89.78 | −$42.62 | 2 |
| XAUUSD | 2 | $16.23 | −$42.17 | 1 |
| ETHUSD | 2 | $13.34 | −$29.34 | 2 |
| BTCUSD | 3 | $30.22 | −$26.49 | 3 |
| GBPUSD | 4 | $107.94 | −$22.20 | 2 |
| DE40 | 6 | $118.44 | +$187.16 | 2 |

IC counts the bigger side of each symbol only ("MAX" margin on a hedged account). That is why the margin used is $2,707.70 and not the $5,225 the table adds up to.

### Timeline (UTC)

| When | What |
|---|---|
| 04-Nov 18:14 | Scout goes from 6 to 18 symbols on IC Markets, with a fixed size per symbol (scout.md v1.4–v1.6): SOLUSD 20 lots, XNGUSD 0.5 lot (its minimum, about 9× the others). TM mirrors every order |
| 05-Nov | 30 positions closed, net +$146; more trades stay open, each as main + mirror + hedges |
| 06-Nov 00:32 | First "not enough money" refusal: a SOLUSD hedge |
| 06-Nov 01:04 → | Scout's waiting orders start being refused when price reaches them (DE40, ETHUSD, AUDJPY …); Scout keeps placing new ones |
| 06-Nov 09:12 | ETHUSD (short 0.9 lot, main and mirror) reaches its hedge level 2241.97. Both hedge orders trigger, the broker refuses them, and they disappear. The legs have no stop-loss (removed when the hedge orders were placed) |
| 09:12–13:33 | Every second TM sends a market hedge for each ETHUSD leg: 5,448 refusals. When the price dips below the level it places a stop hedge instead (187 times per leg), and each one is refused again when it triggers |
| 13:29 | Another ETHUSD trade reaches TM's 3R loss limit and is closed |
| 14:35 | First check: balance $3,810.94, Scout and TM running |
| 14:47 | New read-only account status (this fix): equity $2,491.85, margin level 92 % |

What did **not** happen: the repeated hedges did not pile up into extra positions, because none of them was ever filled. The 187 "ok" rows in `actions.csv` were stop orders that the broker accepted and then refused when they triggered. Pepperstone was not affected.

### Causes

1. **No bot looked at the account.** Neither Scout nor TM knew the balance, equity, free margin or margin level. The Open API library had no request for the open result, so there was no equity figure at all. TM's design accepted this ([tm.md §8](bots/tm.md#8-accepted-risks), "risk limits not enforced, warning only"), but even the warning was never built.
2. **Exposure ×4 per setup.** A Scout order becomes a main, TM's mirror and two hedges. On a hedged IC account with "MAX" margin, a hedge adds no margin as long as its side is the smaller one. Every mirror doubles the margin, though, and SOLUSD at 20 lots needs much more margin than the other symbols (low leverage on crypto). SOLUSD alone held about 83 % of the margin in use.
3. **Sizes set by price movement, not by the account.** `lotsBySymbol` made a 1H move worth about $15 on every symbol, but nothing tied the total to the account size. XNGUSD at its 0.5-lot minimum is worth about $135 per 1H move; its two legs alone were losing $994.50.
4. **Protection that depends on money.** Once a hedge order was waiting, TM removed the leg's stop-loss, so the hedge became the only protection. A hedge needs margin, and a refused hedge order simply disappears. Five trades ended up with no server-side protection.
5. **Retry without a limit.** TM's rule is "plan again every second". That heals missed messages, but against a refusal that won't change it just loops. Each retry also sent a "refused" pop-up and an `events.csv` line (10,989 warnings that day).
6. **Scout kept adding.** New orders went out on an account that had no money for them. They were refused when price reached them, and Scout tried the next setups anyway.

### Solution (06-Nov-2026)

The bots now see the account and act on it themselves. Nobody closes anything by hand.

| Part | Change | Where |
|---|---|---|
| **Account status for every bot** | New read-only calls: balance, equity (balance + each position's net open result, by the broker's own `ProtoOAGetPositionUnrealizedPnLReq`, written by hand because the library is older), margin used (by the broker's SUM / MAX / NET rule), free margin, margin level; and the margin a new order would hold (`ProtoOAExpectedMarginReq`) | Trader.Core `AccountStatus`, `CTraderSession.GetAccountStatusAsync` / `GetExpectedMarginAsync`, `UnrealizedPnLMessages.cs` ([Open Code Pieces](open-code-pieces.md) v1.9) |
| **Backstop stop-loss** | A leg is never without a stop on the server. As long as no hedge is filled (hedge order waiting, refused or not placed), TM keeps a stop-loss at the 3R loss limit on it. Only a filled hedge replaces it | TM `Planner` ([tm.md §5.1](bots/tm.md#51-margin-and-the-backstop-v19-06-nov-2026)) |
| **No money for a hedge** | After a "not enough money" refusal, a stop hedge is tried again after a minute, not every second. If the price is already past the hedge level, TM **closes the leg**: that freezes the loss just as the hedge would, and frees margin instead of needing it | TM `Planner`, `LegState.NoMoneyUtc` |
| **No mirror when margin is short** | Below a margin level of 300 % (`marginWarnLevel`), new trades get hedges but no mirror, so exposure is not doubled | TM `Planner` |
| **Margin rescue** | Below 100 % (`marginRescueLevel`, IC's margin call), TM closes one whole trade (main, mirror, hedges): the one holding the most margin. It then reads the account again and closes the next only if the level is still too low. That way TM decides which trades go, before the broker's stop-out at 50 % closes positions in its own order | TM `Planner`, `AccountManager` |
| **Scout's margin budget** | Scout places an order only if the margin in use, plus 4 × the margin of each of its waiting orders and of the new one (mirror and hedges), stays within 50 % of equity (`marginUsePercent`, `marginFactor`). Otherwise it logs `skip` / `no margin` in `orders.csv` once and tries again at the next check while the setup lasts | Scout `MarginBudget`, `AccountScout` ([scout.md §2.5](bots/scout/scout.md#25-margin-budget-v17-06-nov-2026)) |
| **Seeing it** | TM logs an `AccountStatus` line every 15 minutes, and a margin-low pop-up at most once an hour. A refused action pops up once per 10 minutes, not every second. `ListAccounts` shows equity, margin and margin level; `--positions` shows them by symbol | TM `AccountManager`; `src/Trader.ListAccounts` |

Tests: 254 in all, up from 237. New: TM `MarginTests` (9: the ETHUSD case, waiting after a refusal, no mirror, rescue one at a time, restart, a missed backstop reply) and a loss-limit test, Core `AccountStatusTests` (4), Scout `MarginBudgetTests` (3). Six TM tests were updated, because they expected the stop-loss to be removed where it now becomes the backstop.

### What the bots will do once restarted with the fix

At 92 % the margin rescue starts at once. TM closes the trade holding the most margin (one of the three SOLUSD trades, which hold most of it), reads the account again and closes the next one if the level is still below 100 %. It also sets backstop stop-losses on every leg that has no filled hedge. A leg that is past its hedge level without a hedge gets one market hedge attempt, and is closed if that is refused. Scout places no new orders until the budget allows it. These closes realise part of today's open loss of about −$1,319.

### What the bots did after the restart (06-Nov-2026, UTC)

| When | What |
|---|---|
| 16:12:07 | The owner deploys and restarts both bots. Scout starts on 34ae014. TM restarts on the **old build** (2b56e33, 03-Nov): its `~/tradebot/bin/TM` was still a plain folder from before versioned folders, and a running process used it, so `deploy-tm.sh` built `TM-34ae014` but left the link alone and printed the one-time switch command ([shared §3](shared.md#3-machine-and-services)). The old TM goes on as before |
| 16:13 | IC Markets read-only: balance $3,426.47, equity $2,289.48, margin $2,638.86 used, free −$349.38, **level 87 %**, 43 positions / 15 orders |
| 16:14:24 | The owner runs the one-time switch (stop TM, rename the plain folder to `TM-2b56e33`, link `TM` → `TM-34ae014`, start TM). From now on a plain restart picks up a TM deploy, as for Scout |
| 16:14:28 | TM logs its first `AccountStatus`: equity $2,270.68, free −$368.18, level 86 %, plus a `MarginLow` pop-up |
| 16:14:28–30 | **Margin rescue:** TM closes the SOLUSD trade P692692856, which holds the most margin ($1,536.62): main, mirror and both filled hedges. The hedges had locked most of its loss, so the close cost $32.12 (balance $3,426.47 → $3,394.35). The whole trade, with its first hedge pair stopped out on 05-Nov (−$13.08), lost $45.20 — the main alone −$131.52 (the broker's deals, read for the dashboard) |
| 16:14:30–31 | **Backstops:** stop-losses at the 3R loss limit on 7 legs that had none and no filled hedge: XNGUSD (main, mirror) 2.897, BTCUSD (main, mirror) 77,849.83, ETHUSD (main, mirror) 2,281.99, XAUUSD (main) 3,952.37 |
| 16:14:56 | IC Markets read-only: balance $3,394.35, equity $2,278.93, margin $1,868.02 used, free **+$410.91**, **level 122 %**, 39 positions / 15 orders. Above 100 %, so TM closes nothing more. No refused-action loop since the switch |

Every position still without a stop-loss (18 of 39) is a leg with a filled hedge, or one of those hedges, on XNGUSD, SOLUSD, EURUSD, US500, EURJPY, DE40 and GBPUSD. That is the design: the filled hedge freezes the loss in place of the stop-loss. At 122 % TM opens no mirrors (below 300 %), and Scout places no orders while the margin in use is above half the equity. Pepperstone stayed at 539 % throughout.

Lesson for deploys: read the output of the deploy script before restarting. A "Not switched yet" line means the restart will run the old build.

### Still open

- **Sizes against the account** (owner's decision): `lotsBySymbol` sets the value of a 1H move, not a share of the account. XNGUSD (0.5-lot minimum) and SOLUSD (20 lots, high margin) are the outliers. Scout's trial review (≈ 10-Nov) should decide: drop them, size them from the account, or cap the risk per trade.
- ~~**Dashboard:** show the account status~~ — done 06-Nov-2026 in [dashboard](dashboard/dashboard.md) build step 5a: the Accounts page shows balance, equity, margin and the margin level coloured by TM's 100 % / 300 % rules; the Trades page shows each trade's whole result, its picture and TM's actions, refusals counted.
- **Rewind** (the replay simulator, a separate project) does not answer the two new requests yet. Under replay, TM runs without its margin rules and Scout places no orders until it does.
- **Values to tune:** 300 % / 100 % levels, 50 % budget, factor 4.

## #2 Hedges and loss limit hit by the daily rollover spread (05-Nov-2026)

### What happened, in general terms

Every day at 17:00 New York (22:00 UTC since US winter time began on 01-Nov) brokers roll positions over to the next day, and for a few minutes liquidity is thin: the **spread widens many times over**, mostly on one side of the price. A buy-stop is triggered by the **ask**, so TM's buy-stop hedges filled although the bid — the price on the chart — never reached them. TM then saw the bid already below the new hedge's stop and closed the hedge at once, placed a new hedge that filled again on the same wide ask, and counted all of it against the leg: the **3R loss limit** closed the whole trade. All within four seconds. A stop-loss would not have done better — on a short it is triggered by the ask as well — but it would have lost one spread once, not the chain.

Found by the owner on 08-Nov from Scout's order picture of GBPUSD (`closed −$15.91`), which shows only Scout's own position; the whole trade lost about **$95**.

### The facts (from TM's `events.csv` / `actions.csv` and the dashboard's `deals.csv`, IC Markets demo 49123893)

GBPUSD trade P692923991: Scout's Sell Stop 1.34498 filled at 1.34493 on 05-Nov 21:00:17 (main and mirror, 0.1 lot each); TM removed the stop-loss and placed two buy-stop hedges at 1.34634 (1R = 0.00141 = 14.1 pips).

| Position | Opened | Closed | Net |
|---|---|---|---|
| Main (sell) | 1.34493 | 1.34640 | −$15.91 |
| Mirror (sell) | 1.34493 | 1.34625 | −$14.41 |
| Hedge 1, main (buy) | 1.34644 | 1.34484 | −$16.80 |
| Hedge 1, mirror (buy) | 1.34644 | 1.34484 | −$16.80 |
| Hedge 2, main (buy) | 1.34640 | 1.34505 | −$14.30 |
| Hedge 2, mirror (buy) | 1.34640 | 1.34505 | −$14.30 |
| **Whole trade** | | | **≈ −$95** (with the opening commissions) |

At 22:01:00–22:01:01 the ask was 1.34644 while the bid was 1.34484: a **16-pip spread** (GBPUSD normally ≈ 0.2 pip at IC Markets).

**The same pattern elsewhere** — loss-limit closes within 10 minutes after 22:00 UTC (17 trades were closed by the loss limit from 03 to 07-Nov):

| When (UTC) | Broker | Trade | Loss limit at |
|---|---|---|---|
| 03-Nov 22:06 | Pepperstone | GBPJPY (hedge filled 22:06:11, closed 22:06:12) | −4.1R |
| 03-Nov 22:10 | IC Markets | GBPJPY | −3.4R |
| 04-Nov 22:02 | Pepperstone | AUDJPY | −3.4R |
| 05-Nov 22:01 | IC Markets | GBPUSD (above) | −3.0R |

(EURUSD at IC Markets on 05-Nov 22:33 may be the same; spreads can stay wide for half an hour.)

### Timeline (UTC, 05-Nov)

| Time | What happened |
|---|---|
| 21:00:16–18 | Sell filled; mirror; two buy-stop hedges at 1.34634; stop-losses removed (before TM's backstop, v1.9) |
| 22:01:00 | Rollover spread: both hedges filled at 1.34644 on the ask (`HedgeFilled`, hedge stop 1.34573) |
| 22:01:01 | Bid 1.34484 already below the hedge stop: TM closed both hedges at market (−1.13R each, `HedgeClosed`) |
| 22:01:02 | TM placed new hedges at 1.34634; they filled at once at 1.34640; `LossWarning` −2.2R |
| 22:01:03–04 | Loss limit 3R: main, mirror and both hedges closed (`LossLimit`, `TradeDone`) |

### Causes

1. **Stop orders triggered by the side that spikes.** A buy-stop fills on the ask, a sell-stop on the bid (cTrader's default). At the rollover one side jumps by many spreads.
2. **TM acts on spike prices.** Its client-side rules — close a hedge whose stop the price has passed, re-hedge, the 2R warning and the 3R limit — use the current bid/ask without asking whether the spread is normal.
3. **Visibility:** Scout's picture tags the trade with Scout's own position only (−$15.91), so the real loss (−$95) was not visible there.

### Solution (designed 08-Nov-2026, not built yet)

Owner's choice: the stop orders' **trigger on the other side** for TM's hedges, plus a **spread guard** on TM's own actions, with a **rollover window** from the broker's trading hours as a backup. Design: [tm.md §5.2](bots/tm.md#52-spread-guard-and-trigger-on-the-other-side-planned-08-nov-2026), waiting for the owner's OK to build.

### Still open

- Scout's entry orders: the same trigger choice? (owner to decide)
- Scout's picture: tag the whole trade's result instead of Scout's position? (owner to decide)
- Testing the fix with Rewind (the replay simulator) on a rollover spike before it goes live.

## History

| Version | Date | Change |
|---|---|---|
| v1.0 | 06 Nov 2026 | First issue: #1, IC Markets demo margin call; the account status calls and the bots' margin rules (TM v1.9, Scout v1.7, Open Code Pieces v1.9, shared v1.11) |
| v1.1 | 06 Nov 2026 | #1: what the bots did after the restart (TM's one-time folder switch, margin rescue of one SOLUSD trade, 7 backstops, IC from 86 % to 122 %); state updated |
| v1.2 | 06 Nov 2026 | #1: the SOLUSD trade's whole result (−$45.20) from the broker's deals; the dashboard item done (dashboard step 5a) |
| v1.3 | 08 Nov 2026 | **#2** rollover spread: hedges and TM's loss limit hit by the 22:00 UTC spread spike (GBPUSD −$95 whole trade; 4 such trades 03–05 Nov); fix designed in tm.md §5.2 |

## Template

```
## #n Title (dd-Mon-yyyy)

### What happened, in general terms
### The facts (account, positions, numbers — with where they come from)
### Timeline (UTC)
### Causes
### Solution (what changed, where)
### Still open
```
