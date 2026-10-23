# CLAUDE.md — Trader project

Standing instructions for Claude Code in this repository. Read this first, then `README.md` and the document of the project you work on.

## What this project is

A semi-automated short-to-mid-term trend-trading bot. The human (Zeeshan, owner) reads the market on TradingView; a C# / .NET 10 service on Ubuntu executes and manages trades through the **cTrader Open API**. First deliverable is an **Audit Bot** that compares execution quality at two brokers on demo accounts.

- **Each bot is a project of its own** (owner, 20-Oct-2026). Its document in `docs/bots/<bot>.md` is the single source of truth for that bot: goal, rules, decisions, open items, history, with its own version. Shared things are in `docs/shared.md` (brokers, accounts, machine, safety rules, folders, file format, glossary); the reusable code is documented in `docs/open-code-pieces.md` (code: `src/Trader.Core`). If code and its document disagree, stop and ask; do not silently pick one. The old single plan is archived in `docs/archive/plan-v1.22.md` (history only; `docs/plan.md` is a pointer page).
- **Projects:** Audit Bot (`docs/bots/audit-bot.md`), TM (`docs/bots/tm.md`), Auto Bot (`docs/bots/auto-bot.md`), Open Code Pieces (`docs/open-code-pieces.md`). A new bot gets a document from `docs/bots/_template.md` and a row in the README, `docs/bots/README.md` and `docs/shared.md` §1.
- **Environment setup record:** `docs/setup.md`; check with `bash scripts/check-env.sh`.
- **TM (Trade Manually)** (`docs/bots/tm.md`): owner opens positions in cTrader; TM mirrors and hedges them. Steps 1–6 done (tm.md §9): built and tested on both demos. Runs as the systemd user service `trader-tm` from `~/tradebot/bin/TM` (deploy with `scripts/deploy-tm.sh`, which does not restart it); stop the service before running TM by hand (`dotnet run --project src/Trader.TM`) so only one TM acts. Testing TM live: a manual position is one with **no label** (open it with a helper, never with a TM label); leave both accounts flat afterwards and the state files in `~/tradebot/run/tm/` empty. TM is the one exception to the ownership rule (open-code-pieces.md §4): it modifies the owner's unlabelled positions on its listed symbols.
- **Audit Bot** (`docs/bots/audit-bot.md`): built and finished. **Audit run in progress since 18-Oct-2026**: the Audit Bot runs as the systemd user service `trader-auditbot` from `~/tradebot/bin/AuditBot` — do not stop or restart it, or delete anything in `~/tradebot/logs`, without the owner's OK. Deploy code changes with `scripts/deploy-auditbot.sh` (it does not restart the bot). The daily report runs from the timer `trader-auditreport.timer` (00:15 UTC, `~/tradebot/bin/AuditReport`, deploy with `scripts/deploy-auditreport.sh`); it only reads the logs, so redeploying it needs no OK. Don't overwrite reports in `~/tradebot/reports/` with partial ones (a day is complete only after 00:00 UTC); preview into a scratch folder with `--reports <dir>`.
- **Auto Bot** (`docs/bots/auto-bot.md`): concept only, many open questions; nothing built.

## Working rules

