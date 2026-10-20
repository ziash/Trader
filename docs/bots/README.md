# Bots

Each bot is a **project of its own**: its document holds its goal, rules, decisions, open items and history, with its own version number. What all bots share is in [shared.md](../shared.md); the reusable code is the [Open Code Pieces](../open-code-pieces.md).

| Bot | What it does | Status | Document |
|---|---|---|---|
| **Audit Bot** | Compares spreads, slippage, latency and reliability at two brokers with tiny probe orders; daily report | Built and finished; running (audit run since 18-Oct-2026) | [audit-bot.md](audit-bot.md) |
| **TM** (Trade Manually) | You open positions in cTrader; TM mirrors and hedges them and manages the hedges | Working on demo (steps 1–5 of 6); service next | [tm.md](tm.md) |
| **Auto Bot** | Semi-automated trend trading: TradingView decisions, hedge-and-recover, 4 legs, pyramiding | Concept; many questions open | [auto-bot.md](auto-bot.md) |

**Tools** (not bots): [`Trader.ListAccounts`](../../src/Trader.ListAccounts) lists the accounts a token can reach; [`Trader.AuditReport`](../../src/Trader.AuditReport) builds the Audit Bot's reports; [`Trader.SampleBot`](../../src/Trader.SampleBot) is the template a new bot starts from.

## Adding a bot

1. Copy [`_template.md`](_template.md) to `docs/bots/<bot>.md`; agree its goal and rules there (version v1.0).
2. Add a row to the table above, to the table in [shared.md §1](../shared.md#1-the-projects) and to the main [README](../../README.md).
3. When the code starts: `src/Trader.<Bot>/` from a copy of `Trader.SampleBot` ([Open Code Pieces §5](../open-code-pieces.md#5-adding-a-bot)), with a short `README.md` pointing to the bot's document.
4. Every change of decision, scope or design: bump the bot document's version, add a history row, commit as `<bot> vX.Y: <what changed>`.
