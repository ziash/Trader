# CLAUDE.md — Trader project

Standing instructions for Claude Code in this repository. Read this first, then `docs/plan.md`.

## What this project is

A semi-automated short-to-mid-term trend-trading bot. The human (Zeeshan, owner) reads the market on TradingView; a C# / .NET 10 service on Ubuntu executes and manages trades through the **cTrader Open API**. First deliverable is an **Audit Bot** that compares execution quality at two brokers on demo accounts.

- **Single source of truth:** `docs/plan.md`. If code and plan disagree, stop and ask; do not silently pick one.
- **Environment setup record:** `docs/setup.md`; check with `bash scripts/check-env.sh`.
- **Current state:** Phase 1 complete (plan v1.16, steps in Section 8.1): connect, stay connected, price feed to disk, instance registry, probe orders, alerts (6.2.4), daily report (6.5.1); framework reusable for many bots (plan 5.3). **Phase 2 audit run in progress since 18-Oct-2026**: the Audit Bot runs as the systemd user service `trader-auditbot` from `~/tradebot/bin/AuditBot` — do not stop or restart it, or delete anything in `~/tradebot/logs`, without the owner's OK. Deploy code changes with `scripts/deploy-auditbot.sh` (it does not restart the bot). The daily report runs from the timer `trader-auditreport.timer` (00:15 UTC, `~/tradebot/bin/AuditReport`, deploy with `scripts/deploy-auditreport.sh`); it only reads the logs, so redeploying it needs no OK. Don't overwrite reports in `~/tradebot/reports/` with partial ones (a day is complete only after 00:00 UTC); preview into a scratch folder with `--reports <dir>`.

## Working rules

1. **Code only when the owner says so.** Spec and planning discussions do not authorise coding. When asked to build something, build that step, not the next ones.
2. **Plan is spec-driven.** Any change in decisions, scope or design goes into `docs/plan.md`:
   - bump the version in the header table,
   - add a row to the change log (Section 11),
   - update open items (Section 12) and glossary (Section 13) if affected,
   - commit with a message like `plan v1.8: <what changed>`.
   Code-only commits do not need a plan version unless they change a decision.
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
| Broker B | Pepperstone demo **5357135** — Hedging, USD, 1:200 — `ctidTraderAccountId` 49124666 |
| Demo endpoint | `demo.ctraderapi.com:5035` (Protobuf over TLS) |
| Live endpoint | `live.ctraderapi.com:5035` — **do not use** before Phase 5 |
| Rate limits | 50 req/s, 5 req/s for historical data (per connection) |
| Heartbeat | every 10 s |

Account numbers above are broker login numbers. The API uses `ctidTraderAccountId`, a different number obtained from `ProtoOAGetAccountListByAccessTokenReq` (run `dotnet run --project src/Trader.ListAccounts` to list them).

**Demo only.** No code may connect to the live endpoint or place orders on a live account until the plan says Phase 5 has started. Make `CTRADER_ENV=demo` the default and refuse `live` unless explicitly enabled by the owner.

## Tech stack and conventions

- **.NET 10 LTS** (`net10.0`), C#, nullable reference types on, warnings as errors where practical.
- cTrader library: NuGet `cTrader.OpenAPI.Net` (old, targets .NET 6 — see plan 5.1 library risk; fallback is generating classes from Spotware's `.proto` files).
- Logging: Serilog, **UTC timestamps** everywhere.
- Layout: source in `src/`, tests in `tests/`, one solution file at the repo root.
- **Many bots, one framework (plan 5.3, decision 14):** shared code lives in `src/Trader.Core` (usage guide: `src/Trader.Core/README.md`); each bot is `src/Trader.<BotName>/`, started from a copy of `src/Trader.SampleBot`, using `BotRunner`. Never copy connection/token/logging code into a bot; anything two bots need goes into `Trader.Core`. A change there affects every bot: run all tests.
- Runtime data lives outside the repo, under `~/tradebot/` (`config/`, `run/instances/`, `logs/`, `reports/`) — plan Section 6.3. Reference copies of the config files are in the repo's `config/`; the bot reads `~/tradebot/config/`. Keep both in step when a config changes.
- Testing bots by hand: stop a background bot by its exact PID (`pgrep -f '^dotnet src/Trader.<Bot>'`); a `pkill -f` pattern can match the shell running it.
- Every order carries a label from `BotIdentity.Label()`: instance ID + optional leg, e.g. `TrendBot-3f9a1c-T1-S1` (plan 5.3, 10.8.2); max 100 characters. Reconcile positions after a restart by bot name, not instance ID.
- **Ownership rule (plan 5.3):** a bot only modifies/closes orders and positions with its own label, always by `positionId` (never "close all on a symbol"); everything else (the owner's manual trades) is foreign — never touched, but counted for risk.
- **Orders only from the Primary:** a bot may place orders on an account only while `InstanceRegistry.RoleOn(account)` is Primary (plan 6.4).
- Every live position must always have a server-side protective order (hedge buy-stop before 1R, stop-loss after) — plan Section 7 protection rule. Audit Bot probes carry a wide backup stop-loss (plan 6.2.2).
- Live tests place real orders on the **demo** accounts (probes are 0.01 lot). Leave the accounts flat afterwards: check with a reconcile; never close a position that lacks the bot's label.

## Before finishing a task

- `dotnet build` passes with no errors; run tests if they exist.
- `bash scripts/check-env.sh` still passes if the environment was touched.
- Secrets check on the diff, then commit and `git push` (the owner works from GitHub `main`).
