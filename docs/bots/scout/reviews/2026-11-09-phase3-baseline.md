# Phase 3 baseline: Scout and TM together on history

**The first measurement of the whole system. It loses money after costs in both periods tested, so no trading edge is shown.** Run on 09-Nov-2026 from Trader `eacc430` plus the replay clock fix, Rewind `0c2ced5`. Review reference: [P-06, P-07](2026-11-08-v1.0-independent-review.md).

| Period | Equity from $5,000 | Largest fall | Mean per finished trade, after costs (95 % interval) |
|---|---|---|---|
| Development, Nov 2024 to Dec 2025 | **−57.2 %** | 63.7 % | −0.197 R (−0.368 to −0.032) |
| Validation, Jan to Jun 2026 | **−44.2 %** | 46.3 % | −0.225 R (−0.379 to −0.079) |

The **locked period, Jul to Oct 2026, has not been run.** The review keeps it for one final test. Dukascopy's SOLUSD history stops on 22-Aug-2026, so that test will lack SOLUSD from then on.

**Corrected the same day.** The first version of this report showed −54.3 % and −32.2 %. Matching the replay to [real trading](2026-11-09-phase3-real-week.md) showed that Scout acted 5 minutes late in every replay. These are the runs with that fixed.

## What was run

The real Scout and the real TM, built from this repository with the repository's settings (sizing 0.5 %, portfolio limits on), traded one simulated $5,000 account over all 18 symbols, in step with Rewind (`scripts/full-replay.sh`). Margin by the MAX rule, Pepperstone's symbol details, commission and swap read from the demo on 09-Nov-2026. A repeat of a test week gave identical results. Each run takes 15 to 30 minutes.

One setting differs from live: **the weekly and peak loss halts are lifted**, because they wait for the owner's reset and a replay has no owner. With them in force, development stopped itself on 4 Dec 2024 and validation on 20 Jan 2026. The daily 3 % halt stays. Live, those halts would have stopped the losses far earlier, and the owner would have been asked to review.

## What the runs show

- **Costs and rule losses dominate.** Both periods lose, and in both the 95 % interval lies wholly below zero.
- **Results differ by group, and not consistently.** JPY crosses and crypto lost in both periods. Indices made a little in both. Energy changed sign. Most symbols change sign between the periods, so this is not yet a basis for choosing symbols.
- **Metals did not trade in validation.** At 2026's prices, 0.5 % of a $5,000 account or less no longer buys the broker's minimum size, so the sizer refused them. In development there were 33 metal trades, which lost.
- **Old hedged trades fill the account.** A leg was held for a median of about 14 hours, but the longest for 234 days. At least 28 positions, about seven trades, were open for 82 % of the hours in development and 46 % in validation, and in development the limit of 8 trades refused 14,415 tries. Scout was mostly shut out by trades that were frozen but not finished.
- **The figures per trade flatter the system.** They count finished trades only. The money check in each table shows what is still in open trades: −$1,031 and −$174. The equity line is the full measure.
- **The risk controls held.** Margin use peaked at 16 % and 18 %, against the limit of 33 %. No stop-out by the broker and no hedge refused for lack of margin.

## Limits of this measurement

- **Right in general, not trade by trade.** [Against real trading](2026-11-09-phase3-real-week.md), a replay on Dukascopy's history finds about 75 % of the real orders; on the broker's own candles, 96 %. So these figures describe the system's behaviour, not what each trade would have done.
- History is Dukascopy's, and its spreads differ from Pepperstone's. Each 5-minute candle is walked in 4 price steps.
- Today's swap, commission and margin rates are applied to all past dates.
- Only one parameter set. No experiments yet (review Phase 4).
- The 18 symbols were chosen with hindsight, so results may be flattered.

## The numbers

Made with `scripts/replay-report.py`. R is a trade's result divided by the risk budget of a trade (0.5 % of equity) when it opened.

## development: 04 Nov 2024 to 28 Dec 2025

| Measure | Value |
|---|---|
| Equity | $5,000.00 → $2,137.83 (-57.2 %) |
| Largest fall from a peak | 63.7 % |
| Worst day; best day | -4.1 %; +4.7 % |
| Whole trades finished | 480 (and 28 positions still open at the end) |
| Mean result per trade, after costs | **-0.197 R** (95 % interval -0.368 to -0.032) |
| Trades won | 52.9 % |
| Profit factor | 0.73 |
| Longest losing run | 6 trades |
| Worst trade; best trade | -8.86 R; +8.48 R |
| Money check | balance changed -2,746.45: finished trades -1,715.66, parts of trades still open -1,030.79 |
| Commission paid | $350.98 |
| Margin use: typical; highest | 6 %; 16 % |
| Stop-outs by the broker | 0 |
| Orders refused at their price for lack of margin | 0 |

| Scout's orders | Count |
|---|---|
| Placed | 1014 |
| Filled | 487 |
| Not placed: portfolio limit | 14415 |
| Not placed: size refused | 3491 |
| Not placed: no live price | 1775 |
| Not placed: stale candles | 1413 |
| Not placed: setup too old | 178 |
| Not placed: halted | 177 |
| Refused because the level had passed | 8870 |

