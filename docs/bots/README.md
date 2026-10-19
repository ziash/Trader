# Bots

Every bot has its own page here. The rules themselves live in the [plan](../plan.md) (single source of truth); each page summarises its bot, links to its plan sections, code and config, and says how to run it.

| Bot | What it does | Status | Page |
|---|---|---|---|
| **Audit Bot** | Compares spreads, slippage, latency and reliability at two brokers with tiny probe orders; daily report | Running (Phase 2 audit, since 18-Oct-2026) | [audit-bot.md](audit-bot.md) |
| **TM** (Trade Manually) | You open positions in cTrader; TM mirrors and hedges them and manages the hedges | Building: step 1 of 6 done | [tm.md](tm.md) |

**Tools** (not bots): [`Trader.ListAccounts`](../../src/Trader.ListAccounts) lists the accounts a token can reach; [`Trader.AuditReport`](../../src/Trader.AuditReport) builds the Audit Bot's reports; [`Trader.SampleBot`](../../src/Trader.SampleBot) is the template a new bot starts from. Shared framework for all bots: [`Trader.Core`](../../src/Trader.Core/README.md).

## Adding a bot

1. Agree the spec in the plan (new section, decision, change-log row).
2. Copy [`_template.md`](_template.md) to `docs/bots/<bot>.md` and fill it in.
3. Add a row to the table above and to the **Bots** table in the main [README](../../README.md).
4. When the code starts: `src/Trader.<Bot>/` from a copy of `Trader.SampleBot`, with a short `README.md` pointing to the bot's page.
5. Keep the page's **Status** and **History** up to date with every plan version that changes the bot.
