# Open Code Pieces

The reusable code every bot is built from: connection, tokens, orders, prices and candles, logs, the instance registry and notifications. Code folder: [`src/Trader.Core`](../src/Trader.Core) (usage guide with examples: [`src/Trader.Core/README.md`](../src/Trader.Core/README.md)). First built for the [Audit Bot](bots/audit-bot.md); [TM](bots/tm.md) added order functions, candles and notifications. A project of its own: a change here affects every bot, so all tests are run.

| Item | Value |
|---|---|
| Status | **In use** by the Audit Bot and TM |
| Document version | v1.5 (23-Oct-2026) — history in §10 |
| Code | [`src/Trader.Core`](../src/Trader.Core), template bot [`src/Trader.SampleBot`](../src/Trader.SampleBot) |
| Tests | [`tests/Trader.Core.Tests`](../tests/Trader.Core.Tests) |

## 1. What it is

The functions every bot needs are written **once**; bots never copy them. Each bot is its own small program that references this code (one line in its project file). A fix here reaches every bot at once, and all bots refresh tokens the same way, which matters because they share one secrets file.

## 2. The pieces

| Piece | What it gives a bot | Code | Used by |
|---|---|---|---|
| Start-up and shut-down | Common options, logging, secrets (demo only), clean stop on Ctrl+C / SIGTERM, exit codes, running several jobs together | `Hosting/BotRunner` | all |
| Identity and labels | Instance ID (`<BotName>-<6hex>`), order labels, parsing labels back | `Hosting/BotIdentity` | all |
| Instance registry | One Primary per bot per account; status files; slots (§6) | `Hosting/InstanceRegistry` | Audit Bot, TM |
| Connection | Stay connected: heartbeat, dead-line ping, reconnect with backoff, account auth, token refresh (§3); since when it has been down (`DownSinceUtc`); demo host or a replay broker on localhost (§3) | `OpenApi/ConnectionManager`, `CTraderSession.ConnectAsync` | all (down time: TM) |
| Requests | Market orders, **stop and limit orders (with stop-loss / take-profit for their position), moving and cancelling them, setting / removing a position's stop-loss (fixed or trailing) and take-profit**, closing by `positionId`, reconcile, symbols, spots, live trendbars, **historical candles** | `OpenApi/CTraderSession` | Audit Bot (market, close, reconcile), TM (all) |
| Secrets and tokens | Reads `~/.config/trader/secrets.env`; refreshes tokens and saves them atomically under a lock | `Config/SecretsFile`, `TokenRefresher` | all |
| Symbols and prices | Names → IDs and details (pip, lot, digits, schedule), currency conversion symbols, bid/ask quotes, trading hours | `MarketData/SymbolCatalog`, `PriceFeed`, `TradingSchedule`, `MarketHours` | Audit Bot, TM |
| Candles | Daily / historical candles, average true range (ATR) | `MarketData/Candle` | TM |
| News calendar | US release dates from `news-releases.json`, New York time → UTC | `MarketData/NewsCalendar` | Audit Bot, TM |
| Logs | Console with UTC times and instance ID; `events.csv` per account | `Logging/BotLogging`, `CsvEventSink` | all |
| CSV files | One file per UTC day; Google-Sheets formats; reading files back | `Csv/DailyCsvWriter`, `CsvFormat`, `CsvTable` | Audit Bot, report, TM |
| Notifications | Messages to the owner: always logged, then desktop pop-ups (more channels later) (§7) | `Notifications/Notifier` | TM |
| Folders | `~/tradebot/...` paths ([shared §5](shared.md#5-folders-and-files)) | `Hosting/TradebotPaths` | all |

Still to come when a bot needs it: respect the request-rate budget when many orders are modified at once (trailing stops on many legs).

## 3. Connection and tokens

- One Open API application registered on Spotware's Open API portal (client ID + secret). *(App "Trend Audit Bot", ID 43770, **approved**; access granted with scope `trading` to the two demo accounts only — IC Markets 10124393 and Pepperstone 5357135. Live accounts deliberately not granted; they get a fresh grant only when a go-live is decided.)*
- One cTrader ID (cTID) linked to both broker demo accounts; OAuth grants the app access to each account.
- .NET worker service using Spotware's official OpenAPI.Net library (NuGet `cTrader.OpenAPI.Net`) for the TCP/Protobuf connection.
- Demo and live use separate API hosts: `demo.ctraderapi.com:5035` and `live.ctraderapi.com:5035` (Protobuf over TLS; confirmed). One connection serves all demo accounts; a second serves all live accounts.
- **Replay environment** (21-Oct-2026, for [Rewind](https://github.com/ziash/Rewind), a separate project that replays past prices and speaks the Open API): `CTRADER_ENV=replay` with `CTRADER_HOST` and `CTRADER_PORT` in the secrets file. Only `localhost`, `127.0.0.1` or `::1` are accepted — checked when the file is read and again when connecting — so a replay setting can never reach a real broker; host and port are refused with `demo`, and `live` stays refused. The secrets file is chosen with `TRADER_SECRETS_FILE` (default `~/.config/trader/secrets.env`), so a bot run against Rewind never reads or rewrites the real tokens. Rewind's own certificate: since v1.5 (23-Oct-2026) the secrets file names Rewind's certificate authority with `CTRADER_CA_FILE` (replay only, refused with `demo`); the bot then connects with its own small client (`OpenApi/ReplayClient`) that trusts **that authority only** — not the system's list — and works on Linux and Windows alike; nothing is installed in the system. Spotware's library cannot do this (it accepts only what the system trusts, with no way to add a certificate); demo connections still use it, unchanged. Without `CTRADER_CA_FILE` a replay connection uses Spotware's library as before, trusted through `SSL_CERT_FILE` (Linux only; the v1.2 way). Rewind writes the secrets file and a launcher that sets all of this (`~/rewind/tm/run-tm.sh` on Linux, `run-tm.ps1` on Windows).
- Access token lasts about 30 days; the bot renews it with the refresh token and must save the new refresh token each time (the old one is invalidated).
- *(step 2)* Token refresh runs over the same Protobuf connection (`ProtoOARefreshTokenReq`), when the token has under 7 days left, when its expiry is unknown, or when the server rejects it. The new pair and its expiry (`CTRADER_ACCESS_TOKEN_EXPIRES_UTC`) are written to `secrets.env` atomically (temp file, then rename; mode 600), under a lock file (`secrets.env.lock`) so only one instance refreshes at a time. First refresh 17-Oct-2026: the server returned a lifetime of about 30 days 10 h (expires 16-Nov-2026).
- *(step 2)* Heartbeats: the bot sends one at least every 10 s when idle; the server sends its own only after about 30 s without traffic. Dead-line rule: after 40 s with nothing received the bot sends a ping (`ProtoOAVersionReq`); no reply within 10 s → reconnect. Reconnect waits 1, 2, 4 … up to 60 s (+ up to 20 % random jitter), then authenticates the app and every account again.

> **Library risk:** `cTrader.OpenAPI.Net` v1.4.4 was last published in 2022 and targets .NET 6, with old dependencies. It runs on .NET 10, so the Audit Bot was built with it. Fallback if it causes problems: generate C# classes from Spotware's `.proto` files and write a thin TLS client.
>
> *Finding (Audit Bot step 1, 17-Oct-2026):* the library works on .NET 10 — app auth, account list, account auth and trader info all succeed on both demo accounts. Its message definitions date from 2022 and already miss at least one newer field (`brokerTitleShort` in the account list). Not a blocker today; if a later step needs newer fields, switch to the fallback.
>
> *Finding (Audit Bot step 3, 18-Oct-2026):* the library's message factory cannot decode some replies — e.g. `ProtoOASubscribeLiveTrendbarRes` (payload type 2165) — so those requests timed out. Fixed in `Trader.Core` (`PayloadDecoder`): when the factory returns nothing, the message is decoded from a table built from the library's own generated message classes (84 types). Still no need for the full fallback.

## 4. One framework, many bots, and the rules every bot follows

The owner may run up to ~10 bots at the same time. The pieces of §2 are therefore written **once**, in `src/Trader.Core`, and every bot uses it; bots never copy that code. Usage guide: [`src/Trader.Core/README.md`](../src/Trader.Core/README.md).

- **One project per bot:** `src/Trader.<BotName>/`, referencing `Trader.Core`. A new bot starts as a copy of `src/Trader.SampleBot` (the smallest complete bot, ~30 lines); the steps are in the usage guide.
- **`BotRunner`** gives every bot the same start-up and shut-down: common options (`--account`, `--verbose`, `--test-drop-after`, `--help`), logging, secrets (demo only), clean stop on Ctrl+C / SIGTERM, exit codes (0 normal, 1 fatal, 2 bad options, 3 bug).
- **Accounts per bot:** each bot chooses its accounts (`--account <ctid>`, or a default in code); default is all demo accounts the token grants.
- **Identity:** each running bot gets an instance ID = bot name + 6 hex characters, e.g. `AuditBot-3f9a1c`, shown on every log line.
- **Order labels:** instance ID, optionally followed by the leg name (e.g. [Auto Bot §7.2](bots/auto-bot.md#72-leg-naming-convention)), e.g. `TrendBot-3f9a1c-T1-S1`; at most 100 characters (Spotware limit). The instance ID changes on every restart but positions do not, so after a restart a bot recognises its own positions by **bot name** (plus account and leg) and uses the instance ID only to attribute fills and log lines.
- **Send time in the order comment** *(owner's choice)*: every bot order carries `sent <yyyy-MM-dd HH:mm:ss.fff>Z` (UTC, milliseconds) in its `comment` field, shown in cTrader next to the label and kept by the server, next to the server's own open time (ms). The label stays the identity for ownership. Considered and not chosen: `BotName-YYYYMMDD-HHMMSS` as the label (not unique when several orders go out in the same second, e.g. a 4-leg trade; loses the instance ID).
- **Connections:** each bot has its own connection (own 50 req/s budget). One secrets file serves all bots; when one refreshes the tokens, the others pick up the new ones (§3).
- **Tested 17-Oct-2026:** 10 bots at once on separate connections, all authenticating the same two demo accounts, ran without errors or dropped sessions. Spotware does not publish a limit on simultaneous connections (§9).
- **Order ownership rule** — how a bot tells its own orders from the owner's manual trades and anything else on the account:
  - Every bot order carries its label (above) in the order's `label` field; the server keeps it on the order and the position and returns it in every execution event and reconcile. Manual trades normally have no label.
  - The bot also records the `positionId` of every fill it receives, as a second, independent check during a run. After a restart it recognises its positions by **bot name** in the label.
  - A bot **only modifies or closes positions and orders that carry its own label**, and always closes **by `positionId`**, never "close all on a symbol".
  - Everything else is **foreign** — the owner's manual trades, other apps with access to the account, broker actions such as a stop-out: never modified, logged to `events.csv` when seen, but **counted for risk** (margin, exposure), because it uses the same account money.
  - This works because the accounts are **Hedging** ([shared §2](shared.md#2-brokers-and-accounts)): every order is its own position, so a manual trade and a bot trade on the same pair and side never merge. Other clients of the same broker are other accounts, which the bot cannot see at all.
  - **Exception — TM** *(owner's decision)*: TM takes over unlabelled positions on the symbols listed in its config (the owner's manual trades meant for TM): it removes or copies their stop-loss and take-profit, and closes one only under its 3R loss limit ([TM §5](bots/tm.md#5-protection-limits-and-closing-q4-q5-q11-q19)). Unlisted symbols and other bots' positions stay foreign to TM.
  - Limit: the label is plain text, so someone with access to the account could type a bot-style label on a manual order. The recorded `positionId`s catch this during a run; if manual labels are ever used, the post-restart rule becomes "label matches **and** `positionId` is in the bot's saved list".

## 5. Adding a bot

Copy `src/Trader.SampleBot` (the smallest complete bot, ~30 lines) to `src/Trader.<BotName>`, rename it, and build on it — steps in the [usage guide](../src/Trader.Core/README.md). Then give it its own project document from [`docs/bots/_template.md`](bots/_template.md) and a row in the [bot index](bots/README.md).

## 6. Instance registry: one Primary per bot per account

Each running copy (instance) of a bot knows how many copies of the same bot are running, per account, and behaves sensibly. Built for the Audit Bot; every bot gets it from `InstanceRegistry`.

- **Identity:** on start, an instance creates an ID = bot name + short random ID (e.g. `AuditBot-3f9a1c`, §4) and writes it into its log path; broker and account are already in that path and in the registry file name. *(was broker + account + short GUID)*
- **Registry:** it writes a heartbeat file in `run/instances/` every 10 seconds (a status file overwritten in place, not a log) (PID, broker, account, symbols, role, last-seen time). Files older than 30 seconds are treated as dead and cleaned up. One file per account: `<broker>-<ctidTraderAccountId>-<instanceId>.json`, written to a temp file then renamed. A file is also removed at once when its PID is no longer running on this machine (e.g. after `kill -9`).
- **Discovery:** each instance reads the registry to know the total count, and the count per broker/account.
- **Tactful behaviour:**
  - Different brokers → instances run fully independently.
  - Same broker + same account → the first instance is Primary (may send orders); later ones become Passive (watch only) to avoid duplicate trades. **Primary = the instance holding the exclusive lock file** `<BotName>-<ctidTraderAccountId>.primary.lock` in `run/instances/`. Roles are **per account**: one process can be Primary on IC Markets and Passive on Pepperstone. 
  - Scheduled actions can be staggered by a few seconds between instances so they never fire together (the Audit Bot does this for its probes). Each instance gets a **slot** = its position among live instances of the same bot on the account, oldest first.
  - Every order carries a label with the instance ID, so fills are attributed to the correct instance.
  - ~~If the Primary dies, the oldest live Passive instance promotes itself after the heartbeat timeout.~~ *(owner's choice)* If the Primary stops, Linux releases its lock at once — even on `kill -9` or a crash — and the first Passive instance to take the lock (checked every 2 s) becomes Primary. There can never be two Primaries on one machine. Tested 18-Oct-2026: Primary killed with `kill -9`, takeover within 1 s.
- **Visibility:** each instance logs the current sibling count on start, on change, and hourly. Event `Siblings` in `events.csv` (list of instances with roles, own role and slot); `Promoted` when an instance becomes Primary; `InstanceGone` when a dead instance's file is removed.
- The registry is in `Trader.Core` (`InstanceRegistry`), so every bot gets the same "one Primary per bot per account" rule — a trend bot and the Audit Bot each have their own Primary on the same account. Limit: it coordinates instances on **one machine**; an account must be run from one machine only.

## 7. Notifications

`Trader.Core/Notifications` — `bot.Notify.Info/Warning/Critical(event, title, message, account)`; every call is logged (journal and `events.csv`), then queued to the channels whose filter in `~/tradebot/config/notifications.json` accepts it (`minLevel`, `bots`, `events`, `excludeEvents`, `enabled`; no file = desktop pop-ups from Warning). The same message within 60 s is sent once; a failing channel is logged once and never blocks the bot. Desktop = `notify-send`; Critical stays on screen. Any bot can test with `--test-notification`.

Pop-ups for warnings and above by default; routing per channel by level, bot and event in `~/tradebot/config/notifications.json` (reference copy [`config/notifications.json`](../config/notifications.json)). A new channel (e.g. Telegram) is one class implementing `INotificationChannel` plus one line in `NotificationChannels`; no bot changes. Pop-ups work from a terminal and from a systemd user service while the owner is logged in to the desktop (tested 20-Oct-2026).

## 8. CSV files

Every bot writes its logs as CSV in the shared format ([shared §5](shared.md#5-folders-and-files)): `DailyCsvWriter` (one file per UTC day, header, flushed per row), `CsvFormat` (times and numbers), `CsvTable` (reading the files back by column name, e.g. for the Audit Bot's report).

## 9. Open items

- Simultaneous connections: Spotware publishes no limit per application or token. 10 bots at once worked on 17-Oct-2026 (45 s test); confirm with Spotware or a longer test before running many bots for days. Fallback if a limit appears: one connection process shared by several bots.
- Accepted risk: if the connection drops after the server has refreshed the tokens but before the reply arrives, the new pair is lost and the old refresh token no longer works. The bot then stops with a clear message; fix by getting new tokens in the Open API Playground. Rare (refresh about once a month).
- Outside this repo: pack the code as a versioned NuGet package in `~/tradebot/packages` and reference a fixed version, rather than copying the folder.
- Request-rate budget for bots that modify many orders at once (§2).

## 10. History

| Version | Date | Change |
|---|---|---|
| — | 16–18 Oct 2026 | (old plan v1.8–v1.14) Connection, reconnect, token refresh; made reusable for many bots (`BotRunner`, identity, labels, template bot); symbols, prices, CSV; ownership rule; instance registry with lock-file Primary; market orders, close, reconcile; send time in the comment |
| — | 20 Oct 2026 | (old plan v1.20–v1.21) Notifications; stop orders, amend, cancel, SL/TP, historical candles, ATR (for TM) |
| v1.0 | 20 Oct 2026 | Own project document, split out of the old plan ([archive](archive/plan-v1.22.md), Sections 5, 6.4, 10.9.7) under the name **Open Code Pieces**. Content unchanged |
| v1.1 | 20 Oct 2026 | News calendar moved here from the Audit Bot (now used by TM too); `ConnectionManager.DownSinceUtc` for TM's disconnect pop-up |
| v1.2 | 21 Oct 2026 | Replay environment (§3): `CTRADER_ENV=replay`, localhost only, for Rewind; `CTraderSession.ConnectAsync` takes the secrets; tested with `Trader.ListAccounts` and TM against Rewind |
| v1.3 | 22 Oct 2026 | `AmendPositionProtectionAsync(…, trailingStopLoss:)`: optional; true = the server trails the stop-loss, false = fixed, not given = not sent (every bot unchanged). For TM's trailing break-even stops ([TM §5](bots/tm.md#5-protection-limits-and-closing-q4-q5-q11-q19)) |
| v1.4 | 22 Oct 2026 | `PlacePendingOrderAsync` (stop or limit, absolute stop-loss / take-profit) and `AmendPendingOrderAsync` (price, volume, stop-loss, take-profit together), for TM's mirror orders ([TM §3.1](bots/tm.md#31-entry-by-a-pending-order-owner-22-oct-2026)). Existing calls unchanged |
| v1.5 | 23 Oct 2026 | Replay on Windows (§3): optional `CTRADER_CA_FILE` (replay only) names the replay broker's certificate authority; `ReplayClient` (own TLS client, trusts only that authority) is used for it instead of Spotware's `OpenClient`, so TM runs against Rewind on Windows too without importing a certificate. Demo connections unchanged |
