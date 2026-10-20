# Audit Bot

Measures execution quality (spreads, slippage, fill speed, reliability) at two brokers on demo accounts, so the live broker can be chosen on data. It places only tiny probe orders and never trades.

| Item | Value |
|---|---|
| Status | **Running** — Phase 2 audit run since 18-Oct-2026 (to about mid-November) |
| Spec (authoritative) | [plan Section 6](../plan.md#6-first-bot-the-audit-bot): 6.2 what it measures, 6.2.2 probes, 6.2.3 log intervals, 6.2.4 alerts, 6.3 logs on disk, 6.4 instances, 6.5 report |
| Code | [`src/Trader.AuditBot`](../../src/Trader.AuditBot) (bot), [`src/Trader.AuditReport`](../../src/Trader.AuditReport) (daily report), shared framework [`src/Trader.Core`](../../src/Trader.Core) |
| Tests | [`tests/Trader.AuditBot.Tests`](../../tests/Trader.AuditBot.Tests), [`tests/Trader.AuditReport.Tests`](../../tests/Trader.AuditReport.Tests) |
| Accounts | IC Markets demo 49123893 (`brokerA`), Pepperstone demo 49124666 (`brokerB`) — demo only |
| Symbols | AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD |
| Bot name / labels | `AuditBot` — orders labelled `AuditBot-<6hex>` (instance ID) |
| Config | [`config/audit.brokerA.json`](../../config/audit.brokerA.json), [`audit.brokerB.json`](../../config/audit.brokerB.json), [`news-releases.json`](../../config/news-releases.json) — live copies in `~/tradebot/config/` |
| Services | `trader-auditbot` (the bot), `trader-auditreport.timer` (report, daily 00:15 UTC) — [`deploy/`](../../deploy) |

## What it does

- **Spreads:** reads every bid/ask tick and writes one summary row per symbol every **2 min** in main hours (London open to New York close) and every **5 min** off-peak: min / average / median / p95 / max spread in pips and USD per lot, tick count, longest gap.
- **Probes:** at fixed windows — **QuietHour** 03:00 London, **LondonOpen** 08:00 London, **NewYorkOpen** 08:00 New York, and 2 min before / 5 s after / 5 min after each **NFP and CPI** release — one 0.01-lot buy and one sell per symbol, opened and closed at once. Each open and each close is a row: requested vs filled price, slippage, latency, commission. Every probe has a wide backup stop-loss on the server.
- **Alerts** (warnings in the journal and `events.csv`): `SpreadWide` / `SpreadNormal` when a row's average spread crosses its limit (forex 2 pips; crypto $50/lot, ETHUSD $10/lot), `SlippageHigh` for a fill worse than its limit (forex 1 pip; crypto $10/lot, ETHUSD $3/lot). Limits per broker in the config.
- **Several copies:** each running copy registers itself; per account one is **Primary** (sends probes) and the others **Passive** (record spreads only). A Passive takes over at once if the Primary stops.
- **Daily report:** `Trader.AuditReport` turns the logs into `~/tradebot/reports/daily-compare-<date>.csv` — both brokers side by side per symbol and period — and, with `--from/--to`, the final summary.
- **Safety:** touches only positions with its own label, always by `positionId`; the owner's trades are never touched (plan 5.3).

## Run and operate

```bash
systemctl --user status trader-auditbot             # running?
journalctl --user -u trader-auditbot -f             # live output
journalctl --user -u trader-auditbot -p warning     # warnings and alerts only
systemctl --user stop trader-auditbot               # stop (owner's OK needed during the audit run)
systemctl --user restart trader-auditbot            # switch to a newly deployed build
scripts/deploy-auditbot.sh                          # publish a new build (does not restart)
```

Report:

```bash
systemctl --user list-timers trader-auditreport     # next run
systemctl --user start trader-auditreport           # make any missing daily reports now
dotnet ~/tradebot/bin/AuditReport/Trader.AuditReport.dll --from 2026-10-18 --to 2026-11-01   # summary
scripts/deploy-auditreport.sh                       # publish a new report build
```

During development: `dotnet run --project src/Trader.AuditBot -- --help` (`--test-probe-now` runs one probe window at once).

## Outputs

```
~/tradebot/logs/<broker>/<ctidTraderAccountId>/<instanceId>/<yyyy-MM-dd>/
  spreads.csv   one row per symbol per interval
  probes.csv    one row per probe order (open and close)
  events.csv    connections, roles, probe summaries, alerts, errors
~/tradebot/reports/
  daily-compare-<yyyy-MM-dd>.csv
  summary-<from>_<to>.csv
```

All CSV, UTC times, open directly in Google Sheets (plan 6.3.1). Every restart starts a new instance folder; the report merges them. Other bots (TM) log into the same broker/account folders under their own names (`TM-…`); the report reads only `AuditBot-…` folders.

## History

| Plan | Date | Change |
|---|---|---|
| v1.8–v1.13 | 17–18 Oct 2026 | Phase 1 steps 1–5: connect, stay connected, price feed, instance registry, probe orders |
| v1.14 | 18 Oct 2026 | Send time in the order comment; systemd service; audit run started |
| v1.15 | 19 Oct 2026 | Spread and slippage alerts; probe summary per asset class |
| v1.16 | 19 Oct 2026 | Daily report tool and timer — Phase 1 complete |
| v1.22 | 20 Oct 2026 | Report reads only Audit Bot folders (TM's test folders had been counted); 19-Oct report rebuilt |

Full detail: [plan change log](../plan.md#11-change-log).
