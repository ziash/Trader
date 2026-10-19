# Trader Project — Semi-Automated Trading Bot: Development Plan

| Item | Detail |
|---|---|
| Owner | Zeeshan |
| Document version | **v1.19** (living document — every change is recorded in [Section 11](#11-change-log) and in Git history) |
| Date | 17 October 2026 |
| Status | Plan agreed in principle; **Phase 0 complete (16-Oct-2026)** — `check-env.sh`: no failures, 0 warnings; Open API app approved and tokens issued for both demo accounts; IC Markets (Broker A) and Pepperstone (Broker B) demos Hedging; setup record in [docs/setup.md](setup.md). **TM (Trade Manually) specified as the first trading bot (v1.18, [Section 10.9](#109-tm--trade-manually-bot-v118))**; **Phase 1 complete (v1.16, 19-Oct-2026):** step 1 (connect and list accounts) done 16-Oct-2026; step 2 (stay connected) done 17-Oct-2026; framework made reusable for many bots ([Section 5.3](#53-one-framework-many-bots-v110)) 17-Oct-2026; steps 3 (price feed to disk), 4 (instance registry) and 5 (probe orders) done 18-Oct-2026; step 6 (daily report) done 19-Oct-2026; **Phase 2 audit run started 18-Oct-2026 before step 6** (owner's decision; the daily report is built later from the collected CSVs); order ownership rule added ([Section 5.3](#53-one-framework-many-bots-v110)); steps in [Section 8.1](#81-phase-1-steps-v18). Audit Bot symbols, probe windows and log format fixed ([Section 6](#6-first-bot-the-audit-bot)). Exit logic: Scenario 1 defined; position structure, scale-out and pyramiding defined ([Section 10.8](#108-position-structure-scale-out-and-pyramiding)). Scenarios 2–5 pending. |

## Contents

1. [Purpose in one paragraph](#1-purpose-in-one-paragraph)
2. [Key decisions at a glance](#2-key-decisions-at-a-glance)
3. [How the pieces fit](#3-how-the-pieces-fit)
4. [Human interaction via TradingView](#4-human-interaction-via-tradingview)
5. [Bot via cTrader Open API](#5-bot-via-ctrader-open-api)
6. [First bot: the Audit Bot](#6-first-bot-the-audit-bot)
7. [Execution environment: Ubuntu](#7-execution-environment-ubuntu)
8. [Phased roadmap](#8-phased-roadmap)
9. [Risk guardrails](#9-risk-guardrails-apply-from-phase-4)
10. [Exit logic: hedge-and-recover](#10-exit-logic-hedge-and-recover-no-stop-loss-for-risk) — including [10.9 TM, the first trading bot](#109-tm--trade-manually-bot-v118)
11. [Change log](#11-change-log)
12. [Open items / decisions pending](#12-open-items--decisions-pending)
13. [Glossary](#13-glossary)

---

## 1. Purpose in one paragraph

Resume short-to-mid-term trend trading with a semi-automated setup. The human reads the market and makes decisions on TradingView; a bot written in C# executes and manages trades through the cTrader Open API on an Ubuntu machine, using raw-spread (ECN/STP-style) broker accounts. The first bot is an Audit Bot that measures, with evidence, how fairly two brokers price and fill orders before any real money is committed.

## 2. Key decisions at a glance

| # | Area | Decision | Why |
|---|---|---|---|
| 1 | Human interaction | TradingView (browser) | Best charts; works on Ubuntu; no install |
| 2 | Bot ↔ broker | cTrader Open API | Native on Linux; direct to broker; no Windows needed |
| 3 | Bot language | C# on **.NET 10 LTS** *(changed v1.4; was .NET 8)* | Best-documented cTrader route; runs natively on Linux. .NET 8 and 9 reach end of support on 10 Nov 2026; .NET 10 is supported to Nov 2028 and is in Ubuntu's own archive |
| 4 | First bot | Audit Bot on 2 brokers | Prove execution quality before trading strategy |
| 5 | Execution environment | Ubuntu (home PC → Linux VPS later) | Existing machine; same code moves to VPS |
| 6 | Plan document | Markdown in the private GitHub repo (`docs/plan.md`); Word/PDF exported when needed | Single source of truth next to the code; full change history *(changed v1.3; was .docx in Google Drive)* |
| 7 | Change control | All future changes go into this document | Version table + change log (Section 11) + Git commits |
| 8 | Broker candidates | Two of: Pepperstone, IC Markets, FP Markets, Fusion. **Broker A = IC Markets** (cTrader demo created 11-Oct-2026, Hedging, Raw Spread) *(v1.4, v1.5)*; **Broker B = Pepperstone** (cTrader demo created 11-Oct-2026, Hedging) *(v1.6)* | Raw spreads, cTrader, crypto CFDs, regulated groups |
| 9 | Trading style | Naked price action; real trades are shorts *(v1.18: TM handles longs and shorts, 10.9)* | Trader's own method (ex-Chris Capre student) |
| 10 | Loss control | Opposite-side hedge order instead of a stop-loss (Section 10) | Trader's design; specified scenario by scenario |
| 11 | Position structure | Every trade = 2 half-size shorts + 2 half-size hedges, with numbered legs (Section 10.8) *(new v1.3)* | Clean scale-out; each pair has its own exit |
| 12 | Trade management | Runner leg trails on M5; rotating leg closes when the next pyramid add opens; unlimited adds within an exposure cap *(new v1.3)* | Ride the trend while bounding total exposure |
| 13 | Audit Bot logs | CSV files that open directly in Google Sheets / Excel; spreads written as one summary row per symbol every 2 min (main hours) or 5 min (off-peak), in pips and USD per lot (Section 6.3) *(new v1.8)* | Easy to export and analyse elsewhere; small files without losing spikes or feed gaps |
| 14 | Bot framework | All bots share one library, `src/Trader.Core` (connection, tokens, logging, start-up); each bot is its own small project referencing it. No copied connection code (Section 5.3) *(new v1.10)* | A fix reaches every bot at once; all bots must refresh tokens the same way because they share one secrets file |
| 15 | First trading bot *(new v1.18)* | **TM (Trade Manually):** the owner opens every position by hand in cTrader; TM mirrors it and hedges both legs, manages the hedges, protects at 1R, and cleans up when the owner closes. Longs and shorts. Reusable notifications module in `Trader.Core` (Section 10.9) | The owner keeps every trading decision; the bot does the hedge bookkeeping that is impossible by hand across many positions |

## 3. How the pieces fit

```
YOU ──► TradingView (browser) : read trend, decide, (later) send alerts
              │ optional webhook (Phase 4)
              ▼
        Ubuntu machine
┌───────────────────────────────────────────────┐
│ Bot engine (C# / .NET worker service)         │
│  • own price & candle feed from broker        │
│  • rules, risk sizing, hedge logic, exits     │
│  • per-broker / per-instance logs on disk     │
└───────────────┬───────────────────────────────┘
                │ cTrader Open API (TLS, Protobuf)
                ▼
   Broker A (cTrader)        Broker B (cTrader)
```

> **Important:** the bot never reads TradingView charts. It gets its own prices from the broker — the same prices it trades on. TradingView is the human's window, and later an optional signal source.

## 4. Human interaction via TradingView

### 4.1 What the human does

- Reads the higher-timeframe trend (D1 / H4) and picks candidate markets.
- Marks levels, decides direction and when to engage (opens Trade 1 of each sequence).
- Monitors open positions (manually, or via TradingView-broker chart integration where the broker supports it).

### 4.2 How a human decision reaches the bot (staged)

| Stage | Method | Notes |
|---|---|---|
| Phase 1–3 | None needed | Audit Bot and backtests run on their own |
| Phase 4a | Bot config file / small local dashboard | Human sets symbol, direction, risk %; bot executes. *(v1.18)* **First: TM** — the human opens each position in the cTrader app; TM mirrors and hedges it (Section 10.9). |
| Phase 4b | TradingView alert → webhook → bot | Needs a paid TradingView plan with webhooks and a secure public HTTPS endpoint (e.g. Cloudflare Tunnel). Bot validates a shared secret before acting. |

> **Guardrail:** a TradingView alert can only request an action. The bot re-checks risk limits, spread and account state before placing any order.

> **v1.3 note:** after the human opens Trade 1, the bot itself opens pyramid adds (Section 10.8, rule R4). This moves trade management from semi-automated toward automated; every add still passes the same risk checks.

## 5. Bot via cTrader Open API

### 5.1 Building blocks

- One Open API application registered on Spotware's Open API portal (client ID + secret). *(v1.7: app "Trend Audit Bot", ID 43770, **approved**; access granted with scope `trading` to the two demo accounts only — IC Markets 10124393 and Pepperstone 5357135. Live accounts deliberately not granted; they get a fresh grant in Phase 5.)*
- One cTrader ID (cTID) linked to both broker demo accounts; OAuth grants the app access to each account.
- .NET worker service using Spotware's official OpenAPI.Net library (NuGet `cTrader.OpenAPI.Net`) for the TCP/Protobuf connection.
- Demo and live use separate API hosts: `demo.ctraderapi.com:5035` and `live.ctraderapi.com:5035` (Protobuf over TLS; confirmed v1.4). One connection serves all demo accounts; a second serves all live accounts.
- Access token lasts about 30 days; the bot renews it with the refresh token and must save the new refresh token each time (the old one is invalidated).
- *(v1.9, step 2)* Token refresh runs over the same Protobuf connection (`ProtoOARefreshTokenReq`), when the token has under 7 days left, when its expiry is unknown, or when the server rejects it. The new pair and its expiry (`CTRADER_ACCESS_TOKEN_EXPIRES_UTC`) are written to `secrets.env` atomically (temp file, then rename; mode 600), under a lock file (`secrets.env.lock`) so only one instance refreshes at a time. First refresh 17-Oct-2026: the server returned a lifetime of about 30 days 10 h (expires 16-Nov-2026).
- *(v1.9, step 2)* Heartbeats: the bot sends one at least every 10 s when idle; the server sends its own only after about 30 s without traffic. Dead-line rule: after 40 s with nothing received the bot sends a ping (`ProtoOAVersionReq`); no reply within 10 s → reconnect. Reconnect waits 1, 2, 4 … up to 60 s (+ up to 20 % random jitter), then authenticates the app and every account again.

> **Library risk (v1.4):** `cTrader.OpenAPI.Net` v1.4.4 was last published in 2022 and targets .NET 6, with old dependencies. It runs on .NET 10, so Phase 1 starts with it. Fallback if it causes problems: generate C# classes from Spotware's `.proto` files and write a thin TLS client.
>
> *v1.8 finding (Phase 1 step 1):* the library works on .NET 10 — app auth, account list, account auth and trader info all succeed on both demo accounts. Its message definitions date from 2022 and already miss at least one newer field (`brokerTitleShort` in the account list). Not a blocker today; if a later step needs newer fields, switch to the fallback.
>
> *v1.11 finding (Phase 1 step 3):* the library's message factory cannot decode some replies — e.g. `ProtoOASubscribeLiveTrendbarRes` (payload type 2165) — so those requests timed out. Fixed in `Trader.Core` (`PayloadDecoder`): when the factory returns nothing, the message is decoded from a table built from the library's own generated message classes (84 types). Still no need for the full fallback.

### 5.2 Core functions the bot framework must provide

- Connect, authenticate application, authenticate each account, heartbeat, auto-reconnect.
- Subscribe to live spots (bid/ask) and candles (trendbars) per symbol — including M5 trendbars for the 21 EMA and trailing logic.
- Place / modify / close orders; always attach broker-side protection (hedge order or, once in profit, a stop-loss — see Section 10).
- Receive execution events (fills, rejections) and reconcile positions on reconnect, rebuilding leg pairs from their labels.
- Respect Open API request-rate limits (shared budget per connection) — trailing stops and 4-leg trades increase order-modify traffic.
- Structured logging (Serilog) in UTC timestamps.

### 5.3 One framework, many bots *(v1.10)*

The owner may run up to ~10 bots at the same time. The functions of Section 5.2 are therefore written **once**, in the shared library `src/Trader.Core`, and every bot uses it; bots never copy that code. Usage guide: [`src/Trader.Core/README.md`](../src/Trader.Core/README.md).

- **One project per bot:** `src/Trader.<BotName>/`, referencing `Trader.Core`. A new bot starts as a copy of `src/Trader.SampleBot` (the smallest complete bot, ~30 lines); the steps are in the usage guide.
- **`BotRunner`** gives every bot the same start-up and shut-down: common options (`--account`, `--verbose`, `--test-drop-after`, `--help`), logging, secrets (demo only), clean stop on Ctrl+C / SIGTERM, exit codes (0 normal, 1 fatal, 2 bad options, 3 bug).
- **Accounts per bot:** each bot chooses its accounts (`--account <ctid>`, or a default in code); default is all demo accounts the token grants.
- **Identity:** each running bot gets an instance ID = bot name + 6 hex characters, e.g. `AuditBot-3f9a1c`, shown on every log line.
- **Order labels:** instance ID, optionally followed by the leg name (10.8.2), e.g. `TrendBot-3f9a1c-T1-S1`; at most 100 characters (Spotware limit). The instance ID changes on every restart but positions do not, so after a restart a bot recognises its own positions by **bot name** (plus account and leg) and uses the instance ID only to attribute fills and log lines.
- **Send time in the order comment** *(v1.14, owner's choice)*: every bot order carries `sent <yyyy-MM-dd HH:mm:ss.fff>Z` (UTC, milliseconds) in its `comment` field, shown in cTrader next to the label and kept by the server, next to the server's own open time (ms). The label stays the identity for ownership. Considered and not chosen: `BotName-YYYYMMDD-HHMMSS` as the label (not unique when several orders go out in the same second, e.g. a 4-leg trade; loses the instance ID).
- **Connections:** each bot has its own connection (own 50 req/s budget). One secrets file serves all bots; when one refreshes the tokens, the others pick up the new ones (Section 5.1).
- **Tested 17-Oct-2026:** 10 bots at once on separate connections, all authenticating the same two demo accounts, ran without errors or dropped sessions. Spotware does not publish a limit on simultaneous connections (Section 12).
- **Order ownership rule** *(v1.12)* — how a bot tells its own orders from the owner's manual trades and anything else on the account:
  - Every bot order carries its label (above) in the order's `label` field; the server keeps it on the order and the position and returns it in every execution event and reconcile. Manual trades normally have no label.
  - The bot also records the `positionId` of every fill it receives, as a second, independent check during a run. After a restart it recognises its positions by **bot name** in the label.
  - A bot **only modifies or closes positions and orders that carry its own label**, and always closes **by `positionId`**, never "close all on a symbol".
  - Everything else is **foreign** — the owner's manual trades, other apps with access to the account, broker actions such as a stop-out: never modified, logged to `events.csv` when seen, but **counted for risk** (margin, exposure; Section 9), because it uses the same account money.
  - This works because the accounts are **Hedging** (decision 8): every order is its own position, so a manual trade and a bot trade on the same pair and side never merge. Other clients of the same broker are other accounts, which the bot cannot see at all.
  - **Exception — TM** *(v1.18, owner's decision)*: TM takes over unlabelled positions on the symbols listed in its config (the owner's manual trades meant for TM): it removes or copies their stop-loss and take-profit, and closes one only under its 3R loss limit (10.9.5). Unlisted symbols and other bots' positions stay foreign to TM.
  - Limit: the label is plain text, so someone with access to the account could type a bot-style label on a manual order. The recorded `positionId`s catch this during a run; if manual labels are ever used, the post-restart rule becomes "label matches **and** `positionId` is in the bot's saved list".
- **Bot pages** *(v1.19)*: every bot has its own details page in [`docs/bots/`](bots/README.md) — status, links to its plan sections, code, config, how to run it, history — and a row in the README's Bots table. Pages summarise; this plan stays authoritative for the rules. Pages so far: [Audit Bot](bots/audit-bot.md), [TM](bots/tm.md).
- **Outside this repo:** pack `Trader.Core` as a versioned NuGet package in `~/tradebot/packages` and reference a fixed version, rather than copying the folder.

## 6. First bot: the Audit Bot

*Details page: [docs/bots/audit-bot.md](bots/audit-bot.md) (v1.19).*

### 6.1 Goal

Run the same measurements on two brokers in parallel for at least 2 weeks on demo accounts, then compare execution quality with data instead of marketing claims.

### 6.2 What it measures

| Measurement | How | Why it matters |
|---|---|---|
| Spread | Read every bid/ask tick per symbol; write one summary row per interval (Section 6.2.3) *(v1.8)* | Real cost; widening at news/rollover |
| Slippage | Small scheduled probe orders (0.01 lot): requested vs filled price | Shows fill fairness, positive vs negative slippage |
| Execution latency | Time from order send → fill event | Speed of the broker path |
| Rejections / requotes | Count and reason codes | Hidden friction |
| Swap & commission | Read from deal/position data | Cost of holding trend trades |
| Feed gaps | Longest gap between ticks, per interval row | Feed stability |

#### 6.2.1 Symbols *(v1.8)*

**AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD** — same names at both brokers, all enabled for trading on both demo accounts (checked 17-Oct-2026 through the API). Replaces the v1.0 draft list (EURUSD, GBPUSD, USDJPY, XAUUSD, US500, BTCUSD).

- Crypto trades 24/7, so BTCUSD and ETHUSD also show weekend spread and feed behaviour while forex is closed.
- USDJPY and GBPUSD are subscribed **for currency conversion only** (JPY and GBP spread costs → USD); they are not logged or probed.
- Crypto check, 17-Oct-2026: IC Markets lists 105 crypto symbols, Pepperstone 53; 25 are enabled at both (22 under the same name; Avalanche, Chainlink and Sonic under different names). The bot looks symbols up by name per broker, with a per-broker name map for any that differ.

#### 6.2.2 Probe windows *(v1.8)*

Probe orders (0.01 lot) open and close immediately. Each probe row in `probes.csv` carries its window name. Times are local market times; the bot converts them to UTC with time-zone rules, so daylight-saving changes are automatic (UK clocks change 25-Oct-2026, US clocks 1-Nov-2026 — for that week London and New York are 4 h apart, not 5 h).

| Window (log name) | Local time | Days |
|---|---|---|
| `LondonOpen` | 08:00 London | Mon–Fri |
| `NewYorkOpen` | 08:00 New York | Mon–Fri |
| `QuietHour` | 03:00 London (Asian session; away from the 17:00 New York rollover) | Mon–Fri |
| `NewsRelease-2m`, `NewsRelease+5s`, `NewsRelease+5m` *(v1.13)* | 2 min before, 5 s after and 5 min after 08:30 New York, on US **Non-Farm Payrolls** and **CPI** release days | Dates in `~/tradebot/config/news-releases.json` (from the BLS schedules; NFP 6-Nov, 4-Dec, 8-Jan, 5-Feb; CPI 10-Nov, 10-Dec, 13-Jan, 11-Feb) |

Per window, each symbol gets one buy probe and one sell probe, a few seconds apart; instances on the same account are staggered (Section 6.4). For `NewsRelease`, probes run just before, just after and about 5 minutes after the release (exact offsets: Section 12).

*(v1.13, built in step 5)* How a probe runs:
- **Who:** only the instance that is **Primary** on the account (6.4). Accounts run in parallel; within an account one probe at a time, symbols in config order, buy then sell, 2 s apart. A symbol whose market is closed is skipped. A window more than 60 s late (computer asleep, bot busy) is skipped and logged.
- **Order:** market order of **0.01 lot** (forex 1,000 units; crypto 0.01 coin), labelled with the instance ID, with a wide server-side **backup stop-loss**: 50 pips for forex, 2 % of the price for crypto. Then closed at once **by its `positionId`** (ownership rule, 5.3). Both the open and the close are written to `probes.csv`.
- **Measured:** requested price = ask for a buy, bid for a sell, at the moment of sending; fill price from the fill message; latency = request sent → fill message received on this machine; commission from the fill.
- **Left-overs:** before every window, after every connect and on becoming Primary, the bot asks the server for all open positions (reconcile) and closes any with an `AuditBot-…` label. Tested 18-Oct-2026 by killing the bot (`kill -9`) while a probe was open: the next start closed it. Positions without the label are never touched; their number is logged (`ForeignPositions`), and fills of foreign orders are logged once (`ForeignTrade`).
- **Close failures:** retried up to 3 times; if the position is still open the bot logs `ProbeCloseFailed`, the backup stop-loss stays on the server, and the clean-up retries before the next window.
- **Test option:** `--test-probe-now` runs one window named `Test` at start-up.
- **First results (18-Oct-2026, Sunday, crypto only):** all orders filled, no rejections; latency ~470 ms (IC Markets) and ~580 ms (Pepperstone); commission 0 on crypto at both.

#### 6.2.3 Log intervals *(v1.8)*

| Period | Definition | Spread row every |
|---|---|---|
| Main hours | Mon–Fri, 08:00 London to 17:00 New York (London open to New York close) | **2 min** |
| Off-peak | All other times, including weekends (crypto only — forex is closed from Fri 17:00 to Sun 17:00 New York) | **5 min** |

Each row summarises all ticks in its interval (min / average / median / 95th-percentile / max spread, tick count, longest gap), so short spikes and gaps between rows are not lost. *(v1.11)* Ticks are placed in intervals by the **server's timestamp**. A row is skipped only when there were no ticks **and** the market was closed during the interval, per the broker's own trading schedule for the symbol (weekend, and the daily break around 17:00 New York: ~2 min for forex, up to 6 min for crypto). No ticks while the market is open gives a row with `ticks` = 0 — a feed gap (including time the bot itself was disconnected; `events.csv` shows which). The last price the server re-sends on every subscribe carries its old timestamp and is ignored. Events (connect, reconnect, errors, rejections) are written when they happen, not on a timer. The 10-second Open API heartbeat (a Spotware protocol requirement) and the 10-second registry status file (Section 6.4, overwritten in place) are not logs and stay at 10 s.

#### 6.2.4 Alerts and probe summary *(v1.15)*

Wide spreads and bad fills are **reported as warnings** (journal and `events.csv`), not only kept in the CSV rows. Limits are per broker in `audit.<broker>.json` (`"alerts"`, optional; defaults below). Forex in **pips**; crypto in **USD per lot**, because a crypto "pip" differs by broker (IC BTCUSD pip = $0.01, Pepperstone $0.10) and cannot be compared or averaged across symbols.

| Alert | Event | Fires when | Default limit |
|---|---|---|---|
| Spread wide | `SpreadWide` (warning), then `SpreadNormal` (info) | A `spreads.csv` row's **average** spread is above the limit. Raised once; cleared once when back under the limit, with duration and peak (like an alarm raise/clear) | Forex 2 pips; crypto $50/lot; ETHUSD $10/lot |
| Slippage high | `SlippageHigh` (warning) | One probe fill (open or close) **worse** than requested by more than the limit. Better-than-requested fills are not alerted | Forex 1 pip; crypto $10/lot; ETHUSD $3/lot |

Defaults chosen from the first day of data (19-Oct-2026): forex averages under 1 pip on both brokers, BTCUSD $5–15, ETHUSD $2–3 per lot. Spreads above 2 pips were seen only around the Sunday reopen; the daily break around 17:00 New York will also raise `SpreadWide` — this is real and is reported.

**Probe window summary** (`ProbeWindowDone`, `ProbeSlippage`): fills, latency average and maximum; then one line per asset class with average slippage, the **worst fill by name**, and how many fills were over the limit, e.g. `forex: slippage avg +0.06 pips, worst -0.30 pips (AUDJPY close buy); 0 of 16 fills over the alert limit`. A single average over all symbols is not used: it mixed units and let good and bad fills cancel out.

### 6.3 Per-broker logging on disk

Each broker gets its own folder; each running instance gets its own sub-folder. Nothing is shared between brokers.

```
~/tradebot/
  config/
    audit.brokerA.json            # account id, symbols, probe schedule
    audit.brokerB.json
  run/instances/                  # live instance registry (heartbeat files)
    brokerA-<accountId>-<instanceId>.json
  logs/
    brokerA/<accountId>/<instanceId>/2026-10-12/
      spreads.csv  # one summary row per symbol per interval (v1.8; was ticks.csv)
      probes.csv   # one row per probe order (open and close each a row, v1.13)
      events.csv   # connect, reconnect, errors, rejections (v1.8; was events.log)
    brokerB/<accountId>/<instanceId>/2026-10-12/ ...
  reports/
    daily-compare-2026-10-12.csv  # one UTC day, both brokers (6.5); made daily at 00:15 UTC
    summary-2026-10-18_2026-11-01.csv  # a range of days (final 2-week summary, v1.16)
  bin/AuditBot/, bin/AuditReport/  # published builds that the services run (Section 7, v1.14/v1.16)
```

#### 6.3.1 File format *(v1.8)*

All logs and reports are CSV files that open directly in Google Sheets, Excel or LibreOffice and import into other tools:

- UTF-8, comma-separated, one header row, one record per line; text quoted only when it contains a comma or quote.
- Times in UTC as `yyyy-MM-dd HH:mm:ss` (Google Sheets reads this as a date-time); the column name says `_utc`.
- Numbers with a `.` decimal point and no thousands separators; units in the column name (`_pips`, `_usd`, `_ms`, `_s`).
- One file per day (UTC) per broker / account / instance, path as above.

**Pips and USD per lot.** Spreads and slippage are given in pips and as USD cost for one lot:

- Forex: 1 pip = 0.0001 (0.01 for JPY pairs); 1 lot = 100,000 units of the base currency. USD value uses the live USDJPY or GBPUSD rate from the same broker (e.g. EURGBP: spread × 100,000 × GBPUSD).
- Crypto: the pip is the broker's own pip size for the symbol, which may differ between brokers — so compare the USD columns, not the pip columns. A 100,000-unit lot does not apply (that would be 100,000 BTC); USD is given per 1 lot = 1 coin. *(v1.11, confirmed from the API 18-Oct-2026: both brokers' lot = 1 coin; BTCUSD/ETHUSD pip = 0.01 at IC Markets, 0.1 at Pepperstone.)*
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
| `action` *(v1.13)* | `open` (the probe) or `close` (closing it — also an order with its own slippage and latency) |
| `symbol`, `side`, `lots` | e.g. `EURUSD`, `buy`, `0.01` |
| `req_px`, `fill_px` | Price seen when sending; price filled |
| `slip_pips`, `slip_usd` | Slippage; negative = worse for us. USD per lot |
| `latency_ms` | Order send → fill event |
| `commission_usd` *(v1.13)* | Commission charged for this order (positive = paid), from the fill |
| `result`, `reason` | `filled` / `rejected` / `error` and the broker's reason code (`TIMEOUT`, `DISCONNECTED` for `error`) |
| `position_id` *(v1.13)* | The position the order opened or closed |
| `label` | Order label (instance ID) |

**`events.csv`:** `time_utc`, `level`, `event`, `detail`.

### 6.4 Multi-instance awareness ("know your siblings")

Each instance must know how many copies of the Audit Bot are running and behave sensibly.

- **Identity:** on start, an instance creates an ID = bot name + short random ID (e.g. `AuditBot-3f9a1c`, Section 5.3) and writes it into its log path; broker and account are already in that path and in the registry file name. *(v1.10; was broker + account + short GUID)*
- **Registry:** it writes a heartbeat file in `run/instances/` every 10 seconds (a status file overwritten in place, not a log) (PID, broker, account, symbols, role, last-seen time). Files older than 30 seconds are treated as dead and cleaned up. *(v1.12)* One file per account: `<broker>-<ctidTraderAccountId>-<instanceId>.json`, written to a temp file then renamed. A file is also removed at once when its PID is no longer running on this machine (e.g. after `kill -9`).
- **Discovery:** each instance reads the registry to know the total count, and the count per broker/account.
- **Tactful behaviour:**
  - Different brokers → instances run fully independently.
  - Same broker + same account → the first instance is Primary (may send probe orders); later ones become Passive (log ticks only) to avoid duplicate trades. *(v1.12)* **Primary = the instance holding the exclusive lock file** `<BotName>-<ctidTraderAccountId>.primary.lock` in `run/instances/`. Roles are **per account**: one process can be Primary on IC Markets and Passive on Pepperstone. Passive instances still write `spreads.csv` in their own instance folder.
  - Probe schedules are staggered by a few seconds between instances so they never fire together. *(v1.12)* Each instance gets a **slot** = its position among live instances of the same bot on the account, oldest first.
  - Every order carries a label with the instance ID, so fills are attributed to the correct instance.
  - ~~If the Primary dies, the oldest live Passive instance promotes itself after the heartbeat timeout.~~ *(v1.12, owner's choice)* If the Primary stops, Linux releases its lock at once — even on `kill -9` or a crash — and the first Passive instance to take the lock (checked every 2 s) becomes Primary. There can never be two Primaries on one machine. Tested 18-Oct-2026: Primary killed with `kill -9`, takeover within 1 s.
- **Visibility:** each instance logs the current sibling count on start, on change, and hourly. *(v1.12)* Event `Siblings` in `events.csv` (list of instances with roles, own role and slot); `Promoted` when an instance becomes Primary; `InstanceGone` when a dead instance's file is removed.
- *(v1.12)* The registry is in `Trader.Core` (`InstanceRegistry`), so every bot gets the same "one Primary per bot per account" rule — a trend bot and the Audit Bot each have their own Primary on the same account. Limit: it coordinates instances on **one machine**; an account must be run from one machine only.

### 6.5 Output

- Daily comparison CSV per broker, built from `spreads.csv` and `probes.csv` in the format of Section 6.3.1: median/95th-percentile spread (pips and USD per lot), average slippage, latency, rejection count — per symbol and per probe window.
- Final 2-week summary → broker choice recorded in this document.

#### 6.5.1 The report tool *(v1.16)*

`src/Trader.AuditReport` — a separate program that **only reads** the CSV logs (never connects to cTrader), so it cannot disturb the running bot. Both brokers in one file:

| Run | Output |
|---|---|
| Daily, automatically (systemd timer, 00:15 UTC; `--missing`) | `reports/daily-compare-<date>.csv` for every complete UTC day that has none yet — also catches up after the laptop was off |
| `--date 2026-10-19` | That day, rebuilt |
| `--from 2026-10-18 --to 2026-11-01` | `reports/summary-<from>_<to>.csv` — same columns over the whole range (the final summary) |

- **Merging:** a day may be spread over several instance folders (every restart starts a new one). They are merged; if two instances logged the same interval (Primary and Passive), it is counted once (the row with more ticks).
- **Rows:** one per broker × symbol × period, sorted by symbol, then period, then broker, so IC Markets and Pepperstone are on adjacent lines. Periods: `day` (all), `main` and `offpeak` (spreads, 6.2.3), and each probe window (`QuietHour`, `LondonOpen`, …; probes only). Then one `ALL` row per broker with order totals, latency and reliability.
- **Columns:**

| Group | Columns |
|---|---|
| Identity | `date_from`, `date_to`, `broker`, `broker_name`, `symbol`, `period` |
| Coverage | `spread_rows`, `logged_min` (minutes covered by rows), `ticks`, `gap_rows` (rows with no ticks while open) |
| Spread | `spread_median_pips`, `spread_p95_pips`, `spread_max_pips`, and the same in `_usd` (per lot) |
| Probes | `orders`, `filled`, `rejected`, `order_errors`; `slip_avg_pips`, `slip_worst_pips`, `slip_avg_usd`, `slip_worst_usd` (negative = worse for us); `latency_avg_ms`, `latency_median_ms`, `latency_max_ms`; `commission_usd` |
| Reliability (`ALL` row) | `instances`, `connection_drops`, `spread_alerts`, `slippage_alerts`, `missed_windows`, `log_warnings`, `log_errors` |

- **Spread figures** come from the interval rows: median and p95 of the rows' average spread, each row weighted by its length (half / 95 % of the time the spread averaged at or below it); max = largest single spread seen. **Crypto: compare by the USD columns** — pips differ by broker (6.2.4). The `ALL` row leaves slippage out, because averaging it over forex and crypto would mix units.
- **Connection drops** are counted per broker, but both brokers share one connection, so a drop shows on both.

## 7. Execution environment: Ubuntu

| Component | Choice |
|---|---|
| OS | Ubuntu 26.04 LTS (current machine: HP laptop) *(v1.7)* |
| Runtime / SDK | .NET 10 LTS SDK from Ubuntu's archive (`dotnet-sdk-10.0`); runtime only on the VPS later *(v1.4)* |
| Editor | VS Code + C# Dev Kit (AI-assisted coding) |
| Running the bot | systemd service (one unit per instance) → later Docker container. *(v1.14)* Audit Bot: **systemd user service** `trader-auditbot` (`deploy/trader-auditbot.service`), running a **published Release build** in `~/tradebot/bin/AuditBot/` so code changes never affect the running bot; `scripts/deploy-auditbot.sh` publishes and installs. Restarts 30 s after a crash; does not restart after a fatal problem the bot reports itself (exit 1/2 — needs a human). Console output in the journal (`journalctl --user -u trader-auditbot`). *(v1.16)* Daily report: **systemd user timer** `trader-auditreport.timer` (00:15 UTC, catches up after the laptop was off) starting `trader-auditreport.service`, which runs the published build in `~/tradebot/bin/AuditReport/`; `scripts/deploy-auditreport.sh` publishes, installs and enables it. |
| Secrets | API client secret and tokens in `~/.config/trader/secrets.env` (permissions 600) — never in Git |
| Setup check | `scripts/check-env.sh` — read-only check of OS, .NET, Git, VS Code, clock, port 5035, folders, secrets *(v1.4)* |
| Version control | Private Git repository (this repo — holds the plan and the code) |
| Clock | NTP sync enabled; all logs in UTC |
| Laptop during the audit *(v1.14)* | Automatic suspend off (Settings → Power), lid closing must not suspend, mains power; a sleeping laptop records nothing and probe windows are logged as missed. User services start at login; to run without anyone logged in: `loginctl enable-linger` (owner's choice). |
| Later | Small Linux VPS near broker servers (London / New York); same container |

> **Protection rule:** because home power or internet can drop, every live position must be protected by an order held on the broker's server. Before 1R this is the leg's hedge buy-stop; from 1R onward it is the leg's server-side trailing stop-loss (Section 10.8). *(v1.13)* Audit Bot probes (open for about a second) carry a wide server-side backup stop-loss instead (6.2.2).

## 8. Phased roadmap

| Phase | Name | Deliverable | Target |
|---|---|---|---|
| 0 | Setup | Demo accounts at 2 brokers, cTID linked, Open API app, .NET + VS Code on Ubuntu, Git repo. Checklist: [docs/setup.md](setup.md). *Complete 16-Oct-2026: Git repo, IC Markets demo (Broker A), Pepperstone demo (Broker B), Ubuntu toolchain, Open API app approved, tokens in secrets file; `check-env.sh` 0 warnings.* | Week 1 |
| 1 | Framework + Audit Bot | Connect/auth/reconnect, spot & candle feed, probe orders, per-broker logs, instance registry. *Complete 19-Oct-2026 (v1.16): all 6 steps of Section 8.1.* | Weeks 2–3 |
| 2 | Audit run | 2 brokers × 2 weeks on demo; daily reports; choose broker. *Started 18-Oct-2026 (v1.14), before Phase 1 step 6, by the owner's decision; covers the UK and US clock changes (25-Oct, 1-Nov) and the NFP (6-Nov) and CPI (10-Nov) releases if run to mid-November.* | Weeks 4–5 |
| 3 | Backtesting | Strategy and exit rules (incl. Section 10 scenarios and 10.8 scale-out/pyramid rules) written as a spec; backtest with broker data incl. costs; hedge version vs net-position version compared | Weeks 6–8 |
| 4 | Trend bot + human link | *(v1.18)* **First: TM** (Trade Manually, Section 10.9) on both demos — built when the owner says go, alongside the Phase 2 audit run; Phase 3 later tunes its parameters (15 %, A, B, 2R/3R). Then: semi-auto execution, hedge-and-recover exit logic, 4-leg structure, trailing and pyramiding, dashboard/config, then TradingView webhook | Weeks 9–12 |
| 5 | Go-live (small) | VPS deployment, small live account, strict risk limits, review after 1 month | After Phase 4 |

### 8.1 Phase 1 steps *(v1.8)*

Each step is built only when the owner says so.

| Step | Name | Content | Status |
|---|---|---|---|
| 1 | Connect and list accounts | TLS connect to the demo host, app auth, account list, account auth, trader info (`src/Trader.ListAccounts`) | **Done 16-Oct-2026** — both demo accounts Hedged; `ctidTraderAccountId` 49123893 (IC Markets), 49124666 (Pepperstone) |
| 2 | Stay connected | Event stream, heartbeat watch, auto-reconnect with re-auth, access-token refresh with atomic save of the new token pair (`src/Trader.AuditBot`, `ConnectionManager`) | **Done 17-Oct-2026** — reconnects tested with simulated drops (back up within ~3 s); first token refresh done |
| 3 | Price feed to disk | Config files `audit.brokerX.json`; symbol lookup and name map; spot subscription; `spreads.csv` summaries (Section 6.2.3, 6.3.1); feed gaps; M5 trendbar subscription; `events.csv` | **Done 18-Oct-2026** — live on both demos (Sunday: crypto rows every 5 min, forex skipped while closed; resubscribe after forced drops) |
| 4 | Instance registry | Section 6.4 (lock-file Primary, per-account roles, slots) | **Done 18-Oct-2026** — 3 copies live; Primary killed with `kill -9`, takeover within 1 s |
| 5 | Probe orders | Windows of Section 6.2.2; `probes.csv`; ownership rule (5.3) | **Done 18-Oct-2026** — probes on both demos (crypto, Sunday); left-over clean-up tested with `kill -9`; manual (unlabelled) position left untouched |
| 6 | Daily report | Section 6.5 | **Done 19-Oct-2026** — `src/Trader.AuditReport` (6.5.1), daily timer; report for 18-Oct built from the live logs |

*Between steps 2 and 3 (17-Oct-2026, v1.10):* framework made reusable for many bots — `BotRunner`, per-bot accounts, instance ID and order labels, usage guide, `src/Trader.SampleBot` (Section 5.3).

## 9. Risk guardrails (apply from Phase 4)

- **Risk per trade:** 1% of equity to the hedge trigger level (adjustable, never above 2%). Applies to every pyramid add individually.
- **Daily loss limit:** bot stops new entries (including pyramid adds) for the day after a set loss.
- Maximum open positions and maximum total exposure caps.
- **Exposure cap for pyramiding (v1.3):** no new add if, after the add, effective leverage would exceed **1:10** or margin level would fall below **300%**. Both values are parameters. There is no cap on the number of adds.
- No trade if spread exceeds a per-symbol threshold.
- Effective leverage typically under 1:10 even if the account allows 1:500.
- **Kill switch:** one command closes all bot positions (including hedges) and stops the service.
- *(v1.18)* **TM exception:** TM does not block on these limits; when one is broken it warns and still mirrors and hedges (owner's decision, 10.9.3). TM's own limits are per leg: warning at 2R loss, close at 3R (10.9.5).

## 10. Exit logic: hedge-and-recover (no stop-loss for risk)

*New in v1.1, updated v1.2 and v1.3.* Scenario 1 is defined below and illustrated with a real AUD/JPY chart (Figure 1). v1.3 adds the position structure, leg naming, scale-out and pyramiding rules (Section 10.8). Scenarios 2–5 will be added in a later session.

### 10.1 Concept

- The real trade is always a **SHORT** position.
- Instead of a stop-loss, the bot places a pending **BUY-STOP** order (the hedge) above the short entry. If price rises to that level, the hedge opens and the short's loss stops growing.
- The LONG side is used only as a hedge, never as a real trade.
- Once the hedge is safely in profit, the bot protects it with an exit just above its own entry. If the move against the short turns out to be a wick, the hedge closes at about break-even and the short continues.
- "No stop-loss" applies to **risk**. Once a short leg is 1R in profit, a stop-loss is used to **lock in** gains (Section 10.8).

### 10.2 Terms and parameters

| Symbol | Meaning | Set by |
|---|---|---|
| E | Short entry price (real trade) | Trader / strategy |
| H | Hedge trigger level: price of the pending buy-stop. Working assumption: H = E + ~10 pips | Trader (working assumption, to be tested) |
| Fh | Actual hedge fill price (H plus any slippage) | Market |
| A | Arming distance: how far price must rise above Fh before the hedge exit is placed ("a bit more") | Parameter — value to be set in backtest |
| X | Hedge exit = Fh + 1 pip (a sell-stop on the hedge) | Rule |
| T | Profit target / exit of the short | Trader / strategy |
| Size | Each hedge leg = the size of its own short leg (each = ½ of trade size). Full hedge, per pair. | Confirmed v1.3 |
| 1R | Profit equal to the distance risked: (H − E). With H = E + 10 pips, 1R = 10 pips | Rule (v1.3) |

### 10.3 Scenario 1 — wick against the short, then the down-move resumes

Market path: price rises through H, makes a spike (wick) higher, then reverses and falls back below H and on toward the short's target. Only one whipsaw against the main position.

![Figure 1 — AUD/JPY daily chart](images/audjpy-scenario1.png)

*Figure 1 — AUD/JPY daily (OANDA, TradingView). Blue line = short entry E ≈ 114.62 (July high retested in September). The early-September wick to ≈ 114.97 is the single whipsaw; price then fell to ≈ 109.7 in mid-September.*

| Step | Market event | Bot action | Position after the step |
|---|---|---|---|
| 1 | Trader takes the short | Open SHORT at E. Place BUY-STOP hedge at H (same size). | Short open; hedge pending |
| 2 | Price rises to H | Hedge fills at Fh. Bot does nothing else yet — it waits. | Short + long = net zero. Short's loss frozen at (Fh − E). |
| 3 | Price rises a further A above Fh | Arm the hedge exit: place SELL-STOP on the hedge at X = Fh + 1 pip. | Hedge now protected at about break-even |
| 4 | Spike reverses (the wick) | Price falls to X → hedge closes at about +1 pip. | Only the short remains open |
| 5 | Down-move resumes | Short is managed by the rules in Section 10.8 (trailing, adds) until closed. | Flat |

**Result of Scenario 1:** hedge ≈ +1 pip (before slippage); short = (E − T) pips profit. A normal stop-loss at H would have closed the short at a loss on the wick and missed the move.

### 10.4 Worked example — real AUD/JPY levels from Figure 1 (1 lot)

| Item | Value |
|---|---|
| Short entry E (blue line) | 114.62 |
| Hedge buy-stop H = E + 10 pips (fill Fh) | 114.72 |
| Arming distance A (example) | 15 pips → hedge exit armed when price reaches 114.87 |
| Wick high | 114.97 (25 pips beyond Fh, so the exit arms) |
| Hedge exit X = Fh + 1 pip | 114.73 → hedge closes +1 pip on the way down |
| Short target T (example) | 110.00 → short closes +462 pips |
| Total (before costs) | +463 pips ≈ $3,100 at ~$6.76 per pip (USD/JPY ≈ 148 assumed) |
| Costs to subtract | Spread on short and on hedge (zero-commission account: cost is in the spread), plus swap for nights held |

> **Key constraint shown by this chart:** Scenario 1 only happens when the wick extends at least A beyond the hedge fill. Here the wick gave 25 pips of room above Fh, so A must be below about 25 pips. With a larger A, or a smaller wick, the hedge exit never arms and the trade falls into Scenario 2.

### 10.5 Equivalent rule on a single net position (for backtesting)

Economically, Scenario 1 is the same as: close the short at H, then re-open the short when price falls back to Fh + 1 pip after having risen at least A above Fh. Exposure, profit/loss and spread paid are the same at every moment. The backtest will run both versions side by side; matching results confirm the logic, and the live version can use whichever the trader prefers. The difference that remains is swap and margin while both legs are open.

### 10.6 Requirements for Scenario 1 to work

- The cTrader account must be a **hedging** account (long and short on the same symbol at the same time).
- The hedge buy-stop is held on the broker's server from the moment the short opens, so the short stays protected if the bot or home internet goes down.
- Every order carries a label linking the short and its hedge, so the bot can reconcile both legs after a reconnect (naming in Section 10.8.2).
- Pip size, spread and slippage are taken from the broker's symbol data, not hard-coded.

### 10.7 Scenarios still to be defined

*(v1.18)* For **TM**, Scenarios 2–5 are answered in Section 10.9: Scenario 2 → hedge stop ½R below its fill, then re-hedge at H (10.9.4); Scenario 3 → each cycle's cost known, bounded by 2R/3R (10.9.5); Scenario 4 → 3R loss limit closes the leg (10.9.5); Scenario 5 → pop-up before news and the Friday close (10.9.6). The list below stays open for a fully automatic trend bot.

- **Scenario 2** — hedge fills, then price falls back before the hedge exit is armed (price never reaches Fh + A).
- **Scenario 3** — chop around H (whipsaw): hedge opens and closes repeatedly; each cycle costs spread.
- **Scenario 4** — sustained trend against the short: hedge never closes; both legs stay locked. When and how to unwind.
- **Scenario 5** — gaps and news spikes: hedge buy-stop fills far beyond H. (Its effect is multiplied by pyramiding — see 10.8.6.)

### 10.8 Position structure, scale-out and pyramiding

*New in v1.3.* Applies after a trade is on track: either it never needed its hedge, or Scenario 1 completed and the hedge closed at about break-even. The trader accepts the risk of whipsaw and of the hedge exit not arming (Scenarios 2–3) as part of this design.

#### 10.8.1 Split structure

- Every trade is opened as **two half-size shorts and two half-size hedge buy-stops**. Example: a 1-lot trade = 2 × 0.5-lot shorts + 2 × 0.5-lot buy-stops.
- Both shorts sit at the same entry E. Both hedges sit at the same trigger level H.
- Each short is paired with its own hedge, so closing a pair never requires resizing an order.
- Minimum trade size is therefore 2 × the broker's minimum volume (e.g. 0.02 lots), and the trade size must split into two valid volume steps.

#### 10.8.2 Leg naming convention

Leg numbers keep counting up across trades. Trade *n* uses the odd/even pair of numbers (2n − 1, 2n). The label is written into each order's label/comment field and is used to rebuild pairs after a reconnect. *(v1.10)* Full label = bot instance ID + leg, e.g. `TrendBot-3f9a1c-T1-S1` (Section 5.3).

| Trade | Runner pair (odd) | Rotating pair (even) |
|---|---|---|
| T1 | T1-S1-L1 | T1-S2-L2 |
| T2 | T2-S3-L3 | T2-S4-L4 |
| T3 | T3-S5-L5 | T3-S6-L6 |
| Tn | Tn-S(2n−1)-L(2n−1) | Tn-S(2n)-L(2n) |

S = short leg, L = its hedge (long buy-stop). Odd legs are **runners**; even legs are **rotating** legs.

#### 10.8.3 Lifecycle rules

| Rule | Trigger | Bot action |
|---|---|---|
| **R1** Entry | Trader decides to short (Trade 1 of a sequence) | Open both shorts at E; place both hedge buy-stops at H. Size = 1% risk to H. |
| **R2** Protect at 1R | A short leg reaches 1R profit (price ≤ E − (H − E)) | First place a server-side stop-loss at E (break-even) or better on that leg; **then** cancel its hedge buy-stop (or close it if filled). Order matters: the leg is never left unprotected. |
| **R3** Trailing | Each M5 candle that closes below the 21 EMA | Move each protected leg's stop to just above the most recent M5 lower high (plus a spread buffer). Stops only move down, never up. |
| **R4** Pyramid add | An M5 candle closes back at the 21 EMA (retracement), **and** the latest trade has already reached 1R | Bot opens the next trade Tn+1 with the same 4-leg structure at the new entry; its hedges at new E + 10 pips; size = 1% risk to its own H. |
| **R5** Rotation | Trade Tn+1 is filled | Close the even (rotating) leg of the previous trade Tn, e.g. T2 opening closes T1-S2. Because of R2 this leg is already in profit when it closes. |
| **R6** Runners | Ongoing | Odd legs (S1, S3, S5…) stay open on trailing stops until stopped out. If no further add occurs, the latest even leg also keeps trailing. |
| **R7** Scope | Any add | Same symbol, short only. |
| **R8** Guards | Before every add | Previous trade ≥ 1R; spread below threshold; daily loss limit not hit; exposure cap respected (Section 9: effective leverage ≤ 1:10 and margin level ≥ 300% after the add). No limit on the number of adds. |

Effect: the position pyramids by half a trade per add. Older runners sit on stops in profit, while only the newest trade carries fresh risk, so open risk stays close to one trade's 1%.

#### 10.8.4 Illustration (levels from Figure 1, 1-lot trade)

| Event | Legs open | Protection |
|---|---|---|
| T1 opened at E = 114.62 | T1-S1, T1-S2 (0.5 each) | L1, L2 buy-stops at 114.72 |
| Price reaches 114.52 (1R = 10 pips) | T1-S1, T1-S2 | Stops at ≤ 114.62 placed, then L1 and L2 cancelled |
| Trend continues; M5 closes below 21 EMA | T1-S1, T1-S2 | Stops trail above each new M5 lower high |
| M5 retraces to 21 EMA → T2 opens | T1-S1, T2-S3, T2-S4 | T1-S2 closed in profit; L3, L4 at T2 entry + 10 pips |
| T2 reaches 1R | T1-S1, T2-S3, T2-S4 | All on trailing stops; next add allowed |

*Exact prices for adds depend on where the retracement happens; the table shows the sequence, not a backtest result.*

#### 10.8.5 Implementation notes for the bot

- Build M5 trendbars and the 21 EMA from the broker's own candle feed (same prices the bot trades on).
- "Last lower high" = the highest high of the most recent completed M5 swing below the 21 EMA; the exact swing definition is fixed in the Phase 3 spec and tested.
- Track each leg's state: `pending-hedge → hedged → protected (stop in profit) → trailing → closed`.
- On reconnect, rebuild all pairs from labels and re-check that every open short has either a hedge or a stop on the server.
- Stop modifications on many legs share the API rate budget; batch them per M5 candle close.

#### 10.8.6 Accepted risks and worst case

| Accepted risk | Worst case | How it is bounded |
|---|---|---|
| Gap or news spike through all trailing stops at once | All runners fill with heavy slippage on a large combined size; locked profit can turn into a loss | Exposure cap (leverage ≤ 1:10, margin level ≥ 300%); news filter to be added with Scenario 5 |
| Retracement deeper than expected after an add | New trade hits its hedge; older runners give back part of their open profit | Each add risks only 1%; older legs are at break-even or better |
| Spread paid on every add and every leg | Higher cost in choppy trends | Spread filter; adds only after 1R |
| M5 trailing exits early in a healthy pullback | Runner closes before the trend ends | Accepted; next retracement add re-enters the trend |
| Whipsaw / hedge exit not arming (Scenarios 2–3) | Small loss or locked position on a trade | Accepted by trader; rules to follow in Scenarios 2–3 |

### 10.9 TM — Trade Manually bot *(v1.18)*

*Details page: [docs/bots/tm.md](bots/tm.md) (v1.19).*

*Agreed with the owner on 19-Oct-2026 (20 questions, one at a time). TM is the **first trading bot**. The Audit Bot (Section 6) is unchanged and keeps running.*

#### 10.9.1 Idea

The owner opens every position **by hand in the cTrader app** (desktop, web or mobile). **TM** (`src/Trader.TM`, bot name `TM`) listens to the account; as soon as a new manual position appears on a listed symbol, TM **mirrors** it (opens a second position of the same side and size) and **hedges both**, then manages the hedges by the rules below. Closing trades is the owner's job; TM cleans up around whatever the owner closes. TM uses the shared framework (`Trader.Core`, Section 5.3): connection, reconnect, tokens, orders, logging, instance registry. **Demo accounts only** until Phase 5.

| Term | Meaning |
|---|---|
| Main | The owner's manual position (no label) |
| Mirror | TM's copy: same symbol, side and volume, opened at market just after the main is seen |
| Hedge | A pending stop order on the opposite side, one per short/long leg (main and mirror each have their own), same volume as its leg; becomes a position when it fills |
| E | A leg's own entry price (main and mirror differ by the fill difference) |
| H | Hedge level — the price of both hedge orders |
| 1R | Per leg: \|H − E\| |
| Fh | Price a hedge actually filled at |
| A | Arming distance = **1R** (Q8) |
| B | Hedge stop distance before arming = **½R** (Q10) |

Directions are mirrored: for a **short** the hedge is a **buy-stop above**; for a **long** a **sell-stop below** (Q2). Below, "up/above" is written for a short.

#### 10.9.2 Scope (Q1, Q16, Q18)

- Accounts: **both demos**, IC Markets 49123893 and Pepperstone 49124666, each managed separately with its own symbol list in TM's config.
- Symbols: per account in the config; at first the Audit Bot's six (AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD).
- TM takes over **positions without a label on a listed symbol**. Positions on unlisted symbols stay fully the owner's. Positions with another bot's label (e.g. Audit Bot probes) are never touched.
- **Exception to the ownership rule (5.3), owner's decision:** TM modifies the main (removes or copies its stop-loss and take-profit) — the only case in which a bot touches a position it did not open. TM never closes the main except under the 3R rule (10.9.5).

#### 10.9.3 Trade start (Q3, Q4, Q7)

| Step | Event | TM action |
|---|---|---|
| 1 | Owner opens a main, e.g. 0.5 lot short, with or without a stop-loss | Seen from the account's execution events (within about a second) |
| 2 | | **H:** the main's stop-loss if set; otherwise E + D, where **D = 15 % of the average daily range of the last 3 completed daily candles** (ATR(3) on D1 from the broker's own candles), at least **5 × the current spread**; if the history cannot be fetched, a fixed per-symbol fallback from the config (and a warning) |
| 3 | | Opens the **mirror** at market: same side and volume, with a server-side stop-loss at H so it is never unprotected |
| 4 | | Places **two hedge stop orders at H**, one per leg, each of that leg's volume |
| 5 | Both hedges confirmed by the server | Removes the stop-loss from the main and from the mirror |

- If the main has a **take-profit**, TM copies it to the mirror and follows later changes (Q14).
- If the mirror is rejected, the main still gets its hedge (and a warning). Risk limits (Section 9) do **not** block TM: it always mirrors and hedges; when a limit is broken it only warns (Q12, owner's decision: "Trade Manually — if I am opening, just follow the rules").
- Every further manual position is a **separate trade** with its own mirror and hedges; TM never opens adds of its own and never rotates (rules R4, R5 of 10.8 are not used by TM) (Q6).

#### 10.9.4 Hedge management (Q8, Q9, Q10)

| Situation | TM action |
|---|---|
| A hedge fills at Fh | Puts a stop on the hedge at **Fh − B** (½R below its fill). The leg's loss is now frozen |
| Price reaches **Fh + A** (1R beyond the fill) | Moves the hedge's stop up to **X = Fh + 1 pip** (the hedge exit is armed; Scenario 1) |
| Hedge closed by its **B stop** (price fell back before arming; Scenario 2) | Places a new hedge stop order at the original **H**. Cost of the cycle ≈ B + spread |
| Hedge closed at **X** (the wick reversed) | Places a new hedge stop order **1R above the current price** (a buy-stop cannot sit below the price); as soon as price is back below the original H, moves it down to **H** |
| Owner sets a **new stop-loss** on the main, or **drags a hedge order** | Moves both legs' pending hedges to that level (and removes the stop-loss again). Accepted only on the protective side of the entry; ignored while a hedge of that trade is filled (Q15) |

Every leg always has a protective order on the server: the hedge order (pending), the hedge's own stop (filled), or a stop-loss (after 1R).

#### 10.9.5 Protection, limits and closing (Q4, Q5, Q11, Q19)

| Rule | Trigger | TM action |
|---|---|---|
| Break-even (R2) | A leg is **1R in profit** from its own entry | **First** a server-side stop-loss at its entry on that leg, **then** cancels its hedge order. The leg can no longer lose |
| Trailing (R3) | Per-symbol switch, **off at first** | When switched on: 10.8.3 R3 (M5 lower highs). Needs the swing definition (open item) |
| Loss warning | A leg's loss (its own open P/L plus its hedges' realised and open P/L) reaches **2R** | Warning and pop-up |
| Loss limit | It reaches **3R** | Closes that leg and its hedge (cancels a pending one), accepting the loss; pop-up |
| Owner closes a leg (or its stop-loss / take-profit closes it) | | Removes that leg's hedge (cancel if pending, close if filled) and stop; the **other leg carries on**, still managed. Closing both = full exit; closing one = half off |

The 2R/3R values, 15 %, A, B and the spread floor are config values, to be tuned by the backtest (Phase 3).

#### 10.9.6 Start-up, restarts and news (Q13, Q20)

- **TM's own orders** (mirrors, hedges) are found again after a restart from their labels and linked to their main by its `positionId` (labels below).
- **A main TM did not see opened** (TM was down): **hedges only, no mirror**, plus a warning; if price is already beyond H, the hedge opens at market at once.
- **News and weekend:** a pop-up **10 minutes before each NFP/CPI release** (`news-releases.json`, shared with the Audit Bot) and **10 minutes before the Friday close** (17:00 New York), listing the open TM trades. No automatic action; hedge fills beyond H are logged with their slippage (Scenario 5).

#### 10.9.7 Labels, logs and notifications (Q17)

- **Labels** (Section 5.3): `TM-<6hex>-P<mainPositionId>-MIR` (mirror), `-HMAIN` (hedge of the main), `-HMIR` (hedge of the mirror), e.g. `TM-3f9a1c-P123456789-HMAIN`. The main's `positionId` in the label links every order to its trade across restarts; reconcile by bot name, as for every bot.
- **Logs:** per account and instance as for the Audit Bot (6.3): `events.csv`, plus `actions.csv` — one row per order TM sends or changes (time, trade, leg, action, side, volume, requested and filled price, slippage, latency, result).
- **Notifications:** a **reusable module in `Trader.Core`** (decision 15): one call for every bot; channels plug in behind it — **desktop pop-ups** on Ubuntu now (`notify-send`), Telegram or others later as one new class; a config file routes events to channels. Logs (journal, `events.csv`) always get everything. Pop-ups for: 2R warning, 3R close, mirror skipped or rejected, hedge rejected, TM stopped or disconnected, news / Friday warnings.

#### 10.9.8 Accepted risks

| Risk | Effect | Bounded by |
|---|---|---|
| Price jumps past H (news, gaps) | Hedge fills far beyond H; one jump can pass 3R | Pop-up before news / Friday close; slippage logged |
| Chop around H | Each hedge cycle costs ≈ B + spread, or adds up to 1R after an armed exit | 2R warning, 3R limit |
| TM down when the owner opens a trade without a stop-loss | Main unprotected until TM is back | Set a stop-loss on manual orders; TM hedges at once on start |
| Risk limits not enforced | Many mirrored trades can exceed leverage 1:10 / margin level 300 % | Warning only (owner's choice) |
| Mirror entry differs from the main's | A fraction of a pip | Logged in `actions.csv` |

## 11. Change log

Every change is recorded here, the version number in the header table is increased, and the change is committed to Git with a matching message.

| Version | Date | Change |
|---|---|---|
| v1.0 | 08-Oct-2026 | First issue. Decisions: TradingView for human interaction; cTrader Open API; C#; Audit Bot first on 2 brokers with per-broker logs and multi-instance awareness; Ubuntu; plan stored as .docx in Google Drive. |
| v1.1 | 09-Oct-2026 | Added Section 10: hedge-and-recover exit logic (no stop-loss; shorts as real trades, longs as hedge), Scenario 1 defined with worked example and net-position equivalent; Scenarios 2–5 listed. Decisions 9–10 added to Section 2. Sections 5.2, 7, 8 and 9 aligned to the hedge design. Open items and glossary updated. Sections renumbered (change log now 11). |
| v1.2 | 09-Oct-2026 | Scenario 1 illustrated with AUD/JPY daily chart (Figure 1). Working assumption H = E + ~10 pips. Worked example replaced with real levels (E 114.62, H 114.72, wick 114.97, T 110.00). Constraint on arming distance A added. |
| v1.3 | 10-Oct-2026 | Plan moved from .docx to Markdown in the private GitHub repo (`docs/plan.md`, chart in `docs/images/`); decision 6 updated. Added Section 10.8: 4-leg split structure, leg naming Tn-S(2n−1)/S(2n), lifecycle rules R1–R8 (protect at 1R, M5 trailing above last lower high with 21 EMA close, bot-opened pyramid adds on retracement to 21 EMA, rotation of even legs, runners), illustration, implementation notes, accepted risks. Hedge size confirmed per pair; 1R defined. Exposure cap for pyramiding added to Section 9. Decisions 11–12 added. Sections 4, 5.2, 7, 8, 10.1, 10.2, open items and glossary aligned. |
| v1.4 | 11-Oct-2026 | Phase 0 started: setup checklist `docs/setup.md` and check script `scripts/check-env.sh` added. Runtime changed from .NET 8 to .NET 10 LTS (decision 3, Section 7) because .NET 8/9 support ends 10-Nov-2026. IC Markets confirmed as Broker A (demo created). Open API endpoints and token lifetimes confirmed (Section 5.1); OpenAPI.Net library age noted as a risk with fallback. Open items and glossary updated. |
| v1.5 | 11-Oct-2026 | Phase 0 progress: Ubuntu toolchain ready (`check-env.sh` no failures; .NET 10 SDK, C# Dev Kit, runtime folders, secrets file; NTP fixed by using `pk.pool.ntp.org` as the default time servers are blocked on the home network — see setup.md 4.4). IC Markets demo confirmed Hedging, Raw Spread, USD, 1:500 (decision 8). Open API app "Trend Audit Bot" submitted (ID 43770, trading permission on own accounts). Change-log rows put in version order. Open items updated. |
| v1.6 | 11-Oct-2026 | Pepperstone chosen as Broker B (decision 8): cTrader demo created under the same cTrader ID, Hedging, USD, 1:200; account type (Razor) to confirm. `pepperstone.com` is blocked on the home network, sign-up via `secure.pepperstone.com` (setup.md Step 2). Open items updated. |
| v1.7 | 16-Oct-2026 | **Phase 0 complete.** Open API app "Trend Audit Bot" (ID 43770) approved; tokens issued with scope `trading` for the two demo accounts only (IC Markets 10124393, Pepperstone 5357135); live accounts not granted (Section 5.1). Client ID, secret and tokens stored in `~/.config/trader/secrets.env`; `check-env.sh` result: no failures, 0 warnings. OS recorded as Ubuntu 26.04 LTS (Section 7). Note added that IC Markets' cTrader entity here is Raw Trading Ltd (Seychelles FSA) — relevant to the live-broker choice. Open items updated. |
| v1.8 | 17-Oct-2026 | **Phase 1 started**; step list added (Section 8.1); step 1 (connect and list accounts) done, `ctidTraderAccountId`s recorded. Audit Bot symbol list fixed: AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD (6.2.1; crypto availability checked at both brokers). Probe windows fixed: London open, New York open, quiet hour 03:00 London, US NFP/CPI releases (6.2.2). Spread logging changed from every tick to one summary row per 2 min (main hours) / 5 min (off-peak) (6.2.3). All logs CSV for Google Sheets, with pips and USD per lot; `ticks.csv` → `spreads.csv`, `events.log` → `events.csv` (6.3.1). Decision 13 added. OpenAPI.Net finding added to 5.1. Open items and glossary updated. |
| v1.9 | 17-Oct-2026 | **Phase 1 step 2 done** (stay connected): event stream, dead-line detection by ping, auto-reconnect with backoff and re-auth, token refresh with atomic save and lock (Section 5.1, 8.1). Token expiry now stored in `secrets.env` as `CTRADER_ACCESS_TOKEN_EXPIRES_UTC`. Server heartbeat behaviour recorded. `events.csv` moved into step 3. Open item added: lost refresh reply. |
| v1.10 | 17-Oct-2026 | **Framework reusable for many bots** (new Section 5.3, decision 14): shared library `src/Trader.Core` with `BotRunner` (common options, logging, secrets, Ctrl+C/SIGTERM, exit codes), per-bot account choice, bot instance ID (`<BotName>-<6 hex>`) on every log line and in order labels (max 100 characters), usage guide `src/Trader.Core/README.md`, template `src/Trader.SampleBot`. Instance identity changed from broker + account + GUID to bot name + short ID (6.4); label format added to 10.8.2. 10 simultaneous bots tested. Open items and glossary updated. |
| v1.11 | 18-Oct-2026 | **Phase 1 step 3 done** (price feed to disk), 18-Oct-2026: per-broker config files (`config/audit.brokerA.json`, `audit.brokerB.json` → `~/tradebot/config/`); symbol lookup with name map; spot and M5 trendbar subscriptions renewed after every reconnect; `spreads.csv` and `events.csv` per broker/account/instance/day. Rules added to 6.2.3 (server timestamps, closed-market rows skipped by broker schedule, stale re-sent prices ignored) and 6.3.1 (lot size from symbol data, conversion symbols found automatically, median/p95 definitions). Crypto lot = 1 coin confirmed (open item closed). Second library finding (5.1): `PayloadDecoder` fallback for messages the library cannot decode. Shared parts in `Trader.Core` (MarketData, Csv, events.csv sink). |
| v1.12 | 18-Oct-2026 | **Order ownership rule** added (5.3): bot touches only orders/positions with its own label, closes by `positionId`, records its fills; foreign positions never modified but counted for risk; relies on Hedging accounts. **Phase 1 step 4 done** (instance registry, 6.4): owner chose **lock-file Primary** instead of promotion after the 30 s heartbeat timeout — instant takeover, never two Primaries on one machine; roles per account; slots for staggering; dead instances also detected by PID; registry in `Trader.Core` for all bots. |
| v1.13 | 18-Oct-2026 | **Phase 1 step 5 done** (probe orders, 6.2.2): owner accepted the recommendations — news windows 2 min before / 5 s after / 5 min after (NFP/CPI dates from BLS to Feb 2027 in `news-releases.json`); **open and close each a row** in `probes.csv` with new columns `action`, `commission_usd`, `position_id` (6.3.1); **backup stop-loss** on every probe (50 pips forex, 2 % crypto; note under the protection rule, Section 7). Probe procedure, left-over clean-up and first results recorded in 6.2.2. Order functions (market order, close by positionId, reconcile) in `Trader.Core`. Open item closed (news offsets); new one: dates after Feb 2027. |
| v1.14 | 18-Oct-2026 | **Send time in the order comment** (5.3, owner's choice; label unchanged). **Deployment** (Section 7): systemd user service running a published Release build from `~/tradebot/bin/AuditBot`, deploy script, laptop-sleep note. **Phase 2 audit run started** (Section 8) before step 6, by the owner's decision; Phase 1 test logs deleted first. Monitoring script deferred (open items). |
| v1.15 | 19-Oct-2026 | **Alerts and clearer probe summary** (new 6.2.4), owner's request after the first audit day: `SpreadWide`/`SpreadNormal` and `SlippageHigh` warnings with limits per broker in `audit.<broker>.json` (forex pips, crypto USD per lot, per-symbol overrides); probe summary per asset class with the worst fill named, replacing the single slippage average that mixed forex and crypto pips. First audit day: all 3 windows × 24 orders filled on both brokers; one connection drop (44 s, reconnected automatically). |
| v1.16 | 19-Oct-2026 | **Phase 1 step 6 done; Phase 1 complete.** Daily report tool `src/Trader.AuditReport` (new 6.5.1), owner accepted the recommendations: separate read-only program; systemd timer daily at 00:15 UTC making any missing reports (Section 7); UTC calendar days; one row per broker × symbol × period with both brokers on adjacent lines; `--from/--to` for the final summary; 18-Oct report built at once. Instance folders of one day merged. Disk layout (6.3) shows `summary-*.csv` and `bin/`. |
| v1.17 | 19-Oct-2026 | Pepperstone demo account type confirmed **Razor** (raw spread + commission) by the owner; open item closed. Both audited accounts are the raw-spread type. |
| v1.18 | 19-Oct-2026 | **First trading bot = TM (Trade Manually)** (new Section 10.9, decision 15), agreed question by question with the owner: owner opens positions by hand in cTrader; TM mirrors each and hedges both legs; hedge level from the owner's stop-loss or 15 % of the 3-day daily range (min 5 × spread); A = 1R, B = ½R, re-hedge rules; break-even at 1R; trailing off at first; 2R warning / 3R close; owner closes; TP copied; hedge level movable from the chart; both directions, both demos, six symbols; no risk-limit blocking (warnings only); hedges only for mains found after a restart; pop-ups before news / Friday close; reusable notifications module in `Trader.Core`. Ownership-rule exception for TM (5.3); Scenarios 2–5 answered for TM (10.7); Section 9 TM exception; roadmap Phase 4 starts with TM; open items and glossary. Audit Bot unchanged. |
| v1.19 | 20-Oct-2026 | **One page per bot** (owner's request): `docs/bots/` with an index, a template and pages for the Audit Bot and TM; Bots table in the README; short README in each bot's code folder; rule in 5.3 and CLAUDE.md. The plan stays authoritative for the rules. |

## 12. Open items / decisions pending

- ~~Which two brokers to audit~~: **IC Markets = Broker A** (v1.4), **Pepperstone = Broker B** (v1.6), both under the same cTrader ID. ~~Still to confirm: Pepperstone demo account type is Razor (raw spread + commission).~~ Done v1.17: **Razor** (owner, 19-Oct-2026) — both demos are raw spread + commission, so the audit compares like with like.
- ~~*(v1.4)* IC Markets demo: confirm Hedging mode and account type (Raw Spread vs Standard).~~ Done v1.5: Hedging, Raw Spread, USD, 1:500.
- ~~*(v1.4)* Open API application: submit, await Spotware approval, obtain tokens.~~ Done v1.7: approved, tokens for both demo accounts in the secrets file.
- *(v1.7)* Client secret was shared in a chat on 16-Oct-2026: regenerate it in the Open API portal (if offered) before any live account is granted (Phase 5).
- *(v1.7)* Live broker regulation: the IC Markets cTrader account is with **Raw Trading Ltd** (IC Markets Global, Seychelles FSA), a lighter regulator than Pepperstone's. Weigh this alongside Audit Bot results when choosing the live broker.
- TradingView plan level (webhooks need a paid tier) — decide before Phase 4b.
- ~~Final symbol list for the Audit Bot.~~ Done v1.8: AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD (Section 6.2.1).
- *(v1.10)* Simultaneous connections: Spotware publishes no limit per application or token. 10 bots at once worked on 17-Oct-2026 (45 s test); confirm with Spotware or a longer test before running many bots for days. Fallback if a limit appears: one connection process shared by several bots.
- *(v1.9)* Accepted risk: if the connection drops after the server has refreshed the tokens but before the reply arrives, the new pair is lost and the old refresh token no longer works. The bot then stops with a clear message; fix by getting new tokens in the Open API Playground. Rare (refresh about once a month).
- ~~*(v1.8)* Crypto lot for USD figures: confirm "1 lot = 1 coin" for BTCUSD/ETHUSD.~~ Done v1.11: confirmed from both brokers' symbol data.
- ~~*(v1.8)* News probes: confirm the offsets and fill in the NFP/CPI dates.~~ Done v1.13: 2 min before, 5 s after, 5 min after (owner accepted); dates to Feb 2027 in `news-releases.json`.
- *(v1.14)* Monitoring while the bot runs: a status script (`scripts/audit-status.sh`) with a systemd timer and desktop alerts, Claude only on alerts — deferred by the owner on 18-Oct-2026. *(v1.15)* The bot now logs `SpreadWide` / `SlippageHigh` warnings (6.2.4), which such a script can look for in `events.csv`.
- *(v1.14)* Test data: logs from Phase 1 testing were deleted before the audit run started (18-Oct-2026 ~17:10 UTC); everything in `~/tradebot/logs` from then on is real audit data.
- *(v1.13)* Add NFP/CPI dates after 11-Feb-2027 to `news-releases.json` (BLS publishes them yearly) if the Audit Bot is still running then.
- *(v1.18)* **TM build:** spec agreed (10.9); code starts when the owner says go. To check while building: the API can amend the stop-loss / take-profit of a manual position (`ProtoOAAmendPositionSLTPReq`) and of a filled hedge, on both brokers.
- *(v1.18)* TM parameters to tune in the backtest (Phase 3): 15 % of the 3-day range, spread floor 5×, A = 1R, B = ½R, 2R / 3R limits.
- Price-action entry rules (to be written as a spec in Phase 3). *(v1.18: not needed for TM — the owner chooses the entries.)*
- Hedge design: test H = E + ~10 pips as the working assumption; choose the arming distance A (must be smaller than typical wick extensions beyond Fh). Full-size hedge per pair: confirmed v1.3.
- Hedge design: define Scenarios 2–5 (Section 10.7).
- *(v1.3)* Fix the exact M5 swing definition for "last lower high" and the stop buffer (pips above the high). *(v1.18: also needed before TM's trailing switch is turned on.)*
- *(v1.3)* Confirm the 21 EMA retracement trigger: candle close at/above the EMA vs. a touch of the EMA.
- *(v1.3)* Confirm exposure-cap values (effective leverage 1:10, margin level 300%) after the Audit Bot shows real margin requirements.
- Account type: zero-commission (Standard, spread-only) vs raw-spread + commission for the hedge design — compare using Audit Bot data. *(v1.5: Broker A demo is Raw Spread.)* *(v1.17: Broker B demo is Razor — both raw spread + commission; a Standard account is not being audited.)*
- Protection if power/internet drops: confirm the server-side hedge order (before 1R) and stop-loss (after 1R) are sufficient, or add a wide emergency stop-loss as a last-resort backstop.
- Short-only approach: confirm instruments, and check swap direction for shorts on each one.

## 13. Glossary

| Term | Meaning |
|---|---|
| ECN / STP | Broker models that pass orders to external liquidity providers instead of trading against the client. |
| Raw spread | Interbank price with no mark-up; broker earns a separate commission. |
| cTrader Open API | Spotware's interface that lets external programs trade cTrader accounts over TLS using Protobuf messages. |
| Protobuf | Compact binary message format (Google Protocol Buffers). |
| OAuth | Token-based authorisation; the bot never stores your account password. |
| Slippage | Difference between requested price and actual fill price. |
| ATR (Average True Range) | The average size of a candle's range (high to low, including any gap from the previous close) over recent candles. TM uses the last 3 daily candles to size its default hedge distance. *(v1.18)* |
| TM, main, mirror | TM = Trade Manually bot (10.9). Main = the owner's manual position; mirror = TM's same-size copy of it. Each has its own hedge. *(v1.18)* |
| Trendbar | A candle (open, high, low, close) for a given timeframe. |
| Webhook | An HTTP POST sent automatically by one system (e.g. TradingView) to a URL when an event fires. |
| systemd service | Linux mechanism that keeps a program running in the background and restarts it on failure. |
| Heartbeat | A periodic "I am alive" signal; missing heartbeats mean the instance is dead. |
| Trailing stop | A stop-loss that follows price in your favour to lock in profit during a trend. |
| Hedge | An opposite-side position on the same symbol that freezes the open loss of the real trade. |
| Buy-stop / Sell-stop | Pending orders that open (or close) a position when price rises to / falls to a set level. |
| Arming distance (A) | How far price must move beyond the hedge fill before the bot places the hedge's exit. |
| Hedging vs netting account | Hedging allows long and short on the same symbol at once; netting keeps a single net position. |
| Whipsaw | A sharp move one way quickly followed by a sharp move back. |
| Swap | Overnight financing charged or paid for holding a position past the daily rollover. |
| 1R | A profit equal to the amount originally risked (here the distance from E to H). |
| Pyramiding | Adding to a winning position in steps as the trend proves itself. |
| Runner leg | The odd-numbered half of each trade, kept open on a trailing stop for the whole trend. |
| Rotating leg | The even-numbered half of each trade, closed in profit when the next pyramid add opens. |
| 21 EMA | Exponential moving average of the last 21 candle closes, weighting recent prices more heavily. |
| Lower high | In a downtrend, a swing high that is lower than the previous swing high. |
| Effective leverage | Total position value divided by account equity. |
| Margin level | Equity divided by used margin, as a percentage; low values lead to margin calls. |
| Markdown | A plain-text format where simple symbols (`#`, `|`, `**`) become headings, tables and bold text when displayed. |
| Commit | A saved snapshot of the repository with a short message describing what changed. |
| LTS (Long-Term Support) | A software release that receives security fixes for an extended period (3 years for .NET). |
| Access token / refresh token | OAuth keys: the access token lets the app act on granted accounts for a limited time; the refresh token is used only to obtain a new access token. |
| Client ID / Client Secret | The identity and password of the *application* (the bot), issued by Spotware when the Open API app is approved. |
| ctidTraderAccountId | The number the Open API uses for a trading account; different from the broker login number. |
| CSV | Comma-separated values: a plain-text table, one row per line, that spreadsheets open directly. |
| Pip | The standard price step for a symbol: 0.0001 for most forex pairs, 0.01 for JPY pairs; for crypto, set by the broker. |
| Lot | Standard trade size: 100,000 units of the base currency in forex (e.g. €100,000 for EURUSD). |
| Rollover | The daily change of trading day at 17:00 New York; spreads often widen around it. |
| NFP / CPI | US Non-Farm Payrolls (monthly jobs report) and Consumer Price Index (inflation); both released 08:30 New York and move markets strongly. |
| Library / project reference | A library is shared code with no program of its own (`Trader.Core`); a bot uses it through a project reference — one line in the bot's project file. |
| NuGet package | A versioned zip of compiled .NET code that projects can install; the way to share a library across repos. |
| Instance ID | The name of one running copy of a bot, e.g. `AuditBot-3f9a1c`; new on every start. |
| Market order | An order filled at once at the best available price, as opposed to a pending order waiting for a price. |
| Latency | Delay: here the time from sending an order to receiving its fill message. |
| Backup stop-loss | A stop-loss far from the price, held on the broker's server, that only matters if the bot fails to close a position itself. |
| Reconcile | Asking the server for all open positions and orders, to rebuild the bot's picture after a restart. |
| Lock file | A file one process holds exclusively open; Linux releases it automatically when that process ends, however it ends. Used to make exactly one instance Primary. |
| PID | Process ID: the number Linux gives each running program. |
| Foreign position | A position or order on the account without this bot's label — e.g. the owner's manual trade; never modified by the bot. |
| SIGTERM / SIGINT | Linux stop signals: SIGTERM is what systemd sends to stop a service; SIGINT is Ctrl+C. Bots stop cleanly on both. |
| Main hours / off-peak | Main hours: Mon–Fri from London open to New York close; off-peak: all other times. |
| Median / 95th percentile | The middle value / the value that 95 % of readings are at or below — shows typical and near-worst cases while ignoring rare extremes. |