| Symbol | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| AUDJPY | 24 | -1.084 | 42 % | 0.22 | -593.86 |
| BTCUSD | 24 | -0.169 | 58 % | 0.69 | -48.86 |
| DE40 | 20 | -0.427 | 40 % | 0.48 | -176.10 |
| ETHUSD | 43 | -0.073 | 53 % | 0.88 | -8.72 |
| EURJPY | 23 | -0.536 | 48 % | 0.43 | -220.18 |
| EURUSD | 66 | -0.085 | 48 % | 0.86 | +47.24 |
| GBPJPY | 32 | -0.541 | 53 % | 0.43 | -309.24 |
| GBPUSD | 46 | +0.163 | 61 % | 1.30 | +171.74 |
| SOLUSD | 30 | -0.239 | 60 % | 0.69 | -141.80 |
| US500 | 35 | -0.029 | 54 % | 0.96 | +12.20 |
| USDJPY | 27 | -0.099 | 56 % | 0.84 | -51.16 |
| USTEC | 19 | +0.580 | 68 % | 2.90 | +216.04 |
| XAGUSD | 9 | -1.251 | 33 % | 0.11 | -300.52 |
| XAUUSD | 17 | +0.338 | 65 % | 2.00 | +145.16 |
| XBRUSD | 14 | -0.130 | 43 % | 0.87 | -22.22 |
| XNGUSD | 22 | -0.477 | 41 % | 0.45 | -258.72 |
| XPTUSD | 7 | -0.956 | 29 % | 0.13 | -179.88 |
| XTIUSD | 22 | -0.030 | 68 % | 0.96 | +3.22 |

| Group | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| USD | 139 | -0.006 | 54 % | 0.99 | +167.82 |
| JPY | 79 | -0.705 | 48 % | 0.35 | -1,123.28 |
| Metals | 33 | -0.370 | 48 % | 0.53 | -335.24 |
| Indices | 74 | +0.020 | 54 % | 1.03 | +52.14 |
| Energy | 58 | -0.224 | 52 % | 0.74 | -277.72 |
| Crypto | 97 | -0.148 | 57 % | 0.77 | -199.38 |

| Half-year | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| 2024-H2 | 213 | +0.053 | 59 % | 1.08 | +257.42 |
| 2025-H1 | 141 | -0.223 | 51 % | 0.70 | -782.54 |
| 2025-H2 | 126 | -0.589 | 44 % | 0.35 | -1,190.54 |

## validation: 05 Jan 2026 to 29 Jun 2026

| Measure | Value |
|---|---|
| Equity | $5,000.00 → $2,791.85 (-44.2 %) |
| Largest fall from a peak | 46.3 % |
| Worst day; best day | -3.9 %; +3.8 % |
| Whole trades finished | 421 (and 32 positions still open at the end) |
| Mean result per trade, after costs | **-0.225 R** (95 % interval -0.379 to -0.079) |
| Trades won | 51.3 % |
| Profit factor | 0.67 |
| Longest losing run | 8 trades |
| Worst trade; best trade | -4.26 R; +6.90 R |
| Money check | balance changed -2,100.03: finished trades -1,925.84, parts of trades still open -174.19 |
| Commission paid | $413.74 |
| Margin use: typical; highest | 9 %; 18 % |
| Stop-outs by the broker | 0 |
| Orders refused at their price for lack of margin | 0 |

| Scout's orders | Count |
|---|---|
| Placed | 898 |
| Filled | 429 |
| Not placed: portfolio limit | 4744 |
| Not placed: size refused | 2222 |
| Not placed: no live price | 573 |
| Not placed: stale candles | 465 |
| Not placed: halted | 99 |
| Not placed: setup too old | 31 |
| Refused because the level had passed | 3323 |

| Symbol | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| AUDJPY | 30 | -0.460 | 47 % | 0.50 | -214.86 |
| BTCUSD | 27 | -0.291 | 37 % | 0.60 | -166.02 |
| DE40 | 11 | +0.289 | 73 % | 2.08 | +75.60 |
| ETHUSD | 22 | -1.072 | 23 % | 0.22 | -479.76 |
| EURJPY | 28 | -0.337 | 57 % | 0.60 | -170.00 |
| EURUSD | 48 | -0.032 | 56 % | 0.95 | -58.20 |
| GBPJPY | 39 | -0.685 | 41 % | 0.18 | -512.34 |
| GBPUSD | 47 | -0.655 | 38 % | 0.24 | -614.46 |
| SOLUSD | 16 | -0.209 | 62 % | 0.71 | -71.38 |
| US500 | 42 | +0.057 | 55 % | 1.13 | -6.50 |
| USDJPY | 41 | +0.125 | 59 % | 1.26 | +78.30 |
| USTEC | 19 | +0.483 | 79 % | 5.35 | +182.16 |
| XBRUSD | 14 | +0.887 | 86 % | 14.46 | +245.60 |
| XNGUSD | 15 | -0.401 | 47 % | 0.50 | -112.72 |
| XTIUSD | 22 | -0.189 | 50 % | 0.72 | -101.26 |

| Group | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| USD | 136 | -0.200 | 51 % | 0.70 | -594.36 |
| JPY | 97 | -0.515 | 47 % | 0.40 | -897.20 |
| Metals | 0 | | | | |
| Indices | 72 | +0.205 | 64 % | 1.62 | +251.26 |
| Energy | 51 | +0.044 | 59 % | 1.08 | +31.62 |
| Crypto | 65 | -0.535 | 38 % | 0.43 | -717.16 |

| Half-year | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| 2026-H1 | 421 | -0.225 | 51 % | 0.67 | -1,925.84 |
