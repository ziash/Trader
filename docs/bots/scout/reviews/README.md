# Scout independent reviews

**Each audit of Scout is a separate, dated, versioned report, so results can be compared over time.** The newest row is the current review.

| Version | Date | Report | Code reviewed | Findings (P0 / P1 / P2 / P3) | Verdict |
|---|---|---|---|---|---|
| **v1.0** | 08-Nov-2026 | [Independent review v1.0](2026-11-08-v1.0-independent-review.md) | Trader `0a50a52`, Rewind `663aa94`, Scout spec v1.9 | 3 / 12 / 21 / 3 | Well-built demo system; no trading edge shown; money at risk not yet controlled. Keep on demo |

The brief given to the reviewer: [audit-prompt.md](audit-prompt.md).

## Implementation status of review v1.0

Phase 1 is built, tested and **live on the Pepperstone demo** since 08-Nov-2026 13:33 UTC (Scout v1.26, build `Scout-667ede8`).

| Finding | Status |
|---|---|
| F-02 loss limits and global new-entry stop | Built (local command; dashboard switch to come, P-10) |
| F-03 size raised by the broker minimum | Built |
| F-04, F-05, F-07, F-16, F-17, F-18 placement, cancel, recovery, duplicates, saves, client ID | Built |
| F-06 applied shown while a cancel is open | Built for the acknowledgement; requested and confirmed on the dashboard to come (P-10) |
| F-10 live-price check | Built. Rewind already refuses a passed level. Broker behaviour on such an order not tested separately |
| F-15 test harness; CI (P-12) | Built. Scout-side tests for TM health wait for its gate (Phase 2). Abnormal spread is covered in TM's tests; a Scout entry guard waits for its experiment (Phase 4) |
| F-20 old setups | Built |
| F-01, F-19, F-25 sizing from the money at risk (Phase 2, P-02) | Built and **on** at 0.5 % on the Pepperstone demo (Scout v1.28). Limits across trades, symbols and groups: next row |
| F-01, F-23 limits across the account, one margin limit shared with TM (Phase 2, P-03) | Built and deployed **off** on the Pepperstone demo (Scout v1.34). Exposure is visible with `--risk` |
| F-14 TM health check (Phase 2) | Built and live on the demos (Scout v1.30, TM v1.16) |
| F-08, F-09, F-24 margin by scenario, stress test, protection label (Phase 2, P-05) | Built and live on the demos (Scout v1.32, TM v1.18). The Portfolio Risk Manager reads the same facts straight from the broker (Scout v1.33) |
| F-13 remote dead-man alarm | **Open:** needs the owner's choice of an off-machine channel |
| Phases 3–6 | Not started |

## Rules

- **File name:** `YYYY-MM-DD-vN.M-independent-review.md`.
- **Version:** a new audit raises N (`v2.0`). A correction to an existing report raises M (`v1.1`) and is listed in that report's header.
- **Published reports are records.** A new audit never rewrites an earlier report.
- **Finding IDs carry over.** A finding keeps its `F-nn` ID in later audits; new findings take the next free number.

## Re-running the audit

1. Update "Current operating context" in [audit-prompt.md](audit-prompt.md) and raise its version.
2. Give the reviewer the prompt and the latest report, with this addition: *"Treat the previous report as a claim to verify, not as fact. For every earlier finding, state Fixed, Partly fixed, Open, No longer applies or Disputed, with current evidence. Say what changed since the last audit."*
3. Record the commits reviewed (Trader, Rewind, Scout spec) and the end date of the trial data.
4. Recalculate the evidence instead of copying it:

   | Evidence | Source |
   |---|---|
   | Test suite | `dotnet test Trader.sln`, on a separate clone |
   | Replay statistics by symbol, half-year, role, retracement and hour; drawdown; day-block bootstrap | `~/rewind/lab/outcomes.csv` (`scripts/lens-replay.sh`) |
   | Restart check: detector events from 30-day restarts against an uninterrupted run | `Trader.Scout --replay` on `~/rewind/lab/candles/<SYMBOL>-H1.csv` |
   | Whole-trade results by symbol and role | `~/tradebot/dashboard/outbox/logs/*/*/Dashboard/*/deals.csv` |
   | Order lifecycle: placed, filled, refused, seconds from placement to fill | `~/tradebot/logs/<broker>/<ctid>/Scout-*/*/orders.csv` |
   | Memory against the broker | `~/tradebot/run/scout/*.json` against the account snapshots |
   | Secret scan of the Git history | Report only file and commit, never a value |

5. Save the report under a new file name, add a row at the top of the table above, and update the links in the [front page](../../../../README.md), the [document map](../../../README.md), the [Scout folder page](../README.md) and [scout.md](../scout.md).
6. Audits are read-only. Changes follow only after the owner approves them.
