# Trader
Trading | Spec Driven Development

Semi-automated trend-trading bot for cTrader (C# / .NET, Ubuntu).

- **Plan (single source of truth):** [docs/plan.md](docs/plan.md)
- **Bots — one page each:** [docs/bots/](docs/bots/README.md)

## Bots

| Bot | What it does | Status | Details |
|---|---|---|---|
| **Audit Bot** | Compares spreads, slippage, latency and reliability at two brokers with tiny probe orders; daily report | Running (Phase 2 audit, since 18-Oct-2026) | [docs/bots/audit-bot.md](docs/bots/audit-bot.md) |
| **TM** (Trade Manually) | You open positions in cTrader; TM mirrors and hedges them and manages the hedges | Working on demo (steps 1–5 of 6); service next | [docs/bots/tm.md](docs/bots/tm.md) |

New bots get a page from [docs/bots/_template.md](docs/bots/_template.md) and a row here.

## Repository

- **Instructions for Claude Code:** [CLAUDE.md](CLAUDE.md)
- **Environment setup (Phase 0):** [docs/setup.md](docs/setup.md) — check with `bash scripts/check-env.sh`
- **Images used by the plan:** [docs/images/](docs/images/)
- **Bot config (reference copies):** [config/](config/) — the bots read `~/tradebot/config/`; logs go to `~/tradebot/logs/`
- **Bot code:** `src/` (from Phase 1) — `Trader.Core` (shared framework for all bots; [usage guide](src/Trader.Core/README.md)), `Trader.SampleBot` (template for a new bot), `Trader.ListAccounts` (connect + list accounts), `Trader.AuditBot` (Audit Bot: spreads, probe orders, alerts — plan Section 6), `Trader.AuditReport` (daily broker comparison CSV from the Audit Bot's logs — plan 6.5)

```bash
dotnet build
dotnet test
dotnet run --project src/Trader.ListAccounts          # add --verbose to see each request/response
dotnet run --project src/Trader.AuditBot              # runs until Ctrl+C; --help lists the options
dotnet run --project src/Trader.AuditBot -- --test-probe-now   # one probe window at once (demo orders)
dotnet run --project src/Trader.SampleBot -- --account 49123893
dotnet run --project src/Trader.TM                    # TM: mirrors and hedges your manual cTrader positions (demo)
dotnet run --project src/Trader.AuditReport -- --date 2026-10-19      # one day's report → ~/tradebot/reports/
dotnet run --project src/Trader.AuditReport -- --from 2026-10-18 --to 2026-11-01   # summary over a range
```

Running the Audit Bot for real (background service, survives closing the terminal):

```bash
scripts/deploy-auditbot.sh                          # publish to ~/tradebot/bin/AuditBot + install the service
systemctl --user enable --now trader-auditbot       # start now and at every login
systemctl --user status trader-auditbot             # running?
journalctl --user -u trader-auditbot -f             # live output
systemctl --user stop trader-auditbot               # stop
```

Daily report (runs by itself every day at 00:15 UTC; reports in `~/tradebot/reports/`, open in Google Sheets):

```bash
scripts/deploy-auditreport.sh                       # publish to ~/tradebot/bin/AuditReport, install + enable the timer
systemctl --user list-timers trader-auditreport     # next run
systemctl --user start trader-auditreport           # make any missing reports now
```

Every change to the plan: update the version and change log in `docs/plan.md`, then commit with a message like `plan v1.4: define Scenario 2`.

Secrets (API client secret, tokens) are never committed — see `.gitignore`.
