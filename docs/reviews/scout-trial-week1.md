# Scout trial — week 1 review

**Scout placed 190 stop orders in six days; 85 filled; the Scout legs alone came out near flat (−4.3R, −$224) and the whole TM-managed groups lost 41R (−$1,905) over the 71 trades that have fully closed.** The gap is TM's hedging and the 3R loss limit on two legs: a winner pays about +2R, a loser costs about −6R. This is a scoring report on the logs of both bots. **Everything here is a proposal; no rule, config or code was changed.**

| Item | Value |
|---|---|
| Report version | v1.0 (11-Nov-2026) — history at the end |
| Covers | Scout trial from **03-Nov-2026 17:41Z** to Scout's last log line **09-Nov-2026 18:14Z** (144.6 h). Review run 11-Nov-2026 14:40Z |
| Accounts | brokerA = IC Markets demo 49123893 · brokerB = Pepperstone demo 49124666 — demo only |
| Data | `~/tradebot/logs/<broker>/<ctid>/{Scout-*,TM-*,Dashboard}/<date>/*.csv`, `~/tradebot/run/{scout,tm}/*.json`, `journalctl --user -u trader-scout -u trader-tm`. Read-only |
| Scope | Scout's own orders and the TM groups built on them. Not the detector's accuracy (no missed-setup check — that is [Scout Lens](../bots/scout/scout-lens.md) §1.1) |

**Terms.** *Role* — one side of one pause: the main stop (**M**) or the reversal stop (**R**); each role gets at most one order, ever. *R* — the money risked on Scout's own leg, `|fill − stop-loss| × lots × value per price unit`; a result "in R" is that result divided by it. *Group* — Scout's position plus TM's mirror plus every hedge of both; its result is every deal of the trade. *Break-even* — TM's rule: at +1R a leg's stop moves to its entry and its hedge is cancelled, so that leg can no longer lose. *Rollover* — the daily market break (17:00 New York, 22:00Z in November) when spreads widen many times over.

## 1. Summary

1. **Scout's own entries are roughly a coin toss with a small edge, and no more:** 44 of 71 closed trades won (62 %), but average **−0.06R** and **−$224** in all. On this sample Scout neither makes nor loses money.
2. **TM's management turns that into a real loss:** the same 71 trades as whole groups won 41 of 71 (58 %) and lost **41R / −$1,905**, because a loser costs about 6R (3R limit × two legs, plus hedge churn) while a winner pays about 2R.
3. **One thing decides the result: whether the trade reaches +1R.** The 52 that did average **+1.52R** per group; the 19 that did not average **−6.32R**. Nothing in the pause's own numbers (candles, height, move, pull-back) separates winners from losers.
4. **Two conditions do the damage:** groups whose last leg closed in the rollover window (21:00–02:00Z) average **−2.51R** (n=22) against **+0.29R** elsewhere (n=49); and the 4H trend tag points the opposite way to the hypothesis — *against* the 4H trend averaged **+0.78R**, *with* it **−1.40R**.
5. **The run was not clean:** Scout was up 73.6 % of the window, crashed three times, and was **down for 44.4 h** from 09-Nov 18:14Z after TM's token-refresh loop (started again by hand on 11-Nov 14:36Z); 5,664 hedge orders were refused for lack of money during the IC Markets margin call.

## 2. What ran

The trial was not one experiment. It changed four times while it ran, so almost every number below rests on a different mix of symbols, sizes and rules. The task brief described only the first day's set-up (0.1 lot, six symbols, both brokers); the logs show this:

| From | Accounts | Symbols | Size | Other rules |
|---|---|---|---|---|
| 03-Nov 17:41Z | both demos | 6 (AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD) | 0.1 lot flat | — |
| 04-Nov 18:14Z | brokerA only | 18 (top three per market) | `lotsBySymbol`, ≈ $15 per 1H ATR | — |
| 07-Nov 17:27Z | brokerB only | same 18, five renamed | same $ per move | TM spread guard + `DoubleOpposite` trigger live 07-Nov 19:39Z |
| 08-Nov 13:33Z | brokerB only | same 18 | **0.5 % of equity per trade** (v1.28) | review Phase 1 live |
| 09-Nov 01:40Z / 12:50Z | brokerB only | same 18 | same | portfolio limits **on** (v1.35); **daily direction on** (v1.41) |

Common to all of it: 1H charts, stop-loss at the far side of the pause, trailing switch on, server expiry 16 candles, main and reversal of one pause one-cancels-the-other, the 4H trend tagged but never used to skip.

## 3. Scoring

### 3.1 From setup to fill

Detector events in the window (`setups.csv`):

| Event | brokerA | brokerB | Total |
|---|---|---|---|
| `NewStop` (a pause got its main level) | 116 | 42 | 158 |
| `NewReversal` (the pause rolled over) | 51 | 18 | 69 |
| `StopMoved` | 1 | 0 | 1 |
| `StopFilled` | 65 | 22 | 87 |
| `ReversalFilled` | 33 | 13 | 46 |
| `Cancelled` (setup ended) | 68 | 26 | 94 |
| `TrendTurned` | 20 | 13 | 33 |

Scout acted on **236 roles** inside the window (17 of them have no `setups.csv` row — found while the candle history was being rebuilt, so their 4H tag is unknown):

| Step | brokerA | brokerB | Combined |
|---|---|---|---|
| Roles Scout acted on | 168 | 68 | 236 |
| Order placed and accepted | 150 | 40 | **190** |
| Refused at placement (all `LEVEL_PASSED`) | 0 | 8 | 8 |
| Never sent | 18 | 20 | 38 |
| Filled | 66 | 16 | **82** |
| **Fill rate** (filled ÷ placed) | **44 %** | **40 %** | **43 %** |
| Cancelled by Scout, unfilled | 56 | 13 | 69 |
| Expired on the server | 2 | 7 | 9 |
| Still pending at the end of the window | 26 | 4 | 30 |
| Fill rejected by the broker (`NOT_ENOUGH_MONEY`) | 26 | 0 | 26 |

Three of those 30 pending orders filled later with no Scout watching: two on brokerB on 04-Nov 22:35Z and 22:41Z (BTCUSD and ETHUSD, both the 16:00 main stop, left behind when Scout moved to brokerA), and XTIUSD on brokerB after Scout crashed on 09-Nov. **85 Scout orders really filled**; Scout's logs show 83 of them (the 82 above plus XTIUSD, recovered from the broker at the 11-Nov restart).

Why an order was never sent (last reason logged, 38 roles): **no margin 24**, against daily 7, size below the broker's minimum 6, portfolio limit 1. Why Scout cancelled its own order (69): the other order of the pair filled 33, pause too long or too deep 16, reversal stop filled 10, stop filled 4, trend turned 4, setup ended 1, against daily 1.

Three numbers to note:

