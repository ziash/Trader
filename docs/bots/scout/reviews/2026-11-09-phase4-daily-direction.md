# Phase 4: the daily direction rule

**The rule misses the pass mark set before the run. It helped in one period and hurt in the other, and trades with and against the daily direction do equally badly. The support and resistance part never acted.** Run on 09-Nov-2026 on Dukascopy's history. Review reference: [P-08](2026-11-08-v1.0-independent-review.md), experiment 5. Rule: [scout.md §2.10](../scout.md). The locked period, Jul to Oct 2026, was not used. The owner deployed the rule to the Pepperstone demo, on, the same day, knowing this result.

## Whole account: real Scout and TM on Rewind

The pass mark, set before the run: at least 0.05 R per trade better in both periods, with the largest fall no worse.

| | Development, Nov 2024 to Dec 2025 | | Validation, Jan to Jun 2026 | |
|---|---|---|---|---|
| | Baseline | Rule on | Baseline | Rule on |
| Equity change | −57.2 % | **−41.1 %** | −44.2 % | **−49.4 %** |
| Largest fall | 63.7 % | **49.5 %** | 46.3 % | **52.3 %** |
| Per trade, after costs | −0.197 R | **−0.165 R** | −0.225 R | **−0.305 R** |
| 95 % range | −0.368 to −0.032 | −0.419 to +0.057 | −0.379 to −0.079 | −0.510 to −0.105 |
| Trades finished | 480 | 378 | 421 | 336 |
| Against the mark | | +0.03 R, fall better: **fails** | | −0.08 R, fall worse: **fails** |

With the rule off, the account's value matches the baseline at all 842 points of the first 35 days, so the switch itself changes nothing else.

The two periods disagree and the ranges overlap heavily, so these runs do not show that the rule helps or harms.

## Every order: with or against the daily direction

Scout's own order alone, with the trailing stop and no TM (`scripts/lens-replay.sh`, up to 30 Jun 2026, without EURGBP). This has far more trades, so it is the steadier measure.

| | Trades | With daily | Trades | Against daily |
|---|---:|---|---:|---|
| Development | 4,953 | −0.128 R (−0.158 to −0.095) | 4,836 | −0.134 R (−0.166 to −0.101) |
| Validation | 2,081 | −0.133 R (−0.185 to −0.083) | 1,988 | −0.147 R (−0.199 to −0.091) |

There is no measurable difference, as with the 4H tag.

## Support and resistance

The owner's rule applies when the daily direction is unknown. The trend test gives a direction from the first strong daily candle onwards and never goes back to unknown, so that case hardly exists: 18 of 18,904 orders in development, none in validation. **No order in either replay was held back by a zone.**

The zones are real enough to label: 32 % of development orders and 43 % of validation orders lie near one. Those near a zone did no better or worse in any consistent way. With the daily direction, for example, near resistance gave −0.138 R and −0.208 R, and no zone −0.127 R and −0.128 R.

## Limits

Dukascopy's prices match real trading in general, not trade by trade ([match report](2026-11-09-phase3-real-week.md)). The rule is the detector's own trend, which turns late by design, so another definition of daily direction could do differently. Zones and their distances are first choices and were not tuned.