1. **Code only when the owner says so.** Spec and planning discussions do not authorise coding. When asked to build something, build that step, not the next ones.
2. **Spec-driven, per project.** Any change in decisions, scope or design goes into the document of the project it belongs to (a bot's document, `open-code-pieces.md` or `shared.md`):
   - bump that document's version in its header table,
   - add a row to its history table (last section),
   - update its open items, and the glossary in `shared.md` if a new term appears,
   - commit with a message like `tm v1.1: <what changed>` (or `audit-bot v1.1: …`, `open-code-pieces v1.1: …`, `shared v1.1: …`).
   Code-only commits do not need a version unless they change a decision. Code comments point at document sections, e.g. `(tm.md §4)`.
3. **Define difficult terms.** The owner is a telecom core-network engineer, not a C# developer. When introducing a technical term (C#, .NET, trading, networking), define it briefly in plain words.
4. **Explain before large changes.** For anything bigger than a small fix, say what you will do in a few lines first.
5. **Short, direct answers.** No long recaps of steps already done.

## Secrets — never break these

- Credentials live only in `~/.config/trader/secrets.env` (permissions 600), outside the repo:
  `CTRADER_CLIENT_ID`, `CTRADER_CLIENT_SECRET`, `CTRADER_ACCESS_TOKEN`, `CTRADER_REFRESH_TOKEN`, `CTRADER_ENV` (= `demo`), and `CTRADER_ACCESS_TOKEN_EXPIRES_UTC` (written by the bot on each refresh).
- **Never print, log, echo or commit** any of these values — not in code, tests, logs, commit messages or chat output. Log only "set / not set" or a masked form (first 4 chars + `…`).
- Never `cat` the secrets file. Read it from code only.
- Before every commit, check the diff contains no secret values (`git diff --cached`).
- The refresh token is replaced on every refresh: code that refreshes tokens must write the new pair back to `secrets.env` atomically (write temp file, then rename) and keep permissions 600. Use `TokenRefresher` / `SecretsFile.WriteTokens` (they hold `secrets.env.lock`); never write the file another way.

## Accounts and endpoints

| Item | Value |
|---|---|
| Open API app | "Trend Audit Bot", ID 43770, scope `trading` |
| Broker A | IC Markets (Raw Trading Ltd) demo **10124393** — Hedging, Raw Spread, USD, 1:500 — `ctidTraderAccountId` 49123893 |
| Broker B | Pepperstone demo **5357135** — Hedging, Razor, USD, 1:200 — `ctidTraderAccountId` 49124666 |
| Demo endpoint | `demo.ctraderapi.com:5035` (Protobuf over TLS) |
| Live endpoint | `live.ctraderapi.com:5035` — **do not use** before Phase 5 |
| Replay | Rewind (`~/code/Rewind`, a separate project) on `localhost:5036`; `CTRADER_ENV=replay`, localhost only; secrets in `~/rewind/tm/secrets.env` (fake), run with `~/rewind/tm/run-tm.sh` (Windows: `run-tm.ps1`; Rewind's certificate authority via `CTRADER_CA_FILE`) |
| Rate limits | 50 req/s, 5 req/s for historical data (per connection) |
| Heartbeat | every 10 s |

Account numbers above are broker login numbers. The API uses `ctidTraderAccountId`, a different number obtained from `ProtoOAGetAccountListByAccessTokenReq` (run `dotnet run --project src/Trader.ListAccounts` to list them).

**Demo only.** No code may connect to the live endpoint or place orders on a live account until a go-live is decided and recorded in that bot's document (shared.md §4). Make `CTRADER_ENV=demo` the default and refuse `live` unless explicitly enabled by the owner. `CTRADER_ENV=replay` reaches only localhost (open-code-pieces.md §3); never weaken that check.

## Tech stack and conventions

- **.NET 10 LTS** (`net10.0`), C#, nullable reference types on, warnings as errors where practical.
- cTrader library: NuGet `cTrader.OpenAPI.Net` (old, targets .NET 6 — see open-code-pieces.md §3 library risk; fallback is generating classes from Spotware's `.proto` files).
- Logging: Serilog, **UTC timestamps** everywhere.
- Layout: source in `src/`, tests in `tests/`, one solution file at the repo root.
- **Many bots, one framework (open-code-pieces.md §4, shared.md §7):** shared code — the Open Code Pieces — lives in `src/Trader.Core` (usage guide: `src/Trader.Core/README.md`); each bot is `src/Trader.<BotName>/`, started from a copy of `src/Trader.SampleBot`, using `BotRunner`. Never copy connection/token/logging code into a bot; anything two bots need goes into `Trader.Core`. A change there affects every bot: run all tests.
- Runtime data lives outside the repo, under `~/tradebot/` (`config/`, `run/instances/`, `run/tm/`, `logs/`, `reports/`, `bin/`) — shared.md §5. Reference copies of the config files are in the repo's `config/`; the bot reads `~/tradebot/config/`. Keep both in step when a config changes.
- Testing bots by hand: stop a background bot by its exact PID (`pgrep -f '^dotnet src/Trader.<Bot>'`); a `pkill -f` pattern can match the shell running it.
- Messages to the owner go through `bot.Notify` (`Trader.Core/Notifications`, routed by `~/tradebot/config/notifications.json`), never through a bot's own pop-up or messaging code; new channels are added there.
- Every order carries a label from `BotIdentity.Label()`: instance ID + optional leg, e.g. `TrendBot-3f9a1c-T1-S1` (open-code-pieces.md §4); max 100 characters. Reconcile positions after a restart by bot name, not instance ID.
- **Ownership rule (open-code-pieces.md §4):** a bot only modifies/closes orders and positions with its own label, always by `positionId` (never "close all on a symbol"); everything else (the owner's manual trades) is foreign — never touched, but counted for risk.
- **Orders only from the Primary:** a bot may place orders on an account only while `InstanceRegistry.RoleOn(account)` is Primary (open-code-pieces.md §6).
- Every live position must always have a server-side protective order (hedge buy-stop before 1R, stop-loss after) — protection rule, shared.md §4. Audit Bot probes carry a wide backup stop-loss (audit-bot.md §2.2).
- Live tests place real orders on the **demo** accounts (probes are 0.01 lot). Leave the accounts flat afterwards: check with a reconcile; never close a position that lacks the bot's label.

## Before finishing a task

- `dotnet build` passes with no errors; run tests if they exist.
- `bash scripts/check-env.sh` still passes if the environment was touched.
- Secrets check on the diff, then commit and `git push` (the owner works from GitHub `main`).
