# Phase 3 baseline: Scout and TM together on history

**The first measurement of the whole system. It loses money after costs in both periods tested, so no trading edge is shown.** Run on 09-Nov-2026 from Trader `76a8694` plus the report script, Rewind `d3c8fd0`. Review reference: [P-06, P-07](2026-11-08-v1.0-independent-review.md).

| Period | Equity from $5,000 | Largest fall | Mean per finished trade, after costs (95 % interval) |
|---|---|---|---|
| Development, Nov 2024 to Dec 2025 | **−54.3 %** | 57.1 % | −0.192 R (−0.338 to −0.047) |
| Validation, Jan to Jun 2026 | **−32.2 %** | 35.0 % | −0.129 R (−0.355 to +0.088) |

The **locked period, Jul to Oct 2026, has not been run.** The review keeps it for one final test.

## What was run

The real Scout and the real TM, built from this repository with the repository's settings (sizing 0.5 %, portfolio limits on), traded one simulated $5,000 account over all 18 symbols, in step with Rewind (`scripts/full-replay.sh`). Margin by the MAX rule, Pepperstone's symbol details, commission and swap read from the demo on 09-Nov-2026. A repeat of a test week gave identical results. Each run takes 15 to 30 minutes.

One setting differs from live: **the weekly and peak loss halts are lifted**, because they wait for the owner's reset and a replay has no owner. With them in force, development stopped itself on 4 Dec 2024 and validation on 20 Jan 2026. The daily 3 % halt stays. Live, those halts would have stopped the losses far earlier, and the owner would have been asked to review.

## What the runs show

- **Costs and rule losses dominate.** Both periods lose. Only the interval for development excludes zero. Validation alone cannot separate its result from zero.
- **Results differ by group, and not consistently.** JPY crosses lost in both periods. Indices made a little in both. Crypto, energy and metals changed sign or had too few trades. With about a dozen symbol-period results positive in one period only, this is not yet a basis for choosing symbols.
- **Old hedged trades fill the account.** A leg was held for a median of about 15 hours, but the longest for 228 days. In development at least 28 positions, about seven trades, were open for 73 % of the hours, and the limit of 8 trades refused 13,538 tries. Scout was mostly shut out by trades that were frozen but not finished.
- **The figures per trade flatter the system.** They count finished trades only. The money check in each table shows what is still in open trades: −$543 and −$425. The equity line is the full measure.
- **The risk controls held.** Margin use peaked at 14 % and 21 %, against the limit of 33 %. No stop-out by the broker and no hedge refused for lack of margin.

## Limits of this measurement

- **Not yet checked against the real broker.** The review's acceptance test, a real trial week matched to broker deals, and the replays of issues #1 and #2 have not been done.
- History is Dukascopy's, and its spreads differ from Pepperstone's. Each 5-minute candle is walked in 4 price steps.
- Today's swap, commission and margin rates are applied to all past dates.
- Only one parameter set. No experiments yet (review Phase 4).
- The 18 symbols were chosen with hindsight, so results may be flattered.

## The numbers

Made with `scripts/replay-report.py`. R is a trade's result divided by the risk budget of a trade (0.5 % of equity) when it opened.

## development: 04 Nov 2024 to 28 Dec 2025

| Measure | Value |
|---|---|
| Equity | $5,000.00 → $2,283.87 (-54.3 %) |
| Largest fall from a peak | 57.1 % |
| Worst day; best day | -3.3 %; +7.1 % |
| Whole trades finished | 572 (and 30 positions still open at the end) |
| Mean result per trade, after costs | **-0.192 R** (95 % interval -0.338 to -0.047) |
| Trades won | 52.4 % |
| Profit factor | 0.72 |
| Longest losing run | 8 trades |
| Worst trade; best trade | -7.42 R; +8.56 R |
| Money check | balance changed -2,592.77: finished trades -2,049.30, parts of trades still open -543.47 |
| Commission paid | $411.80 |
| Margin use: typical; highest | 8 %; 14 % |
| Stop-outs by the broker | 0 |
| Orders refused at their price for lack of margin | 0 |

| Scout's orders | Count |
|---|---|
| Placed | 1249 |
| Filled | 580 |
| Not placed: portfolio limit | 13538 |
| Not placed: size refused | 3458 |
| Not placed: no live price | 1874 |
| Not placed: stale candles | 1441 |
| Not placed: setup too old | 184 |
| Not placed: halted | 81 |
| Refused because the level had passed | 9113 |

