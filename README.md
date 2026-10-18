# Trader
Trading | Spec Driven Development

Semi-automated trend-trading bot for cTrader (C# / .NET, Ubuntu).

- **Plan (single source of truth):** [docs/plan.md](docs/plan.md)
- **Instructions for Claude Code:** [CLAUDE.md](CLAUDE.md)
- **Environment setup (Phase 0):** [docs/setup.md](docs/setup.md) — check with `bash scripts/check-env.sh`
- **Images used by the plan:** [docs/images/](docs/images/)
- **Bot config (reference copies):** [config/](config/) — the bots read `~/tradebot/config/`; logs go to `~/tradebot/logs/`
- **Bot code:** `src/` (from Phase 1) — `Trader.Core` (shared framework for all bots; [usage guide](src/Trader.Core/README.md)), `Trader.SampleBot` (template for a new bot), `Trader.ListAccounts` (connect + list accounts), `Trader.AuditBot` (Audit Bot; so far: stays connected, reconnects, refreshes tokens)

```bash
dotnet build
dotnet test
dotnet run --project src/Trader.ListAccounts          # add --verbose to see each request/response
dotnet run --project src/Trader.AuditBot              # runs until Ctrl+C; --help lists the options
dotnet run --project src/Trader.AuditBot -- --test-probe-now   # one probe window at once (demo orders)
dotnet run --project src/Trader.SampleBot -- --account 49123893
```

Running the Audit Bot for real (background service, survives closing the terminal):

```bash
scripts/deploy-auditbot.sh                          # publish to ~/tradebot/bin/AuditBot + install the service
systemctl --user enable --now trader-auditbot       # start now and at every login
systemctl --user status trader-auditbot             # running?
journalctl --user -u trader-auditbot -f             # live output
systemctl --user stop trader-auditbot               # stop
```

Every change to the plan: update the version and change log in `docs/plan.md`, then commit with a message like `plan v1.4: define Scenario 2`.

Secrets (API client secret, tokens) are never committed — see `.gitignore`.
