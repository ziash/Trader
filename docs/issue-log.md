# Issue log

**What went wrong with the bots, why, and what was changed so it does not happen again.** A knowledge base for the owner, for Claude and for later bots: read it before changing how a bot sizes, hedges or protects trades. Each issue keeps its facts (times in UTC, numbers from the logs and the broker), its causes, the fix and what is still open.

| Item | Value |
|---|---|
| Document version | v1.0 (06-Nov-2026) — history at the end |
| Where the facts come from | `~/tradebot/logs/<broker>/<account>/` (`orders.csv`, `actions.csv`, `events.csv`), `~/tradebot/run/tm/`, and the broker itself (`dotnet run --project src/Trader.ListAccounts -- --positions`, read-only) |
| Adding an issue | A new `## #n` section from the template at the end, a row in the table below, and a history row; link it from the bot documents it changed |

| # | Date | Account | Summary | State |
|---|---|---|---|---|
| [1](#1-ic-markets-demo-in-a-margin-call-06-nov-2026) | 06-Nov-2026 | IC Markets demo | Too much exposure for the account: margin ran out, hedges were refused, five trades were left without protection, margin call at 92 % | Fixed in code (TM v1.9, Scout v1.7); waiting for the owner's OK to restart the bots |

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
| **Scout's margin budget** | Scout places an order only if the margin in use, plus 4 × the margin of each of its waiting orders and of the new one (mirror and hedges), stays within 50 % of equity (`marginUsePercent`, `marginFactor`). Otherwise it logs `skip` / `no margin` in `orders.csv` once and tries again at the next check while the setup lasts | Scout `MarginBudget`, `AccountScout` ([scout.md §2.5](bots/scout.md#25-margin-budget-v17-06-nov-2026)) |
| **Seeing it** | TM logs an `AccountStatus` line every 15 minutes, and a margin-low pop-up at most once an hour. A refused action pops up once per 10 minutes, not every second. `ListAccounts` shows equity, margin and margin level; `--positions` shows them by symbol | TM `AccountManager`; `src/Trader.ListAccounts` |

Tests: 254 in all, up from 237. New: TM `MarginTests` (9: the ETHUSD case, waiting after a refusal, no mirror, rescue one at a time, restart, a missed backstop reply) and a loss-limit test, Core `AccountStatusTests` (4), Scout `MarginBudgetTests` (3). Six TM tests were updated, because they expected the stop-loss to be removed where it now becomes the backstop.

### What the bots will do once restarted with the fix

At 92 % the margin rescue starts at once. TM closes the trade holding the most margin (one of the three SOLUSD trades, which hold most of it), reads the account again and closes the next one if the level is still below 100 %. It also sets backstop stop-losses on every leg that has no filled hedge. A leg that is past its hedge level without a hedge gets one market hedge attempt, and is closed if that is refused. Scout places no new orders until the budget allows it. These closes realise part of today's open loss of about −$1,319.

### Still open

- **Sizes against the account** (owner's decision): `lotsBySymbol` sets the value of a 1H move, not a share of the account. XNGUSD (0.5-lot minimum) and SOLUSD (20 lots, high margin) are the outliers. Scout's trial review (≈ 10-Nov) should decide: drop them, size them from the account, or cap the risk per trade.
- **Dashboard:** show the account status (equity, margin level) on the Status and Accounts pages; build step 5 ([dashboard.md](dashboard.md)).
- **Rewind** (the replay simulator, a separate project) does not answer the two new requests yet. Under replay, TM runs without its margin rules and Scout places no orders until it does.
- **Values to tune:** 300 % / 100 % levels, 50 % budget, factor 4.

## History

| Version | Date | Change |
|---|---|---|
| v1.0 | 06 Nov 2026 | First issue: #1, IC Markets demo margin call; the account status calls and the bots' margin rules (TM v1.9, Scout v1.7, Open Code Pieces v1.9, shared v1.11) |

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
