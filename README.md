# Trader
Trading | Spec Driven Development

Trading bots for **cTrader** (C# / .NET 10, Ubuntu laptop). The owner reads the market; the bots place, mirror, hedge and protect orders through the cTrader Open API. **Each bot is a project of its own** with its own document, and they all share the same reusable code, the **Open Code Pieces**. A private web page, the **Dashboard**, shows what they do.

> **Demo accounts only.** Every bot runs on the two demo accounts (IC Markets, Pepperstone) and never connects to a live account ([safety rules](docs/shared.md#4-safety-rules-for-every-bot)).

**Start here:** pick a project below. Each row links every document that belongs to it. The full map of documents is [docs/README.md](docs/README.md).

## Projects

### Bots

| Bot | What it does | Status (07-Nov-2026) | Documents | Code |
|---|---|---|---|---|
| **Scout** | Watches the owner's charts for the "pause, then stop order" setup and places the first stop order; TM manages it after | Trial on demo since 03-Nov-2026 (`trader-scout`): IC Markets, 18 symbols, 1H | [**scout.md**](docs/bots/scout/scout.md) — the bot<br>[**Scout Lens**](docs/bots/scout/scout-lens.md) — AI layer proposal, 5 options with charts, waiting for the owner's choice<br>[Setup rules](https://github.com/ziash/Scout/blob/main/docs/spec.md) — in the Scout repository | [`src/Trader.Scout`](src/Trader.Scout) |
| **TM** (Trade Manually) | You open positions in cTrader; TM mirrors and hedges them, protects each leg and manages the hedges | Built; runs on demo as a service (`trader-tm`); also manages Scout's orders | [**tm.md**](docs/bots/tm.md) | [`src/Trader.TM`](src/Trader.TM) |
| **Audit Bot** | Compared two brokers' spreads, slippage, fill speed and reliability with tiny probe orders; daily report | **Finished.** Audit run 18-Oct → 04-Nov-2026, bot stopped and disabled. Result: IC Markets chosen | [**audit-bot.md**](docs/bots/audit-bot.md) | [`src/Trader.AuditBot`](src/Trader.AuditBot), [`src/Trader.AuditReport`](src/Trader.AuditReport) |
| **Auto Bot** | Semi-automated trend trading: TradingView decisions, hedge-and-recover, 4 legs, pyramiding | Concept; many questions open; nothing built | [**auto-bot.md**](docs/bots/auto-bot.md) | — |

### Supporting projects

| Project | What it is | Status (07-Nov-2026) | Documents | Code |
|---|---|---|---|---|
| **Dashboard** | Private web page at [trader.uzlabz.com](https://trader.uzlabz.com): Scout's setups and orders with pictures, TM's trades, accounts, trading conditions, bot status, Audit result; steers Scout (symbols on/off, keep/skip, settings). Google sign-in for people, M2M sign-in for Claude | Live; build steps 0–6 done, step 7 (a few days' use) started | [**dashboard.md**](docs/dashboard/dashboard.md) — the project<br>[Design brief](docs/dashboard/dashboard-design-brief.md) — what the screens must show<br>[Design handoff](docs/dashboard/design/HANDOFF.md) — tokens and screen sources | [`web/dashboard`](web/dashboard), [`src/Trader.DashboardExport`](src/Trader.DashboardExport) |
| **Open Code Pieces** | The reusable code every bot is built from: connection, tokens, orders, prices and candles, logs, instance registry, notifications | In use by Scout, TM, the Audit Bot and the Dashboard export | [**open-code-pieces.md**](docs/open-code-pieces.md) · [usage guide](src/Trader.Core/README.md) | [`src/Trader.Core`](src/Trader.Core) |

### Proposals waiting for a decision

| Proposal | For | Document |
|---|---|---|
| **Scout Lens** — an AI layer that takes, skips or shrinks the setups Scout's rules find | Scout | [scout-lens.md](docs/bots/scout/scout-lens.md) |

## Shared reference

| Document | What is in it |
|---|---|
| [shared.md](docs/shared.md) | Brokers and accounts, the machine and services, **safety rules for every bot**, folders and file format, how the documents are kept, glossary |
| [issue-log.md](docs/issue-log.md) | What went wrong with the bots, why, and the fix. Read it before changing sizing, hedging or protection |
| [setup.md](docs/setup.md) | How the laptop, accounts and the Open API app were set up; check with `bash scripts/check-env.sh` |
| [CLAUDE.md](CLAUDE.md) | Standing instructions for Claude Code in this repository |
| [Archive](docs/archive/plan-v1.22.md) | The old single plan (v1.0–v1.22), split into the project documents on 20-Oct-2026 |

## Related repositories

Separate projects that share no code with this one:

| Repository | What it is | Used by |
|---|---|---|
| [Scout](https://github.com/ziash/Scout) | The setup rules (spec), Scout_Pine (the TradingView indicator) and research | Scout (the bot here follows its rules) |
| [Rewind](https://github.com/ziash/Rewind) | Replays past prices with a simulated broker, so TM can trade history (`CTRADER_ENV=replay`, localhost only) | TM ([open-code-pieces.md §3](docs/open-code-pieces.md#3-connection-and-tokens)) |
| [Levels](https://github.com/ziash/Levels) | The day's expected range and options-positioning levels on the TradingView chart; never trades | The owner, on TradingView |

## Repository layout

| Folder | What is in it |
|---|---|
| [`docs/`](docs/README.md) | Every project document ([map](docs/README.md)): `bots/` (one document per bot; Scout has its own folder), `dashboard/`, shared reference, images, archive |
| [`src/`](src) | The code, one .NET project per folder: `Trader.Core` (Open Code Pieces), `Trader.Scout`, `Trader.TM`, `Trader.AuditBot`, `Trader.AuditReport`, `Trader.DashboardExport`, plus the tools `Trader.ListAccounts` (lists the accounts a token can reach) and `Trader.SampleBot` (template for a new bot) |
| [`tests/`](tests) | One test project per code project (`Trader.<Name>.Tests`) |
| [`web/dashboard/`](web/dashboard) | The Dashboard site: `ui/` (Next.js screens, exported as plain files), `api/` (PHP API), `openapi.yaml` (the API contract) |
| [`config/`](config) | Reference copies of the bots' settings; the running bots read `~/tradebot/config/` |
| [`scripts/`](scripts) | `deploy-<bot>.sh` (publish a bot and install its service), the Dashboard's upload, sync and deploy scripts, `check-env.sh` |
| [`deploy/`](deploy) | systemd user units (services and timers) the deploy scripts install |

Logs, reports and state live outside the repository in `~/tradebot/` ([shared.md §5](docs/shared.md#5-folders-and-files)). Secrets (API client secret, tokens, sign-in files) are never committed.

## Build, test, run

```bash
dotnet build
```

```bash
dotnet test
```

How to run and operate each bot or service is in its own document:

| Project | Run and operate |
|---|---|
| Scout | [scout.md §4](docs/bots/scout/scout.md#4-run-and-operate) |
| TM | [tm.md §10](docs/bots/tm.md#10-run-and-operate) |
| Audit Bot | [audit-bot.md §6](docs/bots/audit-bot.md#6-run-and-operate) — stopped; do not start it without the owner's OK |
| Dashboard | [dashboard.md §7](docs/dashboard/dashboard.md#7-run-and-operate) |

## How the documents are kept

Every change of decision, scope or design goes into the document of the project it belongs to: bump its version, add a history row, and commit with a message like `tm v1.1: news pop-ups`. A new bot gets a document from [the template](docs/bots/_template.md) and a row in the table above ([how](docs/bots/README.md#adding-a-bot)). Details: [shared.md §6](docs/shared.md#6-how-the-documents-are-kept).