- **26 fill rejections, all on brokerA on 06-Nov**, when the IC Markets demo was in a margin call ([issue #1](../issue-log.md)). The order triggered, the broker refused it for lack of money, and the setup got nothing. That is 26 of 150 brokerA orders — 17 % of the account's orders lost to margin, on top of the 24 never sent for the same reason.
- **30 orders left pending** with nothing further logged (26 on brokerA from 06-Nov, 4 on brokerB). They expire on the server by themselves, but Scout writes no outcome for them, and three of them filled while no Scout was watching the account (§4).
- All 8 placement refusals are `LEVEL_PASSED` — the live-price check from review Phase 1 (F-10) doing its job. All 8 are on brokerB on 09-Nov, the first full day with the check and the daily-direction rule both live.

### 3.2 Headline — the 71 fully closed groups

Of the 83 fills Scout logged: **71 fully closed**, **7 still open** at review time (per TM's memory), **5 closed after the dashboard export's last deal snapshot** (Scout's leg is known, TM's legs are not — see §5). The 71 are the only set with complete money, so every number below rests on them.

| Measure | Scout's leg alone | Whole TM group |
|---|---|---|
| Trades | 71 | 71 |
| Winners | 44 (62 %) | 41 (58 %) |
| Average result | **−0.06R** | **−0.58R** |
| Median result | +0.13R | +0.09R |
| Average winner | +1.03R | +1.96R |
| Average loser | −1.84R | −4.04R |
| Best | +5.28R (US500 R) | +10.58R (US500 R) |
| Worst | −10.32R (SOLUSD M) | −8.53R (GBPJPY M) |
| Sum | **−4.3R** | **−41.0R** |
| Net money | **−$224.37** | **−$1,905.30** |
| Gross money | −$245.30 | −$1,880.52 |
| Costs (net − gross) | +$20.93 | −$24.78 |
| Longest run of losers | 8 | 9 |

*Expectancy* — what one more trade is worth on this evidence — is **−0.06R** for Scout's leg and **−0.58R** for the group. **71 trades is a small sample**; a single trade is worth 1.4 % of it, and the three biggest losers carry a quarter of the group loss. Treat every figure here as a direction to test, not a measurement.

Costs are small and on Scout's leg slightly *positive*: swap credits on the short oil and JPY positions outweighed commission. Costs are not what is wrong.

Account level, for scale: brokerA's balance fell from $3,394.35 (06-Nov 16:46Z) to $2,302.88 (10-Nov 04:02Z) with equity $1,772.58, i.e. about another $530 unrealised on the six trades still open. brokerB's balance rose from $4,607.26 to $4,670.83. Across all Scout and TM deals in the window (not only the 71): brokerA **−$2,161.84**, brokerB **−$202.19**.

### 3.3 The one split that matters: did the trade reach +1R?

| | Trades | Group average | Group sum | Scout leg average |
|---|---|---|---|---|
| Reached +1R (break-even set, hedge cancelled) | 52 | **+1.52R** | +79.1R | +0.87R |
| Never reached +1R | 19 | **−6.32R** | −120.1R | −2.60R |

Once a leg is at break-even it cannot lose, so the whole loss lives in the 19 trades that never got there. All ten of the worst groups are in that 19.

### 3.4 Breakdowns (the 71 closed groups)

**4H trend tag** (`with_higher`). The trial was meant to answer "is it worth skipping *against* setups?" (Scout spec §4.4 I1, §7 q9). The logs say the opposite:

| Tag | n | Winners | Group avg | Group sum | Scout leg avg |
|---|---|---|---|---|---|
| **against** the 4H trend | 25 | 18 (72 %) | **+0.78R** | +19.6R | +0.21R |
| **with** the 4H trend | 44 | 21 (48 %) | **−1.40R** | −61.7R | −0.23R |
| not tagged | 2 | 2 | +0.53R | +1.1R | +0.27R |

**Main vs reversal stop.** Reversal stops — the owner's "failed pause" entry — were the better half:

| Role | n | Winners | Group avg | Group sum | Scout leg avg |
|---|---|---|---|---|---|
| M (main) | 56 | 30 (54 %) | **−1.03R** | −57.7R | −0.24R |
| R (reversal) | 15 | 11 (73 %) | **+1.11R** | +16.7R | +0.61R |

**Broker.** The same rules ran at both; brokerA carried almost all the trades, so this says little:

| Broker | n | Winners | Group avg | Group sum |
|---|---|---|---|---|
| brokerA (IC Markets) | 59 | 35 (59 %) | −0.44R | −25.8R |
| brokerB (Pepperstone) | 12 | 6 (50 %) | −1.27R | −15.2R |

**Did the two brokers diverge on the same setup?** 21 setups were seen at both (03–04 Nov and the 06-Nov overlap). 16 of 21 got the same placement decision; 8 filled at both. The pairs agree closely — same side, fills within a few ticks, same direction of result:

| Setup | brokerA group | brokerB group |
|---|---|---|
| GBPJPY 03-Nov 15:00 M | +0.82R | +0.25R |
| GBPJPY 03-Nov 19:00 M | −7.27R | −8.53R |
| EURUSD 04-Nov 04:00 M | −0.10R | −0.03R |
| AUDJPY 04-Nov 10:00 M | −6.58R | −8.00R |
| EURUSD 04-Nov 10:00 M | −6.81R | −6.69R |
| GBPJPY 04-Nov 10:00 R | +0.22R | +0.24R |
| BTCUSD 04-Nov 11:00 M | +4.58R | +1.78R |
| ETHUSD 04-Nov 11:00 M | +5.09R | +4.78R |

The detector agrees across brokers; the five disagreements are all margin and fill luck, not different setups (four brokerA "no margin" skips on 06-Nov, one ETHUSD that filled at brokerA and not at brokerB). **The two-broker comparison has done its job: the detector is reproducible.** Nothing further is learnt by running both.

**Per symbol** (group R; 18 symbols, most with 1–5 trades — read as a watch-list, not a verdict):

| Symbol | n | Winners | Group avg | Group sum |
|---|---|---|---|---|
| XBRUSD | 5 | 5 | +3.12R | +15.6R |
| BTCUSD | 7 | 7 | +2.08R | +14.5R |
| US500 | 1 | 1 | +10.58R | +10.6R |
| XAUUSD | 3 | 2 | +1.82R | +5.5R |
| XTIUSD | 7 | 4 | +0.80R | +5.6R |
| DE40 | 2 | 2 | +2.21R | +4.4R |
| EURJPY | 2 | 2 | +1.96R | +3.9R |
| USDJPY | 1 | 1 | +0.99R | +1.0R |
| XAGUSD | 2 | 2 | +0.35R | +0.7R |
| USTEC | 1 | 0 | −0.02R | −0.0R |
| SOLUSD | 3 | 1 | −1.90R | −5.7R |
| ETHUSD | 7 | 3 | −1.26R | −8.8R |
| XNGUSD | 2 | 0 | −5.94R | −11.9R |
| GBPJPY | 8 | 6 | −1.62R | −13.0R |
| GBPUSD | 2 | 0 | −7.17R | −14.3R |
| AUDJPY | 5 | 1 | −2.92R | −14.6R |
| EURUSD | 9 | 3 | −1.83R | −16.5R |
| XPTUSD | 4 | 1 | −4.50R | −18.0R |

The pattern: **commodities, crypto and indices paid; the forex majors and crosses did not** (EURUSD, AUDJPY, GBPUSD, GBPJPY together −58.4R on 24 trades). XPTUSD and XNGUSD lose on size and spread, not on direction (§4).

**Hour of the day.** Fills are spread over the clock; the losses are not. The 22:00Z hour alone (n=4) is −21.6R, and 03:00Z and 12:00Z carry another −31.8R. Judged on the group's last close instead:

| Last leg closed | n | Winners | Group avg | Group sum | Scout leg sum |
|---|---|---|---|---|---|
| 21:00–01:59Z (rollover) | 22 | 11 | **−2.51R** | −55.3R | −11.7R |
| any other hour | 49 | 30 | **+0.29R** | +14.2R | +7.4R |

**Pause features — winners vs losers.** No separation at all:

| Feature | Winners (n=39) median / mean | Losers (n=30) median / mean |
|---|---|---|
| `pause_candles` | 2.00 / 2.56 | 2.00 / 2.27 |
| `pause_height_atr` | 1.26 / 1.36 | 1.21 / 1.30 |
| `move_atr` | 5.24 / 5.83 | 5.41 / 5.71 |
| `pullback` | 0.25 / 0.28 | 0.29 / 0.29 |

The detector's own numbers do not predict the result. Whatever separates a good pause from a bad one on this sample is not in `scout.json`'s thresholds — which is the case Scout Lens was proposed to answer.

**Time to fill, and the 16-candle expiry.** Median 0.8 h from placement to fill; 55 % fill inside the first hour, 88 % inside four, 99 % inside twelve. Only 9 of 190 orders ever reached expiry. The nine fills that came later than 4 h were worth **−8.6R** as groups (−6.8R on Scout's leg).

**Slippage.** Median **0.6 pips** over 82 fills with a slippage figure. Forex and indices are near zero; the outliers are all crypto and metals at the moment of a fast move (BTCUSD 975 pips = $9.75 at 0.1 lot on 04-Nov 14:40Z, ETHUSD 253, SOLUSD 400, XPTUSD 229). Slippage is not a problem at these sizes.
## 4. TM on Scout's trades

TM did what tm.md says, promptly and almost without exception. The damage comes from the rules themselves, not from TM failing to follow them.

| Check | Result |
|---|---|
| A mirror order beside every Scout order | **186 of 190** (98 %). All 186 accepted, none refused. The four without one are all brokerA (EURUSD 04-Nov 19:00 R, XPTUSD 05-Nov 03:00 M, EURUSD 05-Nov 04:00 R, XTIUSD 05-Nov 23:00 M) |
| How fast | Scout's `place` → TM's `PlaceMirrorOrder`: median **1.0 s**, mean 1.4 s, 90th percentile 3.0 s, worst 5.0 s |
| TM's own request latency | median **434 ms**, 90th percentile 540 ms, worst 633 ms (n=91 rows carrying a figure) |
| Hedges at Scout's stop-loss | Every one of the 83 filled trades got `PlaceHedge` rows. 66 used only Scout's stop-loss price; 15 also show later prices — the re-hedge rules of tm.md §4 after a hedge closed, not a wrong level |
| Break-even at +1R | **57 of 83** filled trades (69 %) reached it; 45 on brokerA, 12 on brokerB |
| Trailing | Scout sets the trailing switch on every order, so TM set the break-even stops as server-side trailing stops; `BreakEven` lines say "trailing" from 04-Nov on |
| Mirror cancelled with Scout's order | 107 Scout orders ended unfilled with a mirror beside them; **26 have no `CancelOrder` row** — all brokerA on 06-Nov, during the margin call and the hours before Scout left the account |
| Orphans | None found in TM's memory: `~/tradebot/run/tm/*.json` holds 9 trades, every one of them a real open Scout group |
| Errors | `ClosePosition` `POSITION_NOT_FOUND` 13, `POSITION_LOCKED` 3, `CancelOrder` `ORDER_NOT_FOUND` 2, `OpenMirror` `NOT_ENOUGH_MONEY` 2, `SetProtection` `TRADING_BAD_STOPS` 1 — and **`PlaceHedge` `NOT_ENOUGH_MONEY` 5,664** |

Three things stand out.

**1. 5,664 refused hedges.** TM sent 6,607 `PlaceHedge` requests and only 943 succeeded. Every refusal is a `HedgeAtMarket` warning as well (5,668). They are all brokerA, from the 06-Nov margin call onward: the price was already past H, a hedge at market was needed, there was no money for it, and TM asked again a minute later, for hours. The protection rule held — the backstop was on the server — but the retry loop is almost all of TM's log, and **two trades were closed by the margin rescue** (SOLUSD P692692856 at margin level 86 %, SOLUSD P692632217 at 99 %) to save the account.

**2. The 3R loss limit fires on both legs.** 34 `LossLimit` events = **17 trades closed at −3R per leg**, by symbol: ETHUSD 3, XPTUSD 3, EURUSD 3, GBPJPY 2, AUDJPY 2, GBPUSD 2, XNGUSD 2. Because the mirror doubles the position and R is measured on Scout's leg, a trade that runs to the limit costs the group about **−6R**, which is exactly the shape of the loss table in §3.2 (average loser −4.04R, worst −8.53R).

**3. Scout's stop-loss is sometimes inside the spread.** TM's own `TightHedge` warning fired 9 times — the hedge level (= Scout's stop-loss) was **0.5 to 4.7 spreads** from the entry, where "each hedge cycle will cost about 1R":

| When | Symbol | 1R | Spread at that moment | Spreads away |
|---|---|---|---|---|
| 03-Nov 22:06Z | GBPJPY (brokerB) | 0.167 | 0.343 | **0.5** |
| 03-Nov 22:10Z | GBPJPY (brokerA) | 0.148 | 0.250 | **0.6** |
| 04-Nov 23:02Z | XPTUSD | 11.96 | 8.63 | 1.4 |
| 05-Nov 05:00Z | XPTUSD | 13.63 | 8.65 | 1.6 |
| 04-Nov 22:57Z | EURJPY | 0.315 | 0.144 | 2.2 |
| 05-Nov 19:45Z | XPTUSD | 19.23 | 7.04 | 2.7 |
| 05-Nov 12:54Z | XNGUSD | 0.021 | 0.005 | 4.2 |
| 05-Nov 07:04Z | SOLUSD | 0.637 | 0.142 | 4.5 |
| 05-Nov 16:21Z | XPTUSD | 18.97 | 4.02 | 4.7 |

Measured against each symbol's *normal* spread, Scout's stops are fine — median **84 spreads** wide, only 5 of 80 trades under 5 spreads. Every warning above fired at a moment when the spread itself had blown out: seven of the nine are inside 22:00–01:00Z or on XPTUSD, whose spread at the snapshots we have reaches 7.5 price units. **The problem is not the stop distance, it is entering and managing while the spread is wide.** Those 5 trades whose R was under 5 normal spreads lost −24.4R as groups between them (n=5) against −24.0R for the other 64 together.

TM's spread guard and `DoubleOpposite` trigger (tm.md §5.2) went live on 07-Nov 19:39Z, after most of the damage. It worked: 1,256 `SpreadGuard` events, and 16 times the spread was still wide after the full 15 minutes (EURUSD 22×, GBPUSD 30×, USDJPY 62× the usual at the 09-Nov rollover). **Scout's own entry orders still use the broker's default trigger** — the open item at the end of tm.md §5.2.

## 5. Where the money figures come from, and what is missing

| Source | Used for |
|---|---|
| `Dashboard/<date>/deals.csv` (every deal with `owner`, `trade`, `role`, gross / swap / commission / net) | all group and leg money |
| Scout's `orders.csv` `place` / `filled` rows | fill price, stop-loss, lots, slippage, R |
| Scout's `orders.csv` `closed` rows recovered at the 11-Nov restart | the Scout leg of 4 trades that closed after the last deal snapshot |
| `~/tradebot/run/tm/*.json` | which trades are still open at review time |

Two gaps to be honest about:

- The dashboard export's last deal snapshot is **10-Nov 01:15Z** on brokerA and **09-Nov 23:32Z** on brokerB. Five trades closed after it. Scout's recovery gave us its own leg on four of them (EURUSD +$45.42, GBPUSD +$16.47, EURUSD +$27.20, GBPJPY +$11.45 — all four winners); **TM's mirror and hedge deals for those five are in no log we can read**, so they are excluded from the 71 and marked *part* in the table.
- **Seven groups are still open** (brokerA DE40, EURJPY, GBPUSD ×2, US500, XNGUSD; brokerB XTIUSD) plus the two that filled unseen on brokerB (BTCUSD P258012953, net so far −$38.96; ETHUSD P258012957, −$9.02). brokerA's equity sits $530 below its balance, which is roughly what those open trades are worth at the moment. **If they close near today's prices the group loss grows, it does not shrink.**

## 6. Per-trade table

83 fills, in fill order. **R$** is the money at risk on Scout's leg; **Scout $ / R** its own realised result; **Group $ / R** every deal of the trade. *open* = still running at review time; *part* = closed but TM's legs are not in the logs (§5). Picture links open the SVG Scout drew at the moment of the order (scout.md §3.1).

| # | Br | Symbol | Role | 4H | Pause start | Side | Lots | Fill | Stop-loss | R$ | Slip | Scout $ | Scout R | Group $ / R | Picture |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | B | GBPJPY | M | ? | 11-03 15:00 | Buy | 0.1 | 212.684 | 212.481 | 12.79 | 0.1 | +1.62 | +0.13 | +3.24 / +0.25 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-03/charts/GBPJPY-H1-202611031500-M-372886826.svg) |
| 2 | A | GBPJPY | M | ? | 11-03 15:00 | Buy | 0.1 | 212.688 | 212.474 | 13.49 | 0.2 | +5.50 | +0.41 | +11.00 / +0.82 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-03/charts/GBPJPY-H1-202611031500-M-1035054512.svg) |
| 3 | B | ETHUSD | M | with | 11-03 16:00 | Buy | 0.1 | 2299.05 | 2288.44 | 1.06 | 5.8 | -1.09 | -1.03 | -6.72 / -6.34 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-03/charts/ETHUSD-H1-202611031600-M-372890557.svg) |
| 4 | A | BTCUSD | M | with | 11-03 18:00 | Buy | 0.1 | 77588.4 | 77410.4 | 17.80 | 172.0 | +16.39 | +0.92 | +33.07 / +1.86 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-03/charts/BTCUSD-H1-202611031800-M-1035097073.svg) |
| 5 | A | ETHUSD | M | with | 11-03 18:00 | Buy | 0.1 | 2300.59 | 2279.22 | 2.14 | 24.0 | +1.05 | +0.49 | -0.21 / -0.10 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-03/charts/ETHUSD-H1-202611031800-M-1035097074.svg) |
| 6 | B | GBPJPY | M | with | 11-03 19:00 | Buy | 0.1 | 213.061 | 212.894 | 10.52 | 1.6 | -22.20 | -2.11 | -89.74 / -8.53 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-03/charts/GBPJPY-H1-202611031900-M-372923720.svg) |
| 7 | A | GBPJPY | M | with | 11-03 19:00 | Buy | 0.1 | 213.046 | 212.898 | 9.33 | 0.2 | -16.96 | -1.82 | -67.82 / -7.27 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-03/charts/GBPJPY-H1-202611031900-M-1035113694.svg) |
| 8 | B | EURUSD | M | with | 11-04 04:00 | Buy | 0.1 | 1.12445 | 1.12245 | 20.00 | 0.0 | -0.40 | -0.02 | -0.70 / -0.03 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-04/charts/EURUSD-H1-202611040400-M-373072196.svg) |
| 9 | A | EURUSD | M | with | 11-04 04:00 | Buy | 0.1 | 1.12447 | 1.12247 | 20.00 | 0.0 | -1.02 | -0.05 | -1.94 / -0.10 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/EURUSD-H1-202611040400-M-1035391819.svg) |
| 10 | A | EURUSD | M | with | 11-04 10:00 | Buy | 0.1 | 1.1265 | 1.12454 | 19.60 | 0.0 | -61.62 | -3.14 | -133.56 / -6.81 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/EURUSD-H1-202611041000-M-1035549441.svg) |
| 11 | B | EURUSD | M | with | 11-04 10:00 | Buy | 0.1 | 1.12652 | 1.12453 | 19.90 | 0.0 | -45.22 | -2.27 | -133.04 / -6.69 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-04/charts/EURUSD-H1-202611041000-M-373178159.svg) |
| 12 | B | AUDJPY | M | with | 11-04 10:00 | Buy | 0.1 | 111.199 | 111.014 | 11.68 | 0.0 | -21.41 | -1.83 | -93.39 / -8.00 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-04/charts/AUDJPY-H1-202611041000-M-373178155.svg) |
| 13 | A | AUDJPY | M | with | 11-04 10:00 | Buy | 0.1 | 111.203 | 111.016 | 11.84 | 0.0 | -22.99 | -1.94 | -77.92 / -6.58 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/AUDJPY-H1-202611041000-M-1035549436.svg) |
| 14 | B | BTCUSD | M | with | 11-04 11:00 | Buy | 0.1 | 78657.5 | 78180.5 | 47.70 | 184.5 | +41.03 | +0.86 | +84.77 / +1.78 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-04/charts/BTCUSD-H1-202611041100-M-373199381.svg) |
| 15 | A | BTCUSD | M | with | 11-04 11:00 | Buy | 0.1 | 78639.3 | 77899.1 | 74.03 | 975.0 | +169.55 | +2.29 | +339.10 / +4.58 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/BTCUSD-H1-202611041100-M-1035581713.svg) |
| 16 | A | ETHUSD | M | with | 11-04 11:00 | Buy | 0.1 | 2337.45 | 2304.97 | 3.25 | 253.0 | +8.35 | +2.57 | +16.53 / +5.09 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/ETHUSD-H1-202611041100-M-1035581714.svg) |
| 17 | B | ETHUSD | M | with | 11-04 11:00 | Buy | 0.1 | 2337.24 | 2304.01 | 3.32 | 6.2 | +7.59 | +2.29 | +15.87 / +4.78 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-04/charts/ETHUSD-H1-202611041100-M-373199382.svg) |
| 18 | A | GBPJPY | R | against | 11-04 10:00 | Sell | 0.1 | 213.478 | 213.773 | 18.60 | 0.2 | +2.02 | +0.11 | +4.04 / +0.22 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/GBPJPY-H1-202611041000-R-1035613169.svg) |
| 19 | B | GBPJPY | R | against | 11-04 10:00 | Sell | 0.1 | 213.474 | 213.776 | 19.03 | 0.1 | +1.94 | +0.10 | +4.57 / +0.24 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-5726aa/2026-11-04/charts/GBPJPY-H1-202611041000-R-373222816.svg) |
| 20 | A | DE40 | M | ? | 11-04 16:00 | Buy | 0.1 | 26595.1 | 26452.8 | 15.87 | 3.0 | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-ca1c1d/2026-11-04/charts/DE40-H1-202611041600-M-1035754392.svg) |
| 21 | A | AUDJPY | R | against | 11-04 15:00 | Sell | 0.1 | 110.982 | 111.298 | 20.00 | 0.3 | -1.70 | -0.08 | -3.40 / -0.17 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/AUDJPY-H1-202611041500-R-1035764867.svg) |
| 22 | A | XTIUSD | M | with | 11-04 17:00 | Sell | 0.15 | 88.277 | 89.442 | 17.48 | 0.5 | -0.57 | -0.03 | -1.14 / -0.07 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/XTIUSD-H1-202611041700-M-1035764869.svg) |
| 23 | A | BTCUSD | R | against | 11-04 16:00 | Sell | 0.04 | 79863.6 | 80664.5 | 32.04 | 93.0 | +91.02 | +2.84 | +116.92 / +3.65 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/BTCUSD-H1-202611041600-R-1035778762.svg) |
| 24 | A | ETHUSD | M | with | 11-04 16:00 | Buy | 0.1 | 2444.82 | 2414.87 | 3.00 | 49.0 | -21.45 | -7.16 | -18.45 / -6.16 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-5726aa/2026-11-04/charts/ETHUSD-H1-202611041600-M-1035749823.svg) |
| 25 | A | EURJPY | R | with | 11-04 18:00 | Buy | 0.1 | 177.981 | 177.666 | 20.02 | 0.2 | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/EURJPY-H1-202611041800-R-1035802910.svg) |
| 26 | A | EURUSD | R | with | 11-04 19:00 | Buy | 0.1 | 1.12308 | 1.12165 | 14.30 | -1.8 | +21.98 | +1.54 | +43.96 / +3.07 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/EURUSD-H1-202611041900-R-1035808545.svg) |
| 27 | A | XPTUSD | M | with | 11-04 20:00 | Buy | 0.01 | 1504.71 | 1492.75 | 11.96 | 116.0 | +6.81 | +0.57 | +13.62 / +1.14 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/XPTUSD-H1-202611042000-M-1035802916.svg) |
| 28 | A | XAUUSD | M | with | 11-04 18:00 | Buy | 0.01 | 4071.28 | 4055.44 | 15.84 | 82.0 | +31.30 | +1.98 | +62.60 / +3.95 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/XAUUSD-H1-202611041800-M-1035778754.svg) |
| 29 | A | XTIUSD | M | with | 11-04 21:00 | Sell | 0.15 | 88.447 | 89.182 | 11.03 | 0.5 | +9.30 | +0.84 | +18.60 / +1.69 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XTIUSD-H1-202611042100-M-1035823470.svg) |
| 30 | A | XAGUSD | M | with | 11-04 18:00 | Buy | 0.03 | 52.458 | 52.127 | 9.93 | 0.5 | +0.78 | +0.08 | +1.56 / +0.16 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/XAGUSD-H1-202611041800-M-1035778755.svg) |
| 31 | A | SOLUSD | R | against | 11-04 20:00 | Buy | 20 | 96.109 | 94.162 | 38.94 | 170.0 | -124.01 | -3.18 | -84.48 / -2.17 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/SOLUSD-H1-202611042000-R-1035821353.svg) |
| 32 | A | XBRUSD | M | with | 11-04 17:00 | Sell | 0.15 | 97.971 | 98.896 | 13.88 | 1.0 | +1.28 | +0.09 | +2.56 / +0.18 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/XBRUSD-H1-202611041700-M-1035764871.svg) |
| 33 | A | AUDJPY | M | against | 11-04 22:00 | Sell | 0.1 | 110.835 | 110.966 | 8.29 | 0.9 | +1.08 | +0.13 | +2.16 / +0.26 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/AUDJPY-H1-202611042200-M-1035821352.svg) |
| 34 | A | US500 | R | against | 11-04 18:00 | Sell | 0.7 | 7682.4 | 7716.3 | 23.73 | 1.0 | +125.37 | +5.28 | +251.09 / +10.58 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/US500-H1-202611041800-R-1035802917.svg) |
| 35 | A | EURUSD | M | with | 11-05 01:00 | Buy | 0.1 | 1.12586 | 1.12448 | 13.80 | 0.3 | -44.52 | -3.23 | -90.26 / -6.54 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/EURUSD-H1-202611050100-M-1035910420.svg) |
| 36 | A | GBPUSD | M | with | 11-05 01:00 | Buy | 0.1 | 1.34921 | 1.34822 | 9.90 | 0.0 | -27.80 | -2.81 | -75.30 / -7.61 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/GBPUSD-H1-202611050100-M-1035910422.svg) |
| 37 | A | XPTUSD | M | with | 11-05 03:00 | Buy | 0.01 | 1532.38 | 1518.72 | 13.66 | 229.0 | -13.75 | -1.01 | -95.33 / -6.98 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XPTUSD-H1-202611050300-M-1035938796.svg) |
| 38 | A | GBPUSD | M | with | 11-05 04:00 | Buy | 0.1 | 1.34972 | 1.34886 | 8.60 | 0.0 | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/GBPUSD-H1-202611050400-M-1035958998.svg) |
| 39 | A | SOLUSD | M | against | 11-05 03:00 | Buy | 20 | 96.359 | 95.722 | 12.74 | 70.0 | -131.52 | -10.32 | -45.20 / -3.55 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/SOLUSD-H1-202611050300-M-1035938797.svg) |
| 40 | A | XBRUSD | R | against | 11-05 02:00 | Buy | 0.15 | 98.276 | 96.946 | 19.95 | 1.5 | +23.93 | +1.20 | +47.86 / +2.40 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XBRUSD-H1-202611050200-R-1035959012.svg) |
| 41 | A | DE40 | R | against | 11-04 21:00 | Sell | 0.2 | 26494.4 | 26670 | 39.18 | 32.0 | +21.53 | +0.55 | +3.36 / +0.09 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/DE40-H1-202611042100-R-1035889217.svg) |
| 42 | A | EURUSD | R | against | 11-05 04:00 | Sell | 0.1 | 1.12406 | 1.12682 | 27.60 | 0.5 | +8.18 | +0.30 | +17.46 / +0.63 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/EURUSD-H1-202611050400-R-1036000042.svg) |
| 43 | A | XTIUSD | R | against | 11-05 04:00 | Buy | 0.15 | 88.537 | 87.072 | 21.98 | 0.5 | +18.00 | +0.82 | +35.85 / +1.63 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XTIUSD-H1-202611050400-R-1036000050.svg) |
| 44 | A | XNGUSD | R | against | 11-04 18:00 | Sell | 0.5 | 2.738 | 2.791 | 265.00 | 0.0 | -459.90 | -1.74 | -1459.80 / -5.51 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-04/charts/XNGUSD-H1-202611041800-R-1035802919.svg) |
| 45 | A | US500 | R | with | 11-05 05:00 | Buy | 0.7 | 7691.4 | 7661.3 | 21.07 | 0.0 | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/US500-H1-202611050500-R-1036020340.svg) |
| 46 | A | BTCUSD | M | against | 11-05 10:00 | Sell | 0.04 | 80120.2 | 80443.9 | 12.95 | 112.0 | +13.93 | +1.08 | +27.86 / +2.15 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/BTCUSD-H1-202611051000-M-1036084879.svg) |
| 47 | A | XNGUSD | M | against | 11-05 10:00 | Sell | 0.5 | 2.735 | 2.756 | 105.00 | 0.0 | -100.00 | -0.95 | -670.00 / -6.38 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XNGUSD-H1-202611051000-M-1036084874.svg) |
| 48 | A | EURUSD | M | against | 11-05 11:00 | Sell | 0.1 | 1.12166 | 1.12292 | 12.60 | 0.0 | +0.38 | +0.03 | +0.76 / +0.06 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/EURUSD-H1-202611051100-M-1036110482.svg) |
| 49 | A | XAGUSD | R | against | 11-05 02:00 | Sell | 0.03 | 52.379 | 53.293 | 27.42 | 0.6 | +7.35 | +0.27 | +14.70 / +0.54 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XAGUSD-H1-202611050200-R-1035959003.svg) |
| 50 | A | XAUUSD | R | against | 11-05 03:00 | Sell | 0.01 | 4108.55 | 4146.9 | 38.35 | 52.0 | +32.83 | +0.86 | +65.74 / +1.71 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XAUUSD-H1-202611050300-R-1035975382.svg) |
| 51 | A | XTIUSD | M | against | 11-05 13:00 | Buy | 0.15 | 90.337 | 89.472 | 12.98 | 18.0 | -0.60 | -0.05 | -1.35 / -0.10 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XTIUSD-H1-202611051300-M-1036189829.svg) |
| 52 | A | XBRUSD | M | against | 11-05 14:00 | Buy | 0.15 | 100.811 | 100.021 | 11.85 | 0.5 | +39.50 | +3.33 | +67.00 / +5.65 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XBRUSD-H1-202611051400-M-1036271144.svg) |
| 53 | A | XPTUSD | M | with | 11-05 14:00 | Buy | 0.01 | 1534.74 | 1515.77 | 18.97 | 5.0 | -28.38 | -1.50 | -114.64 / -6.04 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XPTUSD-H1-202611051400-M-1036271139.svg) |
| 54 | A | DE40 | M | against | 11-05 16:00 | Sell | 0.2 | 26224.2 | 26418.7 | 43.40 | 50.0 | +94.04 | +2.17 | +188.08 / +4.33 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/DE40-H1-202611051600-M-1036340879.svg) |
| 55 | A | EURJPY | M | with | 11-05 16:00 | Sell | 0.1 | 177.204 | 177.324 | 7.63 | 4.2 | +2.97 | +0.39 | +5.94 / +0.78 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/EURJPY-H1-202611051600-M-1036340865.svg) |
| 56 | A | EURUSD | R | with | 11-05 14:00 | Buy | 0.1 | 1.12415 | 1.12103 | 31.20 | 0.6 | ? |  | part: -66.84 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/EURUSD-H1-202611051400-R-1036340864.svg) |
| 57 | A | USTEC | M | with | 11-05 16:00 | Sell | 0.1 | 26715.1 | 27025.1 | 31.00 | 5.0 | +14.81 | +0.48 | -0.74 / -0.02 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/USTEC-H1-202611051600-M-1036340874.svg) |
| 58 | A | XTIUSD | M | against | 11-05 16:00 | Buy | 0.15 | 91.047 | 90.382 | 9.97 | 1.0 | +17.95 | +1.80 | +25.40 / +2.55 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XTIUSD-H1-202611051600-M-1036340884.svg) |
| 59 | A | XPTUSD | M | with | 11-05 17:00 | Buy | 0.01 | 1537.19 | 1517.96 | 19.23 | 37.0 | -19.14 | -1.00 | -117.91 / -6.13 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XPTUSD-H1-202611051700-M-1036363658.svg) |
| 60 | A | GBPUSD | M | with | 11-05 19:00 | Sell | 0.1 | 1.34493 | 1.34634 | 14.10 | 0.5 | -16.31 | -1.16 | -94.92 / -6.73 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/GBPUSD-H1-202611051900-M-1036403098.svg) |
| 61 | A | ETHUSD | M | with | 11-05 16:00 | Sell | 0.9 | 2234.57 | 2264.01 | 26.50 | 59.0 | -0.65 | -0.02 | +0.78 / +0.03 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/ETHUSD-H1-202611051600-M-1036340893.svg) |
| 62 | A | EURJPY | M | with | 11-05 19:00 | Sell | 0.1 | 177.002 | 177.192 | 12.07 | 0.0 | +18.94 | +1.57 | +37.88 / +3.14 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/EURJPY-H1-202611051900-M-1036403106.svg) |
| 63 | A | GBPJPY | M | with | 11-05 19:00 | Sell | 0.1 | 212.17 | 212.474 | 19.16 | 0.1 | +11.89 | +0.62 | +23.78 / +1.24 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/GBPJPY-H1-202611051900-M-1036403101.svg) |
| 64 | A | XTIUSD | M | against | 11-05 19:00 | Buy | 0.15 | 91.467 | 90.307 | 17.40 | 2.0 | +0.09 | +0.01 | +0.18 / +0.01 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XTIUSD-H1-202611051900-M-1036403117.svg) |
| 65 | A | XNGUSD | M | with | 11-05 19:00 | Sell | 0.5 | 2.739 | 2.765 | 130.00 | 0.0 | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/XNGUSD-H1-202611051900-M-1036403123.svg) |
| 66 | A | USDJPY | M | against | 11-05 18:00 | Sell | 0.1 | 157.646 | 157.876 | 14.61 | 0.0 | +7.24 | +0.50 | +14.48 / +0.99 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/USDJPY-H1-202611051800-M-1036383655.svg) |
| 67 | A | BTCUSD | M | with | 11-05 16:00 | Sell | 0.04 | 75530.1 | 76303.4 | 30.93 | 539.0 | +5.99 | +0.19 | +11.98 / +0.39 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/BTCUSD-H1-202611051600-M-1036340890.svg) |
| 68 | A | AUDJPY | M | with | 11-05 21:00 | Sell | 0.1 | 109.162 | 109.422 | 16.46 | 0.0 | +7.85 | +0.48 | -2.04 / -0.12 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/AUDJPY-H1-202611052100-M-1036431323.svg) |
| 69 | A | SOLUSD | M | with | 11-05 18:00 | Sell | 20 | 88.512 | 90.052 | 30.80 | 400.0 | +34.56 | +1.12 | +0.86 / +0.03 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/SOLUSD-H1-202611051800-M-1036383667.svg) |
| 70 | A | XTIUSD | M | with | 11-05 23:00 | Buy | 0.15 | 91.872 | 91.272 | 9.00 | 1.0 | -0.30 | -0.03 | -0.75 / -0.08 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-06/charts/XTIUSD-H1-202611052300-M-1036479096.svg) |
| 71 | A | BTCUSD | M | with | 11-05 23:00 | Sell | 0.04 | 75593.8 | 76304 | 28.41 | 124.0 | +3.54 | +0.12 | +3.54 / +0.12 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-06/charts/BTCUSD-H1-202611052300-M-1036479100.svg) |
| 72 | A | ETHUSD | M | with | 11-06 03:00 | Sell | 0.9 | 2221.96 | 2241.97 | 18.01 | 2.0 | -20.84 | -1.16 | -110.30 / -6.12 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-06/charts/ETHUSD-H1-202611060300-M-1036547981.svg) |
| 73 | A | GBPJPY | M | with | 11-05 23:00 | Sell | 0.1 | 211.827 | 212.309 | 30.39 | 0.3 | +0.57 | +0.02 | +1.14 / +0.04 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-06/charts/GBPJPY-H1-202611052300-M-1036479080.svg) |
| 74 | A | XAUUSD | R | against | 11-06 05:00 | Buy | 0.01 | 4077.74 | 4035.95 | 41.79 | 161.0 | +12.84 | +0.31 | -8.47 / -0.20 | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-06/charts/XAUUSD-H1-202611060500-R-1036630371.svg) |
| 75 | A | GBPUSD | M | with | 11-06 14:00 | Sell | 0.1 | 1.33562 | 1.33676 | 11.40 | 0.1 | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerA/49123893/Scout-6d48a6/2026-11-06/charts/GBPUSD-H1-202611061400-M-1036845948.svg) |
| 76 | B | GBPUSD | R | against | 11-08 23:00 | Buy | 0.03 | 1.33762 | 1.33491 | 8.13 | 0.2 | +16.38 | +2.01 | part: -0.18 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-0aadfa/2026-11-09/charts/GBPUSD-H1-202611082300-R-374741469.svg) |
| 77 | B | EURUSD | R | against | 11-08 23:00 | Buy | 0.06 | 1.11582 | 1.11416 | 9.96 | 0.0 | +45.24 | +4.54 | part: -0.36 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-0aadfa/2026-11-09/charts/EURUSD-H1-202611082300-R-374741467.svg) |
| 78 | B | XBRUSD | M | with | 11-09 01:00 | Sell | 0.09 | 100.074 | 101.2 | 10.13 | 1.0 | +27.86 | +2.75 | +55.83 / +5.51 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-0aadfa/2026-11-09/charts/XBRUSD-H1-202611090100-M-374741471.svg) |
| 79 | B | EURUSD | M | with | 11-09 11:00 | Buy | 0.13 | 1.11939 | 1.11857 | 10.66 | 0.3 | -0.26 | -0.02 | -0.52 / -0.05 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-556e24/2026-11-09/charts/EURUSD-H1-202611091100-M-374921361.svg) |
| 80 | B | EURUSD | M | with | 11-09 14:00 | Buy | 0.08 | 1.12022 | 1.11899 | 9.84 | 0.0 | +26.96 | +2.74 | part: -0.48 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-556e24/2026-11-09/charts/EURUSD-H1-202611091400-M-375004952.svg) |
| 81 | B | XBRUSD | M | with | 11-09 14:00 | Sell | 0.07 | 96.679 | 98.148 | 10.28 | 2.9 | +9.51 | +0.92 | +19.02 / +1.85 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-556e24/2026-11-09/charts/XBRUSD-H1-202611091400-M-375004961.svg) |
| 82 | B | GBPJPY | M | with | 11-09 15:00 | Buy | 0.07 | 212.129 | 211.902 | 10.01 | 0.0 | +11.24 | +1.12 | part: -12.38 | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-556e24/2026-11-09/charts/GBPJPY-H1-202611091500-M-375031651.svg) |
| 83 | B | XTIUSD | M | with | 11-09 14:00 | Sell | 0.04 | 86.591 | 88.904 | 9.91 |  | open |  | open | [chart](file:///home/zia/tradebot/logs/brokerB/49124666/Scout-556e24/2026-11-09/charts/XTIUSD-H1-202611091400-M-375004957.svg) |

## 7. Bot health

Window 03-Nov 17:41Z → 11-Nov 14:40Z = 189.0 h.

| | Scout | TM |
|---|---|---|
| Service starts | 27 | 11 |
| Abnormal exits | **3** — exit 3 (`ChannelClosedException`, 03-Nov 17:58Z network drop), exit 6 core dump (04-Nov 01:10Z, deploy over a running build), exit 1 (**09-Nov 18:14Z, access token rejected after a refresh**) | 1 — exit 3, the same 03-Nov network drop |
| Service uptime | 139.2 h = **73.6 %** | 183.5 h = **97.1 %** |
| Gaps over 1 min | 2: **09-Nov 18:14Z → 11-Nov 14:36Z (44.4 h)** and 07-Nov 19:46Z → 08-Nov 01:13Z (5.5 h) | 1: the same 5.5 h on 07/08-Nov |
| Laptop asleep | one window, **10-Nov 04:16Z → 11-Nov 14:34Z (34.3 h)** — no candles and no management for either bot | same |
| `ConnectionLost` | 614 (12 on 03-Nov, 232 on 07-Nov, 302 on 08-Nov, 68 on 09-Nov) | 1,238 (10 / 488 / 602 / 136 / 2) |
| Cycle errors | 10 `ScoutCycleError` (no reply to a trendbar or reconcile request within 15 s) | 203 `TmCycleError` |
| Chart-drawing failures | **0** `ScoutChartError`; all 190 placed orders have a picture on disk | — |

Three health findings:

1. **Scout was down for 44.4 hours and nothing raised an alarm.** At 09-Nov 18:14:22Z TM's token refresher entered a loop: **502 `TokenRefreshed` and 502 `TokenInvalidated` events inside a few minutes**, each refresh invalidating the token the other bot was holding. Scout was caught twice, reported `CH_ACCESS_TOKEN_INVALID` right after its own refresh and exited with code 1. The service file's `RestartPreventExitStatus=1 2` is deliberate — a rejected token needs a human — so systemd correctly did *not* restart it, and Scout stayed down. It was started again by hand at **11-Nov 14:36:56Z**, three seconds before this review read the service state; the restart was not made by this review, and nothing in the user journal names what triggered it. The remote dead-man alarm that would have caught this is review finding **F-13, left for later** (owner, 09-Nov).
2. **The two bots share one token file and fight over it.** Both read and write `~/.config/trader/secrets.env`; the refresh is supposed to be serialised by the lock, and Scout's log does say "Tokens were already refreshed by another instance; reloaded", but the result on 09-Nov was 1,000 token events and one dead bot. This is a Trader.Core / Open Code Pieces question, not a Scout rule.
3. **The 07–08 Nov connection churn is the network, not the bots.** Every one of those `ConnectionLost` lines reads "no data for ~42 s and no reply to ping"; both bots lost the connection at the same second and both recovered in about a second. No orders were lost to it.

Missed candles follow from the gaps: Scout watched 18 charts on 1H, so the 44.4-hour gap alone is about **780 chart-candles** it never saw, and the 34.3-hour sleep inside it would have blinded both bots even if Scout had been running.

## 8. Suggestions

**All of these are proposals. None is implemented, and none should be until the owner says so (Trader CLAUDE.md rule 1, Scout CLAUDE.md rule 1).** They are ranked by how much evidence this week carries — and the honest headline is that **the strongest findings are about TM's management and the clock, not about Scout's setup rules**.

| # | Proposal | Evidence | What it would have changed this week |
|---|---|---|---|
| **S1** | **Do not skip *against*-4H setups; the trial answers Scout spec §7 q9 "mark only" and the answer is "keep marking".** If anything is worth testing it is the reverse. | *against* 25 trades +0.78R average, +19.6R; *with* 44 trades −1.40R average, −61.7R. Scout's own leg: +0.21R vs −0.23R | Skipping *against* setups would have **removed the only profitable half**: −19.6R worse at group level. Keep the tag, keep taking both |
| **S2** | **Stop entering, and stop managing, around the daily rollover.** Give Scout's entry orders TM's `DoubleOpposite` trigger (the open item in tm.md §5.2) and refuse a new Scout order inside TM's rollover window (5 min before each close to 15 min after each reopen). | Groups whose last leg closed in 21:00–02:00Z: 22 trades, −2.51R average, **−55.3R**; all other hours: 49 trades, +0.29R, +14.2R. The two worst trades of the week (GBPJPY −8.53R, AUDJPY −8.00R) both died at 22:0xZ, both with `TightHedge` warnings at 0.5–0.6 spreads | TM's guard already covers its own actions from 07-Nov. Blocking Scout entries in the window would have dropped a handful of the 22 rollover trades; the hedge trigger would have stopped the spike fills that caused the two −8R trades. Best single change available |
| **S3** | **Change the loss limit so one trade cannot cost 6R.** Either apply the 3R limit to the *trade* (main + mirror together) rather than to each leg, or cut it to 1.5R per leg. | 17 trades hit −3R on both legs; average group loser −4.04R against an average group winner +1.96R. At 58 % winners the group needs a loser under about 2.7R to break even | Capping every group's loss at −3R (1.5R per leg) turns the week from **−41.0R into +22.9R**: the 19 trades that never reached +1R go from −120.1R to −56.2R. This is arithmetic on the losses already taken, **not a backtest** — a tighter stop also closes trades that would have recovered. The replay (`scripts/full-replay.sh`) must measure it before anything changes |
| **S4** | **Shorten the server expiry from 16 candles to 4.** | Median time to fill 0.8 h; 55 % of fills in the first hour, 88 % within 4 h, 99 % within 12. Only 9 of 190 orders ever reached expiry. The 9 fills that came later than 4 h were worth −8.6R as groups | 9 fewer trades, **+8.6R**, and 9 fewer stale orders sitting on the server when Scout restarts or changes account. Small sample (n=9) — the clearest candidate for a replay test |
| **S5** | **Drop or shrink XPTUSD and XNGUSD.** Both lose on size and spread rather than on direction. | XPTUSD 4 trades −18.0R (1 winner), 4 of the 9 `TightHedge` warnings, spread up to 7.5 price units; XNGUSD 2 trades −11.9R, held at the broker's 0.5-lot minimum at IC Markets (≈ $135 per 1H ATR, 9× the others — the open item in scout.md v1.6) | These two symbols are **−29.9R of the −41.0R** group loss on six trades; without them the week is −11.1R. **But six trades is not evidence of anything about platinum or gas** — it is evidence that a symbol whose spread is a large part of 1R should not be traded at these sizes |
| **S6** | **Watch the forex block.** Forex (majors and crosses): 27 trades, **−53.5R**. Everything else (metals, energies, indices, crypto): 44 trades, **+12.5R**. Within forex, EURUSD, AUDJPY, GBPUSD and GBPJPY alone are 24 trades and −58.4R. | Per-symbol table, §3.4 | Nothing yet. This is the shape of the rollover and tight-stop problems (S2, S3) seen per symbol, not an independent reason to drop majors. Re-read it after S2 and S3 |
| **S7** | **Keep reversal stops exactly as they are** (Scout spec §4.4 I4). | R: 15 trades, 73 % winners, +1.11R average. M: 56 trades, 54 %, −1.03R | Nothing to change. Worth asking whether the reversal is good because it is *against* the 4H trend by nature (S1), which the sample cannot separate |
| **S8** | **Do not tighten the pause thresholds** (`pause_candles`, `pause_height_atr`, `move_atr`, `pullback`) on this data. | Winners and losers are indistinguishable on all four (§3.4) | Nothing. This is the case for [Scout Lens](../bots/scout/scout-lens.md): what separates these pauses is not in the numbers the detector already has |
| **S9** | **Two operational fixes before the next trial week:** (a) the off-machine dead-man alarm (review F-13) — Scout died for 44 h unnoticed; (b) write an outcome row for orders Scout leaves pending when it changes account, because 3 of 30 such orders filled unseen and one of them is still open with −$39 realised. | §4, §7 | Not a trading rule. Without (a) a trial week can silently become a trial day |

**What the week does not show.** It does not show that the pause setup has an edge, and it does not show that it has none: Scout's own leg is −0.06R per trade over 71 trades, which is indistinguishable from zero at this sample size. It also cannot be read as a test of one rule set, because the symbols, the sizing and three new rule layers changed underneath it (§2). The independent review's verdict of 08-Nov — "no trading edge shown" — stands unchanged.

## 9. Open questions for the owner

1. **Where should this report live?** The brief asked for `docs/reviews/scout-trial-week1.md`, which is where it is. Scout's own reviews live in `docs/bots/scout/reviews/` with a dated, versioned naming rule and an index ([reviews/README.md](../bots/scout/reviews/README.md)). Should this move there as `2026-11-11-v1.0-trial-week1.md` and get a row in that table?
2. **S3 is the big one and it is a TM rule, not a Scout rule.** Changing the loss limit touches every TM trade, including the owner's own manual ones. Do you want it measured in the replay first (and on which period), or discussed before any measurement?
3. **Was the 11-Nov 14:36Z Scout restart yours?** Scout had been dead since 09-Nov 18:14Z. It was started by hand three seconds before this review read the service state; nothing in the journal says what started it. If it was not you, something on the machine starts services.
4. **brokerA still holds six open Scout groups** from 04–06 Nov, with equity about $530 below balance, and two more sit on brokerB from the 04-Nov hand-over. Leave them to run, or close them to end the trial cleanly? Nothing on either account was touched for this review.
5. **One broker is enough now.** The 21 setups seen at both brokers agree on side, level and direction of result (§3.4). Is the two-broker comparison closed?
6. **Should the next week be a trial at all?** With the rules changing every day the logs cannot score a rule set. One option: freeze `scout.json` and TM's config for seven days and change nothing, so week 2 answers one question.

## 10. History

| Version | Date | Change |
|---|---|---|
| v1.0 | 11 Nov 2026 | First issue: week-1 scoring of the Scout trial (03-Nov 17:41Z → 09-Nov 18:14Z) from Scout, TM and dashboard logs; 236 roles, 190 orders, 85 fills, 71 fully closed groups. Proposals S1–S9, none implemented |
