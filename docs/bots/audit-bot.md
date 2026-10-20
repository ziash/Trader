# Audit Bot

Measures execution quality — spreads, slippage, fill speed, reliability — at two brokers on demo accounts, so the live broker can be chosen on data instead of marketing claims. It places only tiny probe orders and never trades. A project of its own; it is also where the [Open Code Pieces](../open-code-pieces.md) were first built.

| Item | Value |
|---|---|
| Status | **Built and finished; running** — the audit run (2 brokers × 2 weeks) started 18-Oct-2026 and runs to about mid-November (§7) |
| Document version | v1.0 (20-Oct-2026) — history in §10 |
| Code | [`src/Trader.AuditBot`](../../src/Trader.AuditBot) (bot), [`src/Trader.AuditReport`](../../src/Trader.AuditReport) (daily report), on the [Open Code Pieces](../open-code-pieces.md) |
| Tests | [`tests/Trader.AuditBot.Tests`](../../tests/Trader.AuditBot.Tests), [`tests/Trader.AuditReport.Tests`](../../tests/Trader.AuditReport.Tests) |
| Accounts | IC Markets demo 49123893 (`brokerA`), Pepperstone demo 49124666 (`brokerB`) — demo only ([shared](../shared.md#2-brokers-and-accounts)) |
| Symbols | AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD |
| Bot name / labels | `AuditBot` — orders labelled `AuditBot-<6hex>` (instance ID) |
| Config | [`config/audit.brokerA.json`](../../config/audit.brokerA.json), [`audit.brokerB.json`](../../config/audit.brokerB.json), [`news-releases.json`](../../config/news-releases.json) — live copies in `~/tradebot/config/` |
| Services | `trader-auditbot` (the bot), `trader-auditreport.timer` (report, daily 00:15 UTC) — [`deploy/`](../../deploy) |

## 1. Goal

Run the same measurements on two brokers in parallel for at least 2 weeks on demo accounts, then compare execution quality with data instead of marketing claims.

## 2. What it measures

| Measurement | How | Why it matters |
|---|---|---|
| Spread | Read every bid/ask tick per symbol; write one summary row per interval (§2.3) | Real cost; widening at news/rollover |
| Slippage | Small scheduled probe orders (0.01 lot): requested vs filled price | Shows fill fairness, positive vs negative slippage |
| Execution latency | Time from order send → fill event | Speed of the broker path |
| Rejections / requotes | Count and reason codes | Hidden friction |
| Swap & commission | Read from deal/position data | Cost of holding trend trades |
| Feed gaps | Longest gap between ticks, per interval row | Feed stability |

### 2.1 Symbols

**AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD** — same names at both brokers, all enabled for trading on both demo accounts (checked 17-Oct-2026 through the API). Replaces the v1.0 draft list (EURUSD, GBPUSD, USDJPY, XAUUSD, US500, BTCUSD).

- Crypto trades 24/7, so BTCUSD and ETHUSD also show weekend spread and feed behaviour while forex is closed.
- USDJPY and GBPUSD are subscribed **for currency conversion only** (JPY and GBP spread costs → USD); they are not logged or probed.
- Crypto check, 17-Oct-2026: IC Markets lists 105 crypto symbols, Pepperstone 53; 25 are enabled at both (22 under the same name; Avalanche, Chainlink and Sonic under different names). The bot looks symbols up by name per broker, with a per-broker name map for any that differ.

### 2.2 Probe windows and probes

Probe orders (0.01 lot) open and close immediately. Each probe row in `probes.csv` carries its window name. Times are local market times; the bot converts them to UTC with time-zone rules, so daylight-saving changes are automatic (UK clocks change 25-Oct-2026, US clocks 1-Nov-2026 — for that week London and New York are 4 h apart, not 5 h).

| Window (log name) | Local time | Days |
|---|---|---|
| `LondonOpen` | 08:00 London | Mon–Fri |
| `NewYorkOpen` | 08:00 New York | Mon–Fri |
| `QuietHour` | 03:00 London (Asian session; away from the 17:00 New York rollover) | Mon–Fri |
| `NewsRelease-2m`, `NewsRelease+5s`, `NewsRelease+5m` | 2 min before, 5 s after and 5 min after 08:30 New York, on US **Non-Farm Payrolls** and **CPI** release days | Dates in `~/tradebot/config/news-releases.json` (from the BLS schedules; NFP 6-Nov, 4-Dec, 8-Jan, 5-Feb; CPI 10-Nov, 10-Dec, 13-Jan, 11-Feb) |

Per window, each symbol gets one buy probe and one sell probe, a few seconds apart; instances on the same account are staggered (§4). For `NewsRelease`, probes run just before, just after and about 5 minutes after the release .

*(built in step 5)* How a probe runs:
- **Who:** only the instance that is **Primary** on the account (§4). Accounts run in parallel; within an account one probe at a time, symbols in config order, buy then sell, 2 s apart. A symbol whose market is closed is skipped. A window more than 60 s late (computer asleep, bot busy) is skipped and logged.
- **Order:** market order of **0.01 lot** (forex 1,000 units; crypto 0.01 coin), labelled with the instance ID, with a wide server-side **backup stop-loss**: 50 pips for forex, 2 % of the price for crypto. Then closed at once **by its `positionId`** (ownership rule, [Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)). Both the open and the close are written to `probes.csv`.
- **Measured:** requested price = ask for a buy, bid for a sell, at the moment of sending; fill price from the fill message; latency = request sent → fill message received on this machine; commission from the fill.
- **Left-overs:** before every window, after every connect and on becoming Primary, the bot asks the server for all open positions (reconcile) and closes any with an `AuditBot-…` label. Tested 18-Oct-2026 by killing the bot (`kill -9`) while a probe was open: the next start closed it. Positions without the label are never touched; their number is logged (`ForeignPositions`), and fills of foreign orders are logged once (`ForeignTrade`).
- **Close failures:** retried up to 3 times; if the position is still open the bot logs `ProbeCloseFailed`, the backup stop-loss stays on the server, and the clean-up retries before the next window.
- **Test option:** `--test-probe-now` runs one window named `Test` at start-up.
- **First results (18-Oct-2026, Sunday, crypto only):** all orders filled, no rejections; latency ~470 ms (IC Markets) and ~580 ms (Pepperstone); commission 0 on crypto at both.

### 2.3 Log intervals

| Period | Definition | Spread row every |
|---|---|---|
| Main hours | Mon–Fri, 08:00 London to 17:00 New York (London open to New York close) | **2 min** |
| Off-peak | All other times, including weekends (crypto only — forex is closed from Fri 17:00 to Sun 17:00 New York) | **5 min** |

Each row summarises all ticks in its interval (min / average / median / 95th-percentile / max spread, tick count, longest gap), so short spikes and gaps between rows are not lost. Ticks are placed in intervals by the **server's timestamp**. A row is skipped only when there were no ticks **and** the market was closed during the interval, per the broker's own trading schedule for the symbol (weekend, and the daily break around 17:00 New York: ~2 min for forex, up to 6 min for crypto). No ticks while the market is open gives a row with `ticks` = 0 — a feed gap (including time the bot itself was disconnected; `events.csv` shows which). The last price the server re-sends on every subscribe carries its old timestamp and is ignored. Events (connect, reconnect, errors, rejections) are written when they happen, not on a timer. The 10-second Open API heartbeat (a Spotware protocol requirement) and the 10-second registry status file ([Open Code Pieces §6](../open-code-pieces.md#6-instance-registry-one-primary-per-bot-per-account), overwritten in place) are not logs and stay at 10 s.

### 2.4 Alerts and probe summary

Wide spreads and bad fills are **reported as warnings** (journal and `events.csv`), not only kept in the CSV rows. Limits are per broker in `audit.<broker>.json` (`"alerts"`, optional; defaults below). Forex in **pips**; crypto in **USD per lot**, because a crypto "pip" differs by broker (IC BTCUSD pip = $0.01, Pepperstone $0.10) and cannot be compared or averaged across symbols.

| Alert | Event | Fires when | Default limit |
|---|---|---|---|
| Spread wide | `SpreadWide` (warning), then `SpreadNormal` (info) | A `spreads.csv` row's **average** spread is above the limit. Raised once; cleared once when back under the limit, with duration and peak (like an alarm raise/clear) | Forex 2 pips; crypto $50/lot; ETHUSD $10/lot |
| Slippage high | `SlippageHigh` (warning) | One probe fill (open or close) **worse** than requested by more than the limit. Better-than-requested fills are not alerted | Forex 1 pip; crypto $10/lot; ETHUSD $3/lot |

Defaults chosen from the first day of data (19-Oct-2026): forex averages under 1 pip on both brokers, BTCUSD $5–15, ETHUSD $2–3 per lot. Spreads above 2 pips were seen only around the Sunday reopen; the daily break around 17:00 New York will also raise `SpreadWide` — this is real and is reported.

**Probe window summary** (`ProbeWindowDone`, `ProbeSlippage`): fills, latency average and maximum; then one line per asset class with average slippage, the **worst fill by name**, and how many fills were over the limit, e.g. `forex: slippage avg +0.06 pips, worst -0.30 pips (AUDJPY close buy); 0 of 16 fills over the alert limit`. A single average over all symbols is not used: it mixed units and let good and bad fills cancel out.

## 3. Logs on disk

Each broker gets its own folder; each running instance gets its own sub-folder (the full `~/tradebot/` layout is in [shared §5](../shared.md#5-folders-and-files)).

```
~/tradebot/logs/<broker>/<ctidTraderAccountId>/AuditBot-<6hex>/<yyyy-MM-dd>/
  spreads.csv   one summary row per symbol per interval (§2.3)
  probes.csv    one row per probe order (open and close each a row)
  events.csv    connect, reconnect, roles, probe summaries, alerts, errors
~/tradebot/reports/
  daily-compare-<yyyy-MM-dd>.csv      one UTC day, both brokers (§5); made daily at 00:15 UTC
  summary-<from>_<to>.csv             a range of days (the final summary)
```

Other bots (TM) log into the same broker/account folders under their own names (`TM-…`); the report reads only `AuditBot-…` folders.

### 3.1 File columns

All files follow the shared CSV format ([shared §5](../shared.md#5-folders-and-files)).

**Pips and USD per lot.** Spreads and slippage are given in pips and as USD cost for one lot:

- Forex: 1 pip = 0.0001 (0.01 for JPY pairs); 1 lot = 100,000 units of the base currency. USD value uses the live USDJPY or GBPUSD rate from the same broker (e.g. EURGBP: spread × 100,000 × GBPUSD).
- Crypto: the pip is the broker's own pip size for the symbol, which may differ between brokers — so compare the USD columns, not the pip columns. A 100,000-unit lot does not apply (that would be 100,000 BTC); USD is given per 1 lot = 1 coin. *(confirmed from the API 18-Oct-2026: both brokers' lot = 1 coin; BTCUSD/ETHUSD pip = 0.01 at IC Markets, 0.1 at Pepperstone.)*
- Lot size is read from each symbol's details (`lotSize`); forex = 100,000 units at both brokers. The conversion symbols (USDJPY, GBPUSD) are found automatically from the broker's symbol list. USD figures use the conversion rate at the end of the interval.
- Median = middle value; 95th percentile = nearest-rank (the smallest spread with at least 95 % of ticks at or below it).

**`spreads.csv`**

| Column | Meaning |
|---|---|
| `time_utc` | End of the interval |
| `interval_s` | 120 (main hours) or 300 (off-peak) |
| `session` | `main` or `offpeak` |
| `symbol` | e.g. `EURUSD` |
| `ticks` | Number of bid/ask updates in the interval |
| `bid`, `ask` | Last prices in the interval |
| `spread_min_pips`, `spread_avg_pips`, `spread_median_pips`, `spread_p95_pips`, `spread_max_pips` | Spread statistics over the interval |
| `spread_avg_usd`, `spread_max_usd` | Average and maximum spread as USD cost per lot |
| `max_gap_s` | Longest time between two ticks in the interval |

**`probes.csv`**

| Column | Meaning |
|---|---|
| `time_utc` | When the order was sent |
| `window` | `LondonOpen`, `NewYorkOpen`, `QuietHour`, `NewsRelease-2m` / `+5s` / `+5m`, or `Test` |
| `action` | `open` (the probe) or `close` (closing it — also an order with its own slippage and latency) |
| `symbol`, `side`, `lots` | e.g. `EURUSD`, `buy`, `0.01` |
| `req_px`, `fill_px` | Price seen when sending; price filled |
| `slip_pips`, `slip_usd` | Slippage; negative = worse for us. USD per lot |
| `latency_ms` | Order send → fill event |
| `commission_usd` | Commission charged for this order (positive = paid), from the fill |
| `result`, `reason` | `filled` / `rejected` / `error` and the broker's reason code (`TIMEOUT`, `DISCONNECTED` for `error`) |
| `position_id` | The position the order opened or closed |
| `label` | Order label (instance ID) |

**`events.csv`:** `time_utc`, `level`, `event`, `detail`.

## 4. Several copies running

The [instance registry](../open-code-pieces.md#6-instance-registry-one-primary-per-bot-per-account) decides, per account, which copy is **Primary**. For the Audit Bot:

- Only the **Primary** sends probe orders; **Passive** copies still record spreads in their own instance folder.
- Copies on the same account get a **slot** (oldest first), and their probe windows are staggered by 5 s per slot so they never fire together.
- If the Primary stops, a Passive copy takes over at once (tested 18-Oct-2026 with `kill -9`: takeover within 1 s).

## 5. Daily report

- Daily comparison CSV per broker, built from `spreads.csv` and `probes.csv` in the format of §3.1: median/95th-percentile spread (pips and USD per lot), average slippage, latency, rejection count — per symbol and per probe window.
- Final 2-week summary → broker choice recorded in this document (§9).

### 5.1 The report tool

`src/Trader.AuditReport` — a separate program that **only reads** the CSV logs (never connects to cTrader), so it cannot disturb the running bot. Both brokers in one file:

| Run | Output |
|---|---|
| Daily, automatically (systemd timer, 00:15 UTC; `--missing`) | `reports/daily-compare-<date>.csv` for every complete UTC day that has none yet — also catches up after the laptop was off |
| `--date 2026-10-19` | That day, rebuilt |
| `--from 2026-10-18 --to 2026-11-01` | `reports/summary-<from>_<to>.csv` — same columns over the whole range (the final summary) |

- **Merging:** a day may be spread over several instance folders (every restart starts a new one). Only the Audit Bot's own folders (`AuditBot-…`) are read; other bots (TM) log into the same broker/account folders and are ignored. They are merged; if two instances logged the same interval (Primary and Passive), it is counted once (the row with more ticks).
- **Rows:** one per broker × symbol × period, sorted by symbol, then period, then broker, so IC Markets and Pepperstone are on adjacent lines. Periods: `day` (all), `main` and `offpeak` (spreads, §2.3), and each probe window (`QuietHour`, `LondonOpen`, …; probes only). Then one `ALL` row per broker with order totals, latency and reliability.
- **Columns:**

| Group | Columns |
|---|---|
| Identity | `date_from`, `date_to`, `broker`, `broker_name`, `symbol`, `period` |
| Coverage | `spread_rows`, `logged_min` (minutes covered by rows), `ticks`, `gap_rows` (rows with no ticks while open) |
| Spread | `spread_median_pips`, `spread_p95_pips`, `spread_max_pips`, and the same in `_usd` (per lot) |
| Probes | `orders`, `filled`, `rejected`, `order_errors`; `slip_avg_pips`, `slip_worst_pips`, `slip_avg_usd`, `slip_worst_usd` (negative = worse for us); `latency_avg_ms`, `latency_median_ms`, `latency_max_ms`; `commission_usd` |
| Reliability (`ALL` row) | `instances`, `connection_drops`, `spread_alerts`, `slippage_alerts`, `missed_windows`, `log_warnings`, `log_errors` |

- **Spread figures** come from the interval rows: median and p95 of the rows' average spread, each row weighted by its length (half / 95 % of the time the spread averaged at or below it); max = largest single spread seen. **Crypto: compare by the USD columns** — pips differ by broker (§2.4). The `ALL` row leaves slippage out, because averaging it over forex and crypto would mix units.
- **Connection drops** are counted per broker, but both brokers share one connection, so a drop shows on both.

## 6. Run and operate

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

**Services** (installed by the deploy scripts; see [shared §3](../shared.md#3-machine-and-services) for the laptop settings):

- `trader-auditbot` — a systemd user service (`deploy/trader-auditbot.service`) running the published Release build in `~/tradebot/bin/AuditBot/`, so code changes never affect the running bot; `scripts/deploy-auditbot.sh` publishes and installs (it does not restart). Restarts 30 s after a crash; does not restart after a fatal problem the bot reports itself (exit 1/2 — needs a human). Console output in the journal.
- `trader-auditreport.timer` — daily at 00:15 UTC (catches up after the laptop was off), starting `trader-auditreport.service` with the published build in `~/tradebot/bin/AuditReport/`; `scripts/deploy-auditreport.sh` publishes, installs and enables it.
- **During the audit run, the bot is not stopped or restarted, and nothing in `~/tradebot/logs` is deleted, without the owner's OK.**

## 7. Build steps and the audit run

| Step | Name | Content | Status |
|---|---|---|---|
| 1 | Connect and list accounts | TLS connect to the demo host, app auth, account list, account auth, trader info (`src/Trader.ListAccounts`) | **Done 16-Oct-2026** — both demo accounts Hedged; `ctidTraderAccountId` 49123893 (IC Markets), 49124666 (Pepperstone) |
| 2 | Stay connected | Event stream, heartbeat watch, auto-reconnect with re-auth, access-token refresh with atomic save of the new token pair (`src/Trader.AuditBot`, `ConnectionManager`) | **Done 17-Oct-2026** — reconnects tested with simulated drops (back up within ~3 s); first token refresh done |
| 3 | Price feed to disk | Config files `audit.brokerX.json`; symbol lookup and name map; spot subscription; `spreads.csv` summaries (§2.3, §3.1); feed gaps; M5 trendbar subscription; `events.csv` | **Done 18-Oct-2026** — live on both demos (Sunday: crypto rows every 5 min, forex skipped while closed; resubscribe after forced drops) |
| 4 | Instance registry | §4 (lock-file Primary, per-account roles, slots) | **Done 18-Oct-2026** — 3 copies live; Primary killed with `kill -9`, takeover within 1 s |
| 5 | Probe orders | Windows of §2.2; `probes.csv`; ownership rule ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)) | **Done 18-Oct-2026** — probes on both demos (crypto, Sunday); left-over clean-up tested with `kill -9`; manual (unlabelled) position left untouched |
| 6 | Daily report | §5 | **Done 19-Oct-2026** — `src/Trader.AuditReport` (§5.1), daily timer; report for 18-Oct built from the live logs |

*Between steps 2 and 3 (17-Oct-2026, v1.10):* framework made reusable for many bots — `BotRunner`, per-bot accounts, instance ID and order labels, usage guide, `src/Trader.SampleBot` ([Open Code Pieces §4](../open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)).

**The audit run:** 2 brokers × 2 weeks on demo; daily reports; choose the broker. Started 18-Oct-2026 (before step 6, by the owner's decision; test logs from the build were deleted first, so everything in `~/tradebot/logs` from 18-Oct-2026 ~17:10 UTC is real audit data). Running to mid-November covers the UK and US clock changes (25-Oct, 1-Nov) and the NFP (6-Nov) and CPI (10-Nov) releases. First full day (19-Oct-2026): all 3 windows × 24 orders filled on both brokers; one connection drop (44 s, reconnected automatically).

## 8. Decisions

| # | Decision | Why |
|---|---|---|
| 1 | Audit before trading: the first bot measures two brokers on demo before any strategy runs | Prove execution quality with evidence before money is committed |
| 2 | Brokers: IC Markets (Broker A) and Pepperstone (Broker B), both raw spread + commission ([shared §2](../shared.md#2-brokers-and-accounts)) | Raw spreads, cTrader, crypto CFDs, regulated groups |
| 3 | Logs are CSV files that open directly in Google Sheets / Excel; spreads as one summary row per symbol every 2 min (main hours) or 5 min (off-peak), in pips and USD per lot | Easy to export and analyse; small files without losing spikes or feed gaps |
| 4 | Probe windows: London open, New York open, quiet hour 03:00 London, NFP/CPI (2 min before, 5 s after, 5 min after) | Typical, busy, quiet and news conditions |
| 5 | Lock-file Primary instead of promotion after a heartbeat timeout | Instant takeover; never two Primaries on one machine |
| 6 | Send time in every order's comment | Server open time can be compared with the send time |
| 7 | Alerts: `SpreadWide`/`SpreadNormal` and `SlippageHigh`, with limits per broker; probe summary per asset class with the worst fill named | Wide spreads and bad fills are reported, not averaged away |
| 8 | A separate read-only report tool on a daily timer; one row per broker × symbol × period | Cannot disturb the running bot; both brokers side by side in Google Sheets |

## 9. Open items

- **Choose the broker** from the final summary (`--from 2026-10-18 --to <end>`) at the end of the audit run, and record it here. Weigh in the regulation note in [shared §8](../shared.md#8-open-items).
- Monitoring while the bot runs: a status script with a systemd timer and desktop alerts (Claude only on alerts) — deferred by the owner on 18-Oct-2026. The bot's `SpreadWide` / `SlippageHigh` warnings (§2.4) are what such a script would look for; the shared notifications could now carry them as pop-ups.
- Add NFP/CPI dates after 11-Feb-2027 to `news-releases.json` (BLS publishes them yearly) if the Audit Bot is still running then.

## 10. History

| Version | Date | Change |
|---|---|---|
| — | 17–18 Oct 2026 | (old plan v1.8–v1.13) Steps 1–5: connect, stay connected, price feed to disk, instance registry, probe orders |
| — | 18 Oct 2026 | (old plan v1.14) Send time in the order comment; systemd service; audit run started |
| — | 19 Oct 2026 | (old plan v1.15–v1.17) Spread and slippage alerts; daily report tool and timer — built and finished; Pepperstone demo confirmed Razor |
| — | 20 Oct 2026 | (old plan v1.22) Report reads only Audit Bot folders; 19-Oct report rebuilt |
| v1.0 | 20 Oct 2026 | Own project document, split out of the old plan ([archive](../archive/plan-v1.22.md), Sections 6, 7, 8.1) and the former Audit Bot page. Content unchanged |
