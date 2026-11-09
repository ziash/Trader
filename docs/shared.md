# Shared by all projects

Each bot is a project of its own, with its own document in [`docs/bots/`](bots/README.md); all documents: [documents map](README.md). This page holds only what all of them share: the brokers and accounts, the machine, the safety rules, the folders and file format, how the documents are kept, and the glossary. The reusable code has its own document: [Open Code Pieces](open-code-pieces.md).

| Item | Value |
|---|---|
| Owner | Zeeshan |
| Document version | v1.16 (09-Nov-2026) — history in §10 |
| Setup record | [setup.md](setup.md) (environment, accounts, Open API app; check with `bash scripts/check-env.sh`) |
| Archive | [The old single plan, v1.0–v1.22](archive/plan-v1.22.md) — split into these documents on 20-Oct-2026 |

## 1. The projects

The projects — what each one is, its status and every document that belongs to it — are listed in one place only: the [projects table on the front page](../README.md#projects). The full list of documents is the [documents map](README.md). In short: the bots **Scout**, **TM**, **Audit Bot** (finished) and **Auto Bot** (concept), each in [`bots/`](bots/README.md); the **Dashboard** in [`dashboard/`](dashboard/README.md); the **Open Code Pieces** in [open-code-pieces.md](open-code-pieces.md).

## 2. Brokers and accounts

| Item | Value |
|---|---|
| Broker A | **IC Markets** (Raw Trading Ltd) demo **10124393** — Hedging, Raw Spread, USD, 1:500 — `ctidTraderAccountId` 49123893 — folder name `brokerA` |
| Broker B | **Pepperstone** demo **5357135** — Hedging, Razor (raw spread + commission), USD, 1:200 — `ctidTraderAccountId` 49124666 — folder name `brokerB` |
| Why these | Raw spreads, cTrader, crypto CFDs, regulated groups (chosen from Pepperstone, IC Markets, FP Markets, Fusion); both under the same cTrader ID, so one token covers both |
| Open API app | "Trend Audit Bot", ID 43770, scope `trading`, granted to the two demo accounts only |
| Endpoints | Demo `demo.ctraderapi.com:5035` (used); live `live.ctraderapi.com:5035` — **not used** (§4) |
| Hedging accounts | Every order is its own position, so manual trades and bot trades on the same symbol never merge — the ownership rule depends on it ([Open Code Pieces §4](open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)) |

Broker login numbers differ from the `ctidTraderAccountId` the API uses (`dotnet run --project src/Trader.ListAccounts` lists them). A Pepperstone **live** account (1398129, Razor, unfunded) was created by mistake during setup; it is not used and not funded.

## 3. Machine and services

| Component | Choice |
|---|---|
| OS | Ubuntu 26.04 LTS (current machine: HP laptop) |
| Runtime / SDK | .NET 10 LTS SDK from Ubuntu's archive (`dotnet-sdk-10.0`); runtime only on the VPS later |
| Editor | VS Code + C# Dev Kit (AI-assisted coding) |
| Secrets | API client secret and tokens in `~/.config/trader/secrets.env` (permissions 600) — never in Git |
| Setup check | `scripts/check-env.sh` — read-only check of OS, .NET, Git, VS Code, clock, port 5035, folders, secrets |
| Version control | Private Git repository [github.com/ziash/Trader](https://github.com/ziash/Trader) — the project documents and the code |
| Clock | NTP sync enabled; all logs in UTC |
| Laptop during the audit | Automatic suspend off (Settings → Power) for **both** "Plugged In" and "On Battery Power" — on 27-Oct-2026 only plugged-in was off, and on battery the laptop suspended after 15 min (24–26 Oct gap, [Audit Bot §7](bots/audit-bot.md#7-build-steps-and-the-audit-run)); lid closing must not suspend; mains power. A sleeping laptop records nothing and probe windows are logged as missed. User services start at login (`Linger=no` on 27-Oct-2026); to run without anyone logged in: `loginctl enable-linger` (owner's choice). |
| Later | Small Linux VPS near broker servers (London / New York); same container |

**Bots run as systemd user services** (one unit per bot), each from a **published Release build** in `~/tradebot/bin/<Bot>/`, so code changes never affect a running bot until it is redeployed and restarted. Each bot's document says how it is run.

**Deploying never touches a running bot's files** (since v1.6). `scripts/deploy-<bot>.sh` publishes each build into a new folder `~/tradebot/bin/<Bot>-<git hash>` (`-dirty` when the repository had uncommitted changes; `-2`, `-3` … when that name exists) and then switches the link `~/tradebot/bin/<Bot>` to it in one step; the units start the bot through that link (shared code: `scripts/deploy-lib.sh`). The .NET host follows the link once, at start, so a running bot keeps loading from its own folder and switches only when it is restarted (tested 04-Nov-2026). The scripts keep the newest three builds plus any folder a running process uses, and still never start, stop or restart a bot. Before, they published into a fresh folder but renamed the live one away and deleted the previous one, so a bot that loaded a file late (often while stopping) read it from the new build or found it gone: Scout's crash on stop on 04-Nov-2026 ([Scout §7](bots/scout/scout.md#7-decisions-and-open-items)). **One-time switch:** a bot still running from a plain `~/tradebot/bin/<Bot>/` folder is left alone; its next deploy prints a single command that stops it, renames the folder, makes the link and starts it again (a few seconds). Not covered: a .NET runtime update from Ubuntu changes the shared runtime under every running bot ([Audit Bot](bots/audit-bot.md) v1.4) — restart the bots after one.

## 4. Safety rules for every bot

- **Demo only.** No bot connects to the live endpoint or places orders on a live account until a go-live is decided and recorded in that bot's document. A go-live means: a small live account, strict risk limits, a VPS near the broker servers, and a review after one month. The code refuses `live` unless explicitly enabled by the owner.
- **Replay** (`CTRADER_ENV=replay`) connects only to a broker simulator on this computer ([Rewind](https://github.com/ziash/Rewind), a separate project), with its own secrets file of fake values (`TRADER_SECRETS_FILE`); the code refuses any other host ([Open Code Pieces §3](open-code-pieces.md#3-connection-and-tokens)).
- **Secrets** live only in `~/.config/trader/secrets.env` (permissions 600), never in Git, logs or chat; only "set / not set" or a masked form may be shown. The refresh token is replaced on every refresh and written back atomically by the shared code only.
- **Protection rule:** because home power or internet can drop, every live position must be protected by an order held on the broker's server — a hedge order, a stop-loss, or (for the Audit Bot's one-second probes) a wide backup stop-loss. Each bot's document says how. **A hedge order alone is not enough**: it needs free margin when it triggers, and a refused one is simply gone, so TM keeps a backstop stop-loss behind it ([tm.md §5.1](bots/tm.md#51-margin-and-the-backstop-v19-06-nov-2026), [issue-log.md #1](issue-log.md#1-ic-markets-demo-in-a-margin-call-06-nov-2026)).
- **Margin:** a bot that adds exposure reads the account status first (balance, equity, margin level: `AccountStatus`, [Open Code Pieces](open-code-pieces.md) v1.9) and holds back when margin is short (TM §5.1, Scout §2.5). What went wrong and why: [issue-log.md](issue-log.md).
- **Ownership:** a bot touches only its own orders and positions, by `positionId` — with one recorded exception, TM ([Open Code Pieces §4](open-code-pieces.md#4-one-framework-many-bots-and-the-rules-every-bot-follows)).
- **Orders only from the Primary** copy of a bot on an account ([Open Code Pieces §6](open-code-pieces.md#6-instance-registry-one-primary-per-bot-per-account)).

## 5. Folders and files

Runtime data lives outside the repo:

```
~/tradebot/
  config/                          live config (reference copies in the repo's config/)
    audit.brokerA.json, audit.brokerB.json                        Audit Bot
    tm.json                                                       TM
    news-releases.json                                            Audit Bot, TM (US NFP/CPI dates)
    notifications.json                                            all bots (Open Code Pieces §7)
    scout-controls.json                                           Dashboard: options the web may set for Scout (dashboard.md §3.4)
  control/                         Dashboard → Scout: <bot>-<broker>-<ctid>.json (your options, fetched every minute), .ack.json (Scout's reply); scout-halt, scout-reset-… (the owner's stop and reset, scout.md §2.7)
  dashboard/                       Dashboard: outbox/ (the next upload: allowlisted copies + status.json, sent every 30 min), push.lock (dashboard.md §3.1)
  run/instances/                   instance registry: <broker>-<ctid>-<instanceId>.json, <Bot>-<ctid>.primary.lock
  run/tm/<broker>-<ctid>.json      TM's remembered trades
  logs/<broker>/<ctid>/<instanceId>/<yyyy-MM-dd>/
                                   one folder per bot copy (AuditBot-…, TM-…) and UTC day:
                                   events.csv (every bot), spreads.csv + probes.csv (Audit Bot), actions.csv (TM)
                                   Dashboard/<yyyy-MM-dd>/ beside the bot copies (fixed name): account.csv, deals.csv, conditions.csv (dashboard.md §3.3)
  reports/                         Audit Bot reports
  bin/<Bot> → <Bot>-<git hash>/    link to the published build the service runs; the last few builds beside it (§3)
~/.config/trader/secrets.env       secrets (600)
~/.config/trader/hostinger_ed25519, hostinger_known_hosts, dashboard.env, dashboard-m2m.env   Dashboard upload key and settings, Claude's M2M secret (600)
```

**CSV format** (every bot's logs and reports):

All logs and reports are CSV files that open directly in Google Sheets, Excel or LibreOffice and import into other tools:

- UTF-8, comma-separated, one header row, one record per line; text quoted only when it contains a comma or quote.
- Times in UTC as `yyyy-MM-dd HH:mm:ss` (Google Sheets reads this as a date-time); the column name says `_utc`.
- Numbers with a `.` decimal point and no thousands separators; units in the column name (`_pips`, `_usd`, `_ms`, `_s`).
- One file per day (UTC) per broker / account / instance, path as above.

## 6. How the documents are kept

- **One document per project** — each bot in `docs/bots/<bot>.md` (from [`_template.md`](bots/_template.md)); a project with several documents has its own folder with a short `README.md`: Scout in [`docs/bots/scout/`](bots/scout/README.md), the Dashboard in [`docs/dashboard/`](dashboard/README.md). Plus this page and [Open Code Pieces](open-code-pieces.md). Each has its own **version** (v1.0, v1.1 …) and **history** table. A change of decision, scope or design goes into the document it belongs to: bump its version, add a history row, commit as `<doc> vX.Y: <what changed>` (e.g. `tm v1.1: news pop-ups`).
- The **README** on the front page holds the one projects table: each project's status and links to all its documents. The [documents map](README.md) lists every document; the [bot index](bots/README.md) and the project folders' `README.md` link their own documents only, without a status, so nothing is kept twice.
- Code comments point at the document sections, e.g. `(tm.md §4)`.
- Earlier history: [the old single plan](archive/plan-v1.22.md) (v1.0–v1.22, 8–20 Oct 2026).

## 7. Decisions for all projects

| # | Area | Decision | Why |
|---|---|---|---|
| 1 | Bot ↔ broker | cTrader Open API | Native on Linux; direct to the broker; no Windows needed |
| 2 | Language | C# on **.NET 10 LTS** | Best-documented cTrader route; runs natively on Linux; .NET 10 is supported to Nov 2028 and is in Ubuntu's own archive (.NET 8 and 9 end on 10 Nov 2026) |
| 3 | Machine | Ubuntu (home laptop → Linux VPS later) | Existing machine; same code moves to a VPS |
| 4 | Documents | Markdown in the private GitHub repo next to the code; Word/PDF exported when needed | Full change history next to the code |
| 5 | Shared code | All bots use the [Open Code Pieces](open-code-pieces.md) (`src/Trader.Core`); no copied connection code | A fix reaches every bot at once; all bots refresh tokens the same way |
| 6 | Each bot is its own project (20-Oct-2026, owner) | Own document, version, decisions, open items and history | Bots differ in purpose and pace; one plan for all mixed them up |

## 8. Open items

- Client secret was shared in a chat on 16-Oct-2026: regenerate it in the Open API portal (if offered) before any live account is granted.
- Live broker regulation: the IC Markets cTrader account is with **Raw Trading Ltd** (IC Markets Global, Seychelles FSA), a lighter regulator than Pepperstone's. Weigh this alongside the Audit Bot's results when choosing the live broker. **Chosen 06-Nov-2026: IC Markets** (owner, [Audit Bot §9](bots/audit-bot.md#9-open-items)), with this weighed in; to be looked at again before a live account is opened.

## 9. Glossary

| Term | Meaning |
|---|---|
| ECN / STP | Broker models that pass orders to external liquidity providers instead of trading against the client. |
| Raw spread | Interbank price with no mark-up; broker earns a separate commission. |
| cTrader Open API | Spotware's interface that lets external programs trade cTrader accounts over TLS using Protobuf messages. |
| Protobuf | Compact binary message format (Google Protocol Buffers). |
| OAuth | Token-based authorisation; the bot never stores your account password. |
| Slippage | Difference between requested price and actual fill price. |
| ATR (Average True Range) | The average size of a candle's range (high to low, including any gap from the previous close) over recent candles. TM uses the last 3 daily candles to size its default hedge distance. |
| TM, main, mirror | TM = Trade Manually bot ([TM](bots/tm.md)). Main = the owner's manual position; mirror = TM's same-size copy of it. Each has its own hedge. |
| Trendbar | A candle (open, high, low, close) for a given timeframe. |
| Equity, margin, margin level | Equity = balance + the open positions' profit or loss. Margin = the deposit the broker holds for open positions; free margin = equity − margin, what new orders (and hedges) can use. Margin level = equity ÷ margin × 100 %. IC Markets calls margin at 100 % and closes positions itself at 50 % (stop-out). |
| Lock-step | A replay where each program waits for the other: the replay moves one price step, the bot acts and says it has finished, and only then does the replay move on. Years of history run in minutes, with the same result every time. |
| Correlated group | Symbols that tend to move together, such as gold and silver, so that losing on one often means losing on the other. Scout limits the money at risk per group ([scout.md §2.9](bots/scout/scout.md)). |
| Backstop | TM's stop-loss at the 3R loss limit on a leg that has no filled hedge, so the leg keeps a stop on the server even when a hedge is refused ([tm.md §5.1](bots/tm.md#51-margin-and-the-backstop-v19-06-nov-2026)). |
| Webhook | An HTTP POST sent automatically by one system (e.g. TradingView) to a URL when an event fires. |
| systemd service | Linux mechanism that keeps a program running in the background and restarts it on failure. |
| Link (symbolic link) | A small file that points at another file or folder by name; opening the link opens the target. Switching it to another target is one step, so a program started through it gets either the old or the new build, never a mix. |
| Heartbeat | A periodic "I am alive" signal; missing heartbeats mean the instance is dead. |
| Trailing stop | A stop-loss that follows price in your favour to lock in profit during a trend. In cTrader it is held by the broker's server (`trailingStopLoss = true` on the position): the stop keeps the distance it had when trailing was switched on and never moves back. |
| Hedge | An opposite-side position on the same symbol that freezes the open loss of the real trade. |
| Buy-stop / Sell-stop | Pending orders that open (or close) a position when price rises to / falls to a set level. Placed beyond the current price: a breakout (buy-stop above) or breakdown (sell-stop below) entry; TM's hedges are buy-/sell-stops. |
| Buy-limit / Sell-limit | Pending orders placed *before* the price: a buy-limit below it fills when price falls to it (buy a pullback cheaper), a sell-limit above it when price rises to it (sell a rally). They fill at their price or better. A pending order may carry a stop-loss and take-profit that its position gets when it fills. |
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
| Library / project reference | A library is shared code with no program of its own (`Trader.Core`, the [Open Code Pieces](open-code-pieces.md)); a bot uses it through a project reference — one line in the bot's project file. |
| NuGet package | A versioned zip of compiled .NET code that projects can install; the way to share a library across repos. |
| Instance ID | The name of one running copy of a bot, e.g. `AuditBot-3f9a1c`; new on every start. |
| Market order | An order filled at once at the best available price, as opposed to a pending order waiting for a price. |
| Latency | Delay: here the time from sending an order to receiving its fill message. |
| Backup stop-loss | A stop-loss far from the price, held on the broker's server, that only matters if the bot fails to close a position itself. |
| Planner | TM's rule function: from the account's positions, orders and prices it lists the changes the rules call for; run every second, so anything missed is redone next time ([TM §9](bots/tm.md)). |
| State file | `~/tradebot/run/tm/<broker>-<ctid>.json`: what TM remembers about each trade (hedge level, R, hedge results, steps done) so a restart carries on. |
| `TRADING_BAD_STOPS` | The broker's refusal of a stop-loss that is already past the current price (e.g. a buy position's stop above the bid). |
| Reconcile | Asking the server for all open positions and orders, to rebuild the bot's picture after a restart. |
| Lock file | A file one process holds exclusively open; Linux releases it automatically when that process ends, however it ends. Used to make exactly one instance Primary. |
| PID | Process ID: the number Linux gives each running program. |
| Foreign position | A position or order on the account without this bot's label — e.g. the owner's manual trade; never modified by the bot. |
| SIGTERM / SIGINT | Linux stop signals: SIGTERM is what systemd sends to stop a service; SIGINT is Ctrl+C. Bots stop cleanly on both. |
| Main hours / off-peak | Main hours: Mon–Fri from London open to New York close; off-peak: all other times. |
| Median / 95th percentile | The middle value / the value that 95 % of readings are at or below — shows typical and near-worst cases while ignoring rare extremes. |
| Open Code Pieces | The reusable code every bot is built from (`src/Trader.Core`): [open-code-pieces.md](open-code-pieces.md). |
| Project document | Each bot's own document in `docs/bots/`: goal, rules, decisions, open items and history, with its own version number. |
| SSH / rsync | SSH: an encrypted connection to another computer, here signed in with a key file instead of a password. rsync: a copy tool that sends only the files (and parts) that changed. The dashboard's upload uses both. |
| PHP | The programming language web hosts such as Hostinger run for web pages; the dashboard's API (sign-in, data, controls) is written in it. |
| React / Next.js | React: a way to build web screens from reusable pieces (components), in TypeScript. Next.js: a framework around React; the dashboard uses it to turn its screens into plain files that any web host can serve. |
| Subdomain | A name under a domain, e.g. trader.uzlabz.com under uzlabz.com, with its own folder of pages. |
| HTTPS / SSL certificate | Encrypted web traffic; the certificate proves the site is the real one (the padlock in the browser). |
| API / JSON | API: an address programs call instead of reading pages. JSON: the plain-text format of its answers (and of the bots' config files). |
| Sign in with Google (OAuth / OpenID Connect) | Google checks the person and tells the site only their verified e-mail address; the site keeps no passwords. |
| M2M sign-in | Machine-to-machine: a program (e.g. Claude) signs in with a client ID and secret and gets a short-lived token, instead of a person signing in. |
| Hash | A one-way scrambled form of a secret: enough to check it, impossible to read it back. |
| Session cookie | A small token the browser keeps after sign-in and sends with each page, so you stay signed in. |
| Options catalogue | The laptop's list of options the dashboard may set for a bot, with the values allowed (dashboard.md §3.4). |
| Outbox (staging folder) | A folder where files are copied and checked before they are sent; only what is in it leaves the laptop. The dashboard's upload uses `~/tradebot/dashboard/outbox/` (dashboard.md §3.1). |

## 10. History

| Version | Date | Change |
|---|---|---|
| — | 08–20 Oct 2026 | Everything here was part of the old single plan (v1.0–v1.22): decisions, setup (Phase 0 complete 16-Oct-2026), environment, folders, file format, open items, glossary |
| v1.0 | 20 Oct 2026 | Own document. The old plan was split into one project document per bot (Audit Bot, TM, Auto Bot), [Open Code Pieces](open-code-pieces.md) and this page (owner: "each bot is a project in itself"). Content unchanged |
| v1.1 | 20 Oct 2026 | `news-releases.json` now shared by the Audit Bot and TM (§5) |
| v1.2 | 21 Oct 2026 | Replay safety rule (§4); TM's status in §1 brought up to date (built, service) |
| v1.3 | 22 Oct 2026 | Glossary: buy-/sell-limit, pending orders' stop-loss and take-profit, server-side trailing stop |
| v1.4 | 27 Oct 2026 | §3: suspend must be off on battery too (the 24–26 Oct Audit Bot gap); linger is off |
| v1.5 | 03 Nov 2026 | Scout added to the projects (§1); Audit Bot no longer started at login (audit-bot.md v1.3) |
| v1.6 | 04 Nov 2026 | §3, §5: deploy scripts publish each build into its own folder `~/tradebot/bin/<Bot>-<git hash>` and switch the link `~/tradebot/bin/<Bot>` to it; a running bot's files are never overwritten, moved or deleted (Scout's crash on stop, scout.md v1.3). Glossary: link |
| v1.7 | 04 Nov 2026 | §1: Audit Bot's audit run over and the bot stopped (audit-bot.md v1.5); Scout on IC Markets only with 18 symbols (scout.md v1.4) |
| v1.8 | 05 Nov 2026 | Dashboard added to the projects (§1, [dashboard.md](dashboard/dashboard.md) v1.3); its folders and credential files (§5); glossary: SSH / rsync, PHP, subdomain, HTTPS, API / JSON, Sign in with Google, M2M sign-in, hash, session cookie, options catalogue |
| v1.9 | 05 Nov 2026 | Glossary: PHP now for the dashboard's API; React / Next.js added (dashboard stack, [dashboard.md](dashboard/dashboard.md) v1.4) |
| v1.10 | 06 Nov 2026 | Dashboard live with the 30-min upload and the Scout page ([dashboard.md](dashboard/dashboard.md) v1.10): §1 status, §5 `~/tradebot/dashboard/outbox/`; glossary: outbox |
| v1.11 | 06 Nov 2026 | New [issue log](issue-log.md) (#1: IC Markets demo margin call); §4 protection rule: a hedge order alone is not enough (backstop), and a margin rule for bots that add exposure ([TM](bots/tm.md) v1.9, [Scout](bots/scout/scout.md) v1.7, [Open Code Pieces](open-code-pieces.md) v1.9); glossary: equity, margin, margin level (merged), backstop |
| v1.12 | 06 Nov 2026 | §8: live broker chosen — IC Markets (owner, Audit Bot v1.7), the regulation note weighed in |
| v1.13 | 07 Nov 2026 | Documents reorganised (owner): Scout's documents moved to `docs/bots/scout/` and the Dashboard's to `docs/dashboard/`; the projects table now kept only on the front page README (§1 points to it); new [documents map](README.md); §6 updated |
| v1.14 | 08 Nov 2026 | §5: Scout's stop and reset files in `control/` ([scout.md §2.7](bots/scout/scout.md#27-stopping-new-entries-v125-08-nov-2026)) |
| v1.15 | 09 Nov 2026 | Glossary: correlated group ([Scout](bots/scout/scout.md) v1.33) |
| v1.16 | 09 Nov 2026 | Glossary: lock-step |
