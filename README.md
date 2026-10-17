# Trader
Trading | Spec Driven Development

Semi-automated trend-trading bot for cTrader (C# / .NET, Ubuntu).

- **Plan (single source of truth):** [docs/plan.md](docs/plan.md)
- **Instructions for Claude Code:** [CLAUDE.md](CLAUDE.md)
- **Environment setup (Phase 0):** [docs/setup.md](docs/setup.md) — check with `bash scripts/check-env.sh`
- **Images used by the plan:** [docs/images/](docs/images/)
- **Bot code:** `src/` (from Phase 1) — `Trader.Core` (shared library), `Trader.ListAccounts` (connect + list accounts), `Trader.AuditBot` (Audit Bot; so far: stays connected, reconnects, refreshes tokens)

```bash
dotnet build
dotnet test
dotnet run --project src/Trader.ListAccounts          # add --verbose to see each request/response
dotnet run --project src/Trader.AuditBot              # runs until Ctrl+C; --test-drop-after 20 tests reconnecting
```

Every change to the plan: update the version and change log in `docs/plan.md`, then commit with a message like `plan v1.4: define Scenario 2`.

Secrets (API client secret, tokens) are never committed — see `.gitignore`.