| Symbol | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| AUDJPY | 30 | -0.759 | 47 % | 0.30 | -450.54 |
| BTCUSD | 35 | -0.374 | 54 % | 0.48 | -272.22 |
| DE40 | 25 | +0.238 | 68 % | 1.47 | +104.94 |
| ETHUSD | 44 | -0.438 | 52 % | 0.45 | -316.64 |
| EURJPY | 45 | -0.237 | 51 % | 0.70 | -179.70 |
| EURUSD | 61 | +0.139 | 49 % | 1.27 | +204.32 |
| GBPJPY | 63 | -0.144 | 59 % | 0.77 | -163.86 |
| GBPUSD | 40 | +0.044 | 62 % | 1.09 | +34.90 |
| SOLUSD | 29 | -0.312 | 59 % | 0.56 | -207.88 |
| US500 | 35 | +0.160 | 63 % | 1.31 | +128.38 |
| USDJPY | 39 | -0.280 | 41 % | 0.48 | -252.56 |
| USTEC | 16 | -0.448 | 56 % | 0.43 | -137.90 |
| XAGUSD | 5 | +0.050 | 40 % | 1.11 | +0.90 |
| XAUUSD | 26 | -0.087 | 50 % | 0.84 | -50.14 |
| XBRUSD | 17 | -0.279 | 41 % | 0.67 | -83.86 |
| XNGUSD | 24 | -0.348 | 38 % | 0.47 | -180.54 |
| XPTUSD | 11 | -0.273 | 36 % | 0.66 | -71.78 |
| XTIUSD | 27 | -0.427 | 48 % | 0.58 | -155.12 |

| Group | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| USD | 140 | -0.005 | 51 % | 0.99 | -13.34 |
| JPY | 138 | -0.308 | 54 % | 0.61 | -794.10 |
| Metals | 42 | -0.119 | 45 % | 0.80 | -121.02 |
| Indices | 76 | +0.057 | 63 % | 1.10 | +95.42 |
| Energy | 68 | -0.362 | 43 % | 0.57 | -419.52 |
| Crypto | 108 | -0.383 | 55 % | 0.49 | -796.74 |

| Half-year | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| 2024-H2 | 202 | -0.098 | 53 % | 0.85 | -506.94 |
| 2025-H1 | 225 | -0.075 | 57 % | 0.87 | -362.14 |
| 2025-H2 | 145 | -0.504 | 44 % | 0.45 | -1,180.22 |

## validation: 05 Jan 2026 to 29 Jun 2026

| Measure | Value |
|---|---|
| Equity | $5,000.00 → $3,389.46 (-32.2 %) |
| Largest fall from a peak | 35.0 % |
| Worst day; best day | -5.4 %; +6.6 % |
| Whole trades finished | 349 (and 32 positions still open at the end) |
| Mean result per trade, after costs | **-0.129 R** (95 % interval -0.355 to +0.088) |
| Trades won | 52.4 % |
| Profit factor | 0.82 |
| Longest losing run | 8 trades |
| Worst trade; best trade | -5.75 R; +19.77 R |
| Money check | balance changed -1,466.31: finished trades -1,041.60, parts of trades still open -424.71 |
| Commission paid | $415.82 |
| Margin use: typical; highest | 13 %; 21 % |
| Stop-outs by the broker | 0 |
| Orders refused at their price for lack of margin | 0 |

| Scout's orders | Count |
|---|---|
| Placed | 756 |
| Filled | 357 |
| Not placed: portfolio limit | 4753 |
| Not placed: size refused | 1977 |
| Not placed: no live price | 590 |
| Not placed: stale candles | 471 |
| Not placed: halted | 148 |
| Not placed: setup too old | 31 |
| Refused because the level had passed | 3467 |

| Symbol | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| AUDJPY | 25 | -0.361 | 56 % | 0.61 | -182.50 |
| BTCUSD | 24 | +0.000 | 54 % | 1.00 | +1.22 |
| DE40 | 17 | +0.290 | 47 % | 1.61 | +101.20 |
| ETHUSD | 23 | -0.040 | 39 % | 0.94 | -10.08 |
| EURJPY | 24 | -0.654 | 58 % | 0.43 | -366.56 |
| EURUSD | 36 | +0.082 | 56 % | 1.14 | +47.00 |
| GBPJPY | 28 | -1.007 | 36 % | 0.21 | -642.34 |
| GBPUSD | 40 | -0.405 | 52 % | 0.50 | -369.58 |
| SOLUSD | 10 | +0.982 | 90 % | 5.35 | +222.36 |
| US500 | 27 | -0.237 | 44 % | 0.59 | -159.08 |
| USDJPY | 35 | -0.071 | 51 % | 0.89 | -35.38 |
| USTEC | 15 | +0.677 | 80 % | 5.01 | +238.94 |
| XBRUSD | 10 | +0.572 | 70 % | 2.76 | +122.64 |
| XNGUSD | 18 | +0.586 | 39 % | 1.85 | +209.12 |
| XTIUSD | 17 | -0.599 | 53 % | 0.34 | -218.56 |

| Group | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| USD | 111 | -0.142 | 53 % | 0.80 | -357.96 |
| JPY | 77 | -0.687 | 49 % | 0.39 | -1,191.40 |
| Metals | 0 | | | | |
| Indices | 59 | +0.147 | 54 % | 1.33 | +181.06 |
| Energy | 45 | +0.135 | 51 % | 1.20 | +113.20 |
| Crypto | 57 | +0.156 | 54 % | 1.29 | +213.50 |

| Half-year | Trades | Mean R | Won | Profit factor | USD |
|---|---|---|---|---|---|
| 2026-H1 | 349 | -0.129 | 52 % | 0.82 | -1,041.60 |
