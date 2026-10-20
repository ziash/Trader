# <Bot name>

<One or two sentences: what the bot is for. A project of its own; it uses the [Open Code Pieces](../open-code-pieces.md).>

| Item | Value |
|---|---|
| Status | Concept / Specified / Building (step n) / Working on demo / Running / Finished — with dates |
| Document version | v1.0 (dd-Mon-yyyy) — history in the last section |
| Code | `src/Trader.<Bot>` |
| Tests | `tests/Trader.<Bot>.Tests` |
| Accounts | Which accounts — demo only until a go-live is decided ([shared §4](../shared.md#4-safety-rules-for-every-bot)) |
| Symbols | Which symbols, where configured |
| Bot name / labels | `<BotName>` — label format |
| Config | Files in `config/` (live copies in `~/tradebot/config/`) |
| Service | systemd unit name, if any |

## 1. Goal

## 2. Rules

<What the bot does, precisely enough to build and test it. Number the subsections (2.1, 2.2 …) so code comments can point at them, e.g. `(<bot>.md §2.1)`.>

## 3. Logs and outputs

## 4. Run and operate

```bash
# start / status / logs / stop / deploy
```

## 5. Build steps

| Step | Content | Status |
|---|---|---|

## 6. Decisions

| # | Decision | Why |
|---|---|---|

## 7. Open items

## 8. History

| Version | Date | Change |
|---|---|---|
| v1.0 | dd Mon yyyy | First issue |
