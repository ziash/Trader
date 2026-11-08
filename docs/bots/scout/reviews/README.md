# Scout independent reviews

**Each audit of Scout is a separate, dated, versioned report, so results can be compared over time.** The newest row is the current review.

| Version | Date | Report | Code reviewed | Findings (P0 / P1 / P2 / P3) | Verdict |
|---|---|---|---|---|---|
| **v1.0** | 08-Nov-2026 | [Independent review v1.0](2026-11-08-v1.0-independent-review.md) | Trader `0a50a52`, Rewind `663aa94`, Scout spec v1.9 | 3 / 12 / 21 / 3 | Well-built demo system; no trading edge shown; money at risk not yet controlled. Keep on demo |

The brief given to the reviewer: [audit-prompt.md](audit-prompt.md).

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
