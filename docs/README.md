# Documents

Every document in this repository, by project. The [README](../README.md) on the front page lists the projects with their status.

## Bots — [`bots/`](bots/README.md)

| Bot | Documents |
|---|---|
| **Scout** — [`bots/scout/`](bots/scout/README.md) | [scout.md](bots/scout/scout.md) — the bot: rules, logs, run and operate, decisions, history<br>[scout-lens.md](bots/scout/scout-lens.md) — **Scout Lens**, the AI layer proposal (5 options, 10 example charts in [`images/`](bots/scout/images))<br>[Setup rules](https://github.com/ziash/Scout/blob/main/docs/spec.md) — what Scout looks for, in the Scout repository |
| **TM** (Trade Manually) | [tm.md](bots/tm.md) |
| **Audit Bot** | [audit-bot.md](bots/audit-bot.md) |
| **Auto Bot** | [auto-bot.md](bots/auto-bot.md) |
| New bot | [_template.md](bots/_template.md) — copy it ([how](bots/README.md#adding-a-bot)) |

## Supporting projects

| Project | Documents |
|---|---|
| **Dashboard** — [`dashboard/`](dashboard/README.md) | [dashboard.md](dashboard/dashboard.md) — the project: pages, data, hosting, sign-in, run and operate, decisions, history<br>[dashboard-design-brief.md](dashboard/dashboard-design-brief.md) — the brief for the screens' design<br>[design/HANDOFF.md](dashboard/design/HANDOFF.md) — the design result: tokens, screen sources, handoff notes |
| **Open Code Pieces** | [open-code-pieces.md](open-code-pieces.md) — the reusable code every bot uses; code guide: [`src/Trader.Core/README.md`](../src/Trader.Core/README.md) |

## Shared by all projects

| Document | What is in it |
|---|---|
| [shared.md](shared.md) | Brokers and accounts, machine and services, safety rules, folders and file format, how the documents are kept, glossary |
| [issue-log.md](issue-log.md) | What went wrong, why, and the fix |
| [setup.md](setup.md) | Environment, accounts and Open API app setup |
| [`images/`](images) | Pictures used by the documents (Scout's are in [`bots/scout/images/`](bots/scout/images)) |
| [`archive/`](archive/plan-v1.22.md) | The old single plan, v1.0–v1.22 (before 20-Oct-2026); [plan.md](plan.md) says where each part went |
