# Bots

Each bot is a **project of its own**: its document holds its goal, rules, decisions, open items and history, with its own version number. What all bots share is in [shared.md](../shared.md); the reusable code is the [Open Code Pieces](../open-code-pieces.md). What each bot does and its status today: the [projects table on the front page](../../README.md#projects) (kept in one place only).

| Bot | Documents |
|---|---|
| **Scout** | [`scout/`](scout/README.md): [scout.md](scout/scout.md) (the bot) · [Scout Lens](scout/scout-lens.md) (AI layer proposal, waiting for the owner's choice) · [setup rules](https://github.com/ziash/Scout/blob/main/docs/spec.md) (Scout repository) |
| **TM** (Trade Manually) | [tm.md](tm.md) |
| **Audit Bot** | [audit-bot.md](audit-bot.md) |
| **Auto Bot** | [auto-bot.md](auto-bot.md) |

**Tools** (not bots): [`Trader.ListAccounts`](../../src/Trader.ListAccounts) lists the accounts a token can reach; [`Trader.AuditReport`](../../src/Trader.AuditReport) builds the Audit Bot's reports; [`Trader.SampleBot`](../../src/Trader.SampleBot) is the template a new bot starts from.

## Adding a bot

1. Copy [`_template.md`](_template.md) to `docs/bots/<bot>.md`; agree its goal and rules there (version v1.0). When a bot gets more than one document (a proposal, images), they go together in `docs/bots/<bot>/` with a short `README.md`, like [Scout](scout/README.md).
2. Add a row to the table above, to the [projects table on the front page](../../README.md#projects) and to the [documents map](../README.md).
3. When the code starts: `src/Trader.<Bot>/` from a copy of `Trader.SampleBot` ([Open Code Pieces §5](../open-code-pieces.md#5-adding-a-bot)), with a short `README.md` pointing to the bot's document.
4. Every change of decision, scope or design: bump the bot document's version, add a history row, commit as `<bot> vX.Y: <what changed>`.
