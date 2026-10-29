# Trader
Trading | Spec Driven Development

Trading bots for cTrader (C# / .NET 10, Ubuntu). **Each bot is a project of its own**, with its own document; they all build on the same reusable code, the **Open Code Pieces**.

## Projects

| Project | What it is | Status | Document |
|---|---|---|---|
| **Audit Bot** | Compares spreads, slippage, latency and reliability at two brokers with tiny probe orders; daily report | Built and finished; running (audit run since 18-Oct-2026) | [docs/bots/audit-bot.md](docs/bots/audit-bot.md) |
| **TM** (Trade Manually) | You open positions in cTrader; TM mirrors and hedges them and manages the hedges | Built; runs on demo as a service (`trader-tm`) | [docs/bots/tm.md](docs/bots/tm.md) |
| **Auto Bot** | Semi-automated trend trading: TradingView decisions, hedge-and-recover, 4 legs, pyramiding | Concept; many questions open | [docs/bots/auto-bot.md](docs/bots/auto-bot.md) |
| **Open Code Pieces** | The reusable code all bots are built from (`src/Trader.Core`) | In use by the Audit Bot and TM | [docs/open-code-pieces.md](docs/open-code-pieces.md) |

- **Shared by all projects** — brokers and accounts, machine, safety rules, folders, file format, glossary: [docs/shared.md](docs/shared.md)
- **New bot** — a document from [docs/bots/_template.md](docs/bots/_template.md) and a row here ([how](docs/bots/README.md#adding-a-bot))
- **History before 20-Oct-2026** — the old single plan, archived: [docs/archive/plan-v1.22.md](docs/archive/plan-v1.22.md)

## Repository

- **Instructions for Claude Code:** [CLAUDE.md](CLAUDE.md)
- **Environment setup:** [docs/setup.md](docs/setup.md) — check with `bash scripts/check-env.sh`
- **Images used by the documents:** [docs/images/](docs/images/)
- **Bot config (reference copies):** [config/](config/) — the bots read `~/tradebot/config/`; logs go to `~/tradebot/logs/`
- **Code:** `src/` — `Trader.Core` (the Open Code Pieces; [usage guide](src/Trader.Core/README.md)), `Trader.SampleBot` (template for a new bot), `Trader.ListAccounts` (connect + list accounts), `Trader.AuditBot` and `Trader.AuditReport` (Audit Bot), `Trader.TM` (TM)

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

Running TM as a background service (same pattern; [tm.md §10](docs/bots/tm.md#10-run-and-operate)):

```bash
scripts/deploy-tm.sh                                # publish to ~/tradebot/bin/TM + install the service
systemctl --user enable --now trader-tm             # start now and at every login
```

TM on past prices: [Rewind](https://github.com/ziash/Rewind) (separate project) replays history with a simulated broker; with Rewind running, `~/rewind/tm/run-tm.sh` (Windows: `run-tm.ps1`) starts TM against it (`CTRADER_ENV=replay`, localhost only — [open-code-pieces.md §3](docs/open-code-pieces.md#3-connection-and-tokens)).

The day's expected range and options-positioning levels on the TradingView chart: [Levels](https://github.com/ziash/Levels) (separate project; never trades, shares no code with Trader).

The owner's "pause, then stop order" setup marked and alerted on TradingView: [Scout](https://github.com/ziash/Scout) (separate project). Its indicator Scout_Pine never trades; orders placed by hand in cTrader are taken over by TM as usual. A Scout bot that places orders is a later phase.

Daily report (runs by itself every day at 00:15 UTC; reports in `~/tradebot/reports/`, open in Google Sheets):

```bash
scripts/deploy-auditreport.sh                       # publish to ~/tradebot/bin/AuditReport, install + enable the timer
systemctl --user list-timers trader-auditreport     # next run
systemctl --user start trader-auditreport           # make any missing reports now
```

Every change of decision, scope or design goes into the project document it belongs to: bump its version, add a history row, and commit with a message like `tm v1.1: news pop-ups` ([how the documents are kept](docs/shared.md#6-how-the-documents-are-kept)).

Secrets (API client secret, tokens) are never committed — see `.gitignore`.
