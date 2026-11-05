# Dashboard

**A private web page at [trader.uzlabz.com](https://trader.uzlabz.com) that shows what the bots are doing — Scout's setups and orders with their chart pictures, TM's trades, the accounts' money and trading conditions, the bots' health, the Audit Bot's result — and lets you steer Scout: switch symbols on or off, keep or skip each setup, and (as options are added) change settings such as a symbol's timeframe.** It never places, changes or closes an order itself; its controls set only the options the laptop lists, within the limits set there, and Scout applies them (§3.4). A project of its own; the laptop part uses the [Open Code Pieces](open-code-pieces.md). The look is designed in Claude Design from the [design brief](dashboard-design-brief.md).

| Item | Value |
|---|---|
| Status | **Specified — waiting for the owner's OK** (04-Nov-2026). Hosting ready: subdomain, HTTPS and upload key set up (§8 step 0) |
| Document version | v1.6 (05-Nov-2026) — history in §11 |
| Address | `https://trader.uzlabz.com` — people sign in with Google (owner `zia.zeeshan@gmail.com`; others watch once approved), machines with M2M sign-in (`claude`, administrator); API under `/api/` (§5) |
| Hosting | Hostinger **web hosting**, plan "Unlimited" (PHP 8.3, MySQL, SSH), account `u624663837`, until 25-Jul-2027. No VPS (a rented Linux server) and no Node.js — so nothing of ours can run there all the time; the site is plain files (the screens) plus a small PHP API (§4.1) |
| Design | [`dashboard-design-brief.md`](dashboard-design-brief.md) — the brief for Claude Design (screens, states, look); its result, [`design/`](design/HANDOFF.md) (tokens, screen sources, handoff notes), is the reference for the pages |
| Code | `web/dashboard/ui/` (the screens: Next.js + React + TypeScript, exported as plain files), `web/dashboard/api/` (the PHP API), `web/dashboard/openapi.yaml` (the API contract both follow, §4.1), `src/Trader.DashboardExport` (the laptop program that collects the data), `scripts/dashboard-push.sh` (30-min upload), `scripts/dashboard-sync.sh` (1-min control sync), `scripts/deploy-dashboard.sh` (puts the site on Hostinger), `scripts/dashboard-m2m.sh` (machine accounts, §5.3); in Scout: reading the control file ([Scout](bots/scout.md), §3.4); the options catalogue `config/scout-controls.json` (§3.4) |
| Tests | `tests/Trader.DashboardExport.Tests` (what gets collected and what never does); Scout's tests for the control file; Vitest for the screens, PHPUnit for the API, and a test that every API call refuses anyone not signed in or without the role |
| Accounts | Reads IC Markets demo 49123893 (`brokerA`) and Pepperstone demo 49124666 (`brokerB`) ([shared §4](shared.md#4-safety-rules-for-every-bot)) |
| Credentials | Laptop: `~/.config/trader/hostinger_ed25519` (upload key), `hostinger_known_hosts`, `dashboard.env`, `dashboard-m2m.env` (Claude's M2M secret) — all 600, outside the repo. Server: `~/trader-dashboard/config.php` (Google sign-in client ID and secret, the owner's address) and `users.json` (people and machines; machine secrets as hashes only), mode 600 — never in Git |
| Services | `trader-dashboard.timer` (data upload, every 30 min), `trader-dashboard-sync.timer` (controls, every minute) — systemd user units |

## 1. Goal

See and steer Scout from anywhere, on the phone too, without opening the laptop:

1. **Every 30 minutes, see what Scout found** — new setups and orders with their pictures — so you can take the same trades by hand on the 1H chart.
2. **Steer Scout:** switch symbols on or off; keep (green) or skip (grey) each setup; and, as they are added, more options — the first planned: a symbol's timeframe (1H → 1D). **Extensible:** a new option is one entry in a catalogue on the laptop plus Scout's support for it; the site builds its controls from the catalogue (§3.4).
3. Follow TM's handling of each trade (mirror, hedges, stops).
4. Know the accounts' money and **how far a trade can be scaled**: leverage, margin per lot, cost per lot, the largest size you could open right now.
5. See at a glance whether the bots are running and healthy, and keep the Audit Bot's broker comparison at hand.
6. **Let others watch** (approved Google accounts, read only), and let **Claude** read and steer through a machine sign-in (§5).

## 2. Pages

The screens, their states and the look are in the [design brief](dashboard-design-brief.md); this section is what each page must contain. Times are shown in UTC as in the logs, with your browser's local time beside them. Every page shows **"Data from HH:MM UTC"** at the top: green when the last upload is under 45 min old, amber up to 2 h, red after that (laptop asleep, logged out or offline). Pages fit a phone screen.

### 2.1 Scout (home page)

- **Setups**, newest first, from `setups.csv`: `NewStop`, `StopMoved`, `NewReversal`, `ReversalMoved`, fills, cancels and trend turns, with symbol, timeframe, side, level, pause size, *with / against* the higher timeframe. The last hour's are highlighted — this is the list to check every 30 min.
- **Keep / skip switch on each live setup** (§3.4): **green = keep** (the default — Scout trades it), **grey = skip** (Scout cancels its pending order and leaves the setup alone). The switch shows how far your choice has got: *saved* → *applied by Scout HH:MM*, or *too late — filled* (TM manages it; close it in cTrader if you want out).
- **Orders by day** from `orders.csv` (one line per order, its rows merged): time placed, symbol, side, main or reversal (M/R), level, stop-loss and its distance in pips, lots, expiry, and where it stands now: *pending*, *filled* (price, slippage), *expired*, *cancelled* (reason, including "skipped by you"), *closed* (net profit in USD).
- **The order's picture** ([Scout §3.1](bots/scout.md#31-order-pictures-owner-04-nov-2026)) as a small image beside each order; a tap opens it full size. When an order has several pictures, the latest `chart` row wins.
- Day picker (previous / next day), filter by symbol, and the day's totals: orders, filled, closed, net profit.

### 2.2 Symbols and settings (owner, 04-Nov-2026)

One row per symbol per account, for the symbols in Scout's `scout.json` only (§3.4):

- **On / off switch:** **on** = Scout trades it as configured; **off** = no new orders on it, and its pending (not yet filled) orders are cancelled.
- **Timeframe** (first added option): 1H, 4H or 1D — only the values the catalogue allows for that symbol (§3.4).
- **More settings:** every further option the catalogue lists for a symbol appears in the row's expandable panel, drawn from the catalogue — no page change needed to add one.
- Beside each: market group (forex, metals, indices, energies, crypto), Scout's size (lots), today's setups and orders. Each setting shows its state (*saved* / *applied by Scout HH:MM* / *refused: reason*) and whether it differs from the laptop's configured value, with **Reset to configured**.

A page **Settings** lists the options that are not per symbol (per account or for all of Scout), also drawn from the catalogue. A symbol cannot be added here — adding one stays a change to `scout.json` and `tm.json` on the laptop.

### 2.3 TM trades

- **Open trades now** (from the account snapshot, §3.2): each position with who opened it — *Scout*, *TM* or *manual* (no label) — side, lots, entry, stop-loss, profit so far.
- **One block per trade** (`trade` column of `actions.csv`: `O<orderId>` before the entry fills, `P<positionId>` after): every action TM took in time order — role (MAIN, MIR, HMAIN, HMIR), action, side, lots, prices, latency, result. A Scout trade links to its Scout order and picture through the order ID ([Scout §3](bots/scout.md#3-logs-and-outputs)).
- Both demos, a day picker, and refused actions (`result` not `ok`) marked red.

### 2.4 Accounts

Per demo account:

| Shows | From |
|---|---|
| Balance, equity (balance + open profit/loss), margin used, free margin, margin level % | snapshot (§3.2), every 30 min |
| Open positions and pending orders: count, by owner (Scout / TM / manual) | snapshot |
| Today's closed profit (net of commission and swap), by owner | closed deals (§3.2) |
| Profit per day (bar chart) and balance / equity over time (line chart) | `deals.csv`, `account.csv` (§3.3) |

### 2.5 Trading conditions (owner, 04-Nov-2026)

To decide how far a trade can be scaled. One table per account, one row per symbol (Scout's and TM's, §3.2), as the broker states them at the last upload:

| Column | Meaning |
|---|---|
| Account leverage | e.g. 1:500 — how many dollars of position one dollar of margin carries |
| Symbol leverage | The symbol's own leverage; where the broker uses tiers (leverage drops as the size grows), the tiers |
| Lot size | What 1 lot is: e.g. 100 000 EUR, 100 oz of gold, 1 BTC |
| Min / step / max lots | Smallest order, size step, largest single order |
| Margin per lot | USD the broker holds for 1 lot (buy and sell) at the current price |
| Pip value per lot | USD gained or lost per pip per lot |
| Spread now | In pips and in USD per lot |
| Commission per lot | USD, open and close together |
| Swap per lot per night | USD, long and short |
| **Largest size now** | Lots you could open with the free margin, rounded down to the step, capped by the max, taking the leverage tiers into account |

A small **"what if" box** under the table: pick a symbol, type a size → margin, pip value, spread and commission cost, and the money at risk for a stop-loss distance you type. It works in your browser on the uploaded numbers; it sends nothing anywhere.

### 2.6 Status

| Shows | From |
|---|---|
| Each bot and timer (Scout, TM, Audit Bot, audit report, dashboard upload, control sync): running / stopped / failed, since when, which build (`Scout-<git hash>`) | `systemctl --user` on the laptop, at upload time |
| Each bot's last log line time, today's warnings and errors (count) and the last 20 of them | `events.csv` |
| Last upload and last control sync: time, how long, next one due | the upload and sync themselves |
| Control changes: the last 50 (who, what, when, applied when) | `control-history.csv` (§3.4) |

### 2.7 Audit

The Audit Bot's comparison of IC Markets and Pepperstone, per symbol side by side ([Audit Bot §5](bots/audit-bot.md#5-daily-report)): the final summary file `summary-<from>_<to>.csv` when it is made, else the daily files; non-forex symbols compared by the USD columns (except XAGUSD, by pips) as that section says. The broker choice, once recorded in the Audit Bot's document, is shown above the table.

### 2.8 Users (owner only)

Who may enter and how: §5.4.

## 3. Data between the laptop and the site

Hostinger cannot reach into the laptop, so the laptop does all the moving: it **pushes** the data out every 30 minutes, and **fetches** your controls every minute.

### 3.1 The data upload (every 30 min)

- **Timer:** `trader-dashboard.timer` at **:02 and :32** each hour (the :02 run catches Scout's orders on each new 1H candle). If the laptop was asleep it runs once on waking. Like the bots, it runs only while you are logged in ([shared §3](shared.md#3-machine-and-services)).
- **Steps:** `Trader.DashboardExport` writes the snapshot (§3.2) and status (§2.6) → `scripts/dashboard-push.sh` checks the files (§6) and sends them.
- **How:** **rsync over SSH** — rsync copies only files that changed (so a run sends a few KB); SSH is the encrypted connection, here with a key file instead of a password, to the server identity pinned in `hostinger_known_hosts` (checked against hPanel on 04-Nov-2026). Files land in a folder outside the web folders (§4).
- **What is sent — an allowlist** (everything not on it stays on the laptop):

| Laptop | What |
|---|---|
| `~/tradebot/logs/<broker>/<ctid>/Scout-*/<date>/` | `orders.csv`, `setups.csv`, `events.csv`, `charts/*.svg` |
| `~/tradebot/logs/<broker>/<ctid>/TM-*/<date>/` | `actions.csv`, `events.csv` |
| `~/tradebot/logs/<broker>/<ctid>/Dashboard/<date>/` | `account.csv`, `deals.csv`, `conditions.csv` (§3.3) |
| `~/tradebot/reports/` | `daily-compare-*.csv`, `summary-*.csv` |
| `~/tradebot/dashboard/` | `status.json`, `snapshot-<broker>-<ctid>.json` (latest values), `scout-options.json` (the options catalogue with each symbol's configured values, §3.4) |
| `~/tradebot/control/` | `scout-<broker>-<ctid>.ack.json` (§3.4) |

Never sent: `~/.config/trader/` (secrets, keys), `~/tradebot/config/` (what the site needs from it, the exporter copies into `scout-options.json`), `~/tradebot/run/`, `~/tradebot/bin/`, the Audit Bot's raw logs, Rewind's logs, anything else. Nothing on the laptop is changed or deleted by the upload; the server keeps what it has received.

### 3.2 The account snapshot (read-only)

`Trader.DashboardExport` connects to the cTrader demo server with the shared code (own connection, the same tokens; token refresh as every bot, [Open Code Pieces §3](open-code-pieces.md#3-connection-and-tokens)) and **only asks questions**:

| Asks | Open API request |
|---|---|
| Balance, leverage, currency | `ProtoOATraderReq` |
| Open positions and pending orders, with labels and margin | `ProtoOAReconcileReq` |
| Open profit / loss | `ProtoOAGetPositionUnrealizedPnLReq` |
| Closed deals since the last run (profit, commission, swap) | `ProtoOADealListReq`; who opened it from the order's label (`Scout-…`, `TM-…`, none = manual) |
| Symbol details: lot size, min / step / max, commission, swap, pip position | `ProtoOASymbolByIdReq` |
| Leverage tiers | `ProtoOAGetDynamicLeverageByIDReq` |
| Margin for 1 lot, buy and sell | `ProtoOAExpectedMarginReq` |
| Current price and spread (USD conversion as the Audit Bot) | a short price subscription, then unsubscribe |

Symbols: those in `~/tradebot/config/scout.json` and `tm.json` for each account. It never sends an order message (place, amend, cancel, close) — it does not use the shared code's order functions at all, and a test fails if it does. It needs no Primary role ([Open Code Pieces §6](open-code-pieces.md#6-instance-registry-one-primary-per-bot-per-account)) because it trades nothing. The run takes seconds, then the program exits. If cTrader cannot be reached, the files are still uploaded and the snapshot is marked old.

### 3.3 New log files

The snapshot is also kept as history, in the shared CSV format ([shared §5](shared.md#5-folders-and-files)), in `~/tradebot/logs/<broker>/<ctid>/Dashboard/<yyyy-MM-dd>/` (a fixed folder name, not one per run — the program runs 48 times a day):

| File | One row per |
|---|---|
| `account.csv` | run: `time_utc`, `balance_usd`, `equity_usd`, `margin_used_usd`, `free_margin_usd`, `margin_level_pct`, `leverage`, `positions`, `pending_orders` |
| `deals.csv` | closed deal (each once, by deal ID): `time_utc`, `deal_id`, `position_id`, `symbol`, `side`, `lots`, `price`, `gross_usd`, `commission_usd`, `swap_usd`, `net_usd`, `owner` (Scout / TM / manual), `label` |
| `conditions.csv` | symbol, at the first run of the day and whenever a value other than price or spread changes: the columns of §2.5 |

### 3.4 Controls: an extensible set of options (owner, 04–05-Nov-2026)

Your switches and settings are kept as **small files** that Scout reads back. The design is **extensible** (owner): today it has three options — symbol on/off, keep/skip per setup, a symbol's timeframe — and more can be added later without redesigning the site or the files.

**The options catalogue — what the web may change**

The laptop holds a list of every option the site may set: `~/tradebot/config/scout-controls.json` (reference copy `config/scout-controls.json` in the repo, kept in step as for every config). Each entry says:

| Field | Meaning | Example (timeframe) |
|---|---|---|
| `key` | The option's name in the control file | `timeframe` |
| `scope` | What it applies to: `scout` (all), `account`, `symbol`, `setup` | `symbol` |
| `type` | The kind of control the site draws: `switch` (on/off), `choice` (one of a list), `multi` (several of a list), `number` (with min, max, step) | `choice` |
| `values` / `min`, `max`, `step` | The only values allowed | `["H1", "H4", "D1"]` |
| `default` | `fromConfig` (the value in `scout.json`) or a fixed value | `fromConfig` |
| `label`, `help` | Text the site shows | "Timeframe", "Chart Scout looks for setups on" |
| `confirm` | A sentence the site shows before saving, or none | "Scout will look on the new chart from its next candle; pending orders on the old one stay until filled or expired" |
| `applies` | When Scout uses it: `now`, `nextCandle` | `nextCandle` |
| `since` | The first Scout version that understands it | `Scout-<hash>` |

The export program uploads the catalogue together with each symbol's configured values (`scout-options.json`, §3.1). **The site draws its controls only from it:** a new option of an existing type appears on the page by itself; only a new *type* of control needs site and design work.

**Adding an option later** (e.g. order size within limits, Scout's watch / trade mode, order expiry):

1. Decide it in Scout's document ([Scout](bots/scout.md)) — rule changes to the setup itself start in Scout's own spec, as always.
2. Teach Scout to apply it (code, tests, deploy and restart with your OK).
3. Add its entry to the catalogue on the laptop. The next upload shows it on the site.

An option in the catalogue that the running Scout does not know yet is shown greyed with "needs a newer Scout".

**The control file** (one per account and bot; JSON, as the bots' other files). The site writes only what you changed; anything absent means "as configured":

```json
{
  "version": 14,
  "updated_utc": "2026-11-04 10:02:11",
  "updated_by": "zia.zeeshan@gmail.com",
  "catalogue": 1,
  "account": "brokerA-49123893",
  "scout":   { },
  "symbols": {
    "XNGUSD": { "enabled": false },
    "XAUUSD": { "timeframe": "D1" }
  },
  "setups": {
    "GBPJPY-H1-202611041400": { "decision": "skip", "utc": "2026-11-04 10:01:40", "by": "zia.zeeshan@gmail.com" }
  }
}
```

- File name `<bot>-<broker>-<ctid>.json` — `scout-brokerA-49123893.json` now; another bot (TM) can get its own file and catalogue the same way later.
- **Setup key** = `<symbol>-<timeframe>-<pause start yyyyMMddHHmm>`: the part of Scout's order label that names the setup ([Scout](bots/scout.md), labels), so one key covers its main and its reversal stop. Setup entries older than 7 days are dropped.
- `version` goes up by one on every change; Scout applies a file only if its version is higher than the last one it applied. `catalogue` is the catalogue version the site used.

**The path of a change**

1. You change a control → the site checks it against the catalogue, saves the file on the server in `~/trader-dashboard/controls/` (written whole to a temporary file, then renamed, so it is never half-written) and adds a row to `control-history.csv` (time, who, option, old → new value). The control shows *saved*.
2. **Every minute** `trader-dashboard-sync.timer` runs `scripts/dashboard-sync.sh`: it fetches `controls/` from the server (rsync over the same SSH key) into `~/tradebot/control/`, and sends back Scout's acknowledgement file. A few hundred bytes each way.
3. **Scout** reads the file when it changes (checked every 30 s, and always before it places or moves an order). It checks **each entry** against the catalogue and its own configuration, applies the good ones, refuses the rest one by one (`ControlApplied` / `ControlRefused` with the reason in `events.csv`), and writes `scout-<broker>-<ctid>.ack.json`: the file version applied, when, and per entry *applied* (with what it cancelled) or *refused* (why).
4. The next sync carries the acknowledgement up → each control shows *applied by Scout HH:MM* or *refused: reason*. Usually within 1–2 minutes; if the laptop is off, it stays *saved* and applies when the laptop is back.

**What Scout does with today's options**

| Option | Scout |
|---|---|
| Setup **skip** (grey) | Cancels that setup's pending orders (its own, by label — the ownership rule holds) and places or moves none for it while it is skipped. TM then cancels its mirror order, as for any cancelled entry ([TM §3.1](bots/tm.md#31-entry-by-a-pending-order-owner-22-oct-2026)). Already filled: nothing — TM manages the position; the page says *too late — filled* |
| Setup back to **keep** (green) | Places the setup's order again at the next candle close if the setup is still valid then |
| Symbol **off** | No new orders on it; cancels its pending ones; filled positions stay with TM. Setups are still logged, so the page keeps showing them |
| Symbol back **on** | Trades it again from the next candle |
| Symbol **timeframe** (e.g. 1H → 1D) | From the next candle, finds setups on the new chart (with its own higher timeframe, as Scout's spec sets per timeframe); loads that chart's history first. Pending orders found on the old chart stay until filled or expired (proposed — §10). New orders, labels and pictures carry the new timeframe |

**Limits that keep this safe**

- The web can set **only options listed in the catalogue, only to the values it allows**. The catalogue and `scout.json` are edited on the laptop only; nothing on the web can add a symbol, an option or a value.
- Scout checks every entry again itself; an unknown option, a value outside the catalogue, a symbol not in `scout.json` or a setup it does not know is refused — that entry only; the rest still apply.
- A file Scout cannot read at all is refused; Scout keeps the last good one and warns (pop-up). No file = everything as configured.
- The site writes only `controls/`; the laptop fetches only that folder, into `~/tradebot/control/` only.

## 4. On Hostinger

| Server folder | Holds | Reachable from the web? |
|---|---|---|
| `~/domains/uzlabz.com/public_html/trader/` | The screens as exported by Next.js (HTML, CSS, JavaScript, fonts — the same files for everyone, no data in them), the API's entry files `api/index.php` and `auth/index.php` that hand every request to `app/`, and `.htaccess`. Hostinger's `default.php` was moved out to `~/trader-dashboard/hostinger-default.php`. Until the Next.js site is built: `web/dashboard/placeholder/index.html` and the privacy page | Yes — the web root of trader.uzlabz.com |
| `~/trader-dashboard/app/` | The PHP API: signs you in, checks the role, reads the files and writes the controls (from `web/dashboard/api/`) | No |
| `~/trader-dashboard/data/` | The uploaded files (§3.1), same layout as on the laptop | No — pictures and files are handed out only by the PHP code, after sign-in |
| `~/trader-dashboard/controls/` | The control files and `control-history.csv` (§3.4) | No |
| `~/trader-dashboard/config.php` | Google sign-in client ID and secret, the owner's address; made once on the server, mode 600 | No |
| `~/trader-dashboard/users.json` | People (address, role, Google `sub`, approved when) and machines (name, role, client ID, secret **hash**), access requests; mode 600 | No |
| `~/trader-dashboard/logs/` | Sign-ins, refused sign-ins, approvals and API calls (`access.csv`) | No |

- **The screens hold no data:** after sign-in they fetch everything from the API as JSON (pictures too), so nothing private is ever a plain file in `public_html`.
- **PHP**, the web language Hostinger runs, reads the CSV files directly; **no database** (MySQL) — the files are small (a day of Scout and TM is well under 1 MB). Added later only if pages get slow.
- The site has **no cTrader credentials and no connection to cTrader**: even someone who broke into the server could read the demo logs and change Scout's options — only those the laptop's catalogue lists, only to the values it allows (§3.4) — but never place an order itself.
- The subdomain's folder lies inside the main site's, so the same pages would also answer at `uzlabz.com/trader`; `.htaccess` (Apache's per-folder settings file) sends those to `https://trader.uzlabz.com`, forces HTTPS, turns off folder listings and blocks every file except the pages and their styles.
- **Deploy:** `scripts/deploy-dashboard.sh` builds the screens on the laptop (Next.js static export), then sends them and the API over the same SSH key; it never touches `data/`, `controls/`, `config.php` or `users.json`.

### 4.1 Stack (owner, 05-Nov-2026)

| Part | Choice |
|---|---|
| Screens | **Next.js** (static export) + **React** + **TypeScript**, built on the laptop and uploaded as plain files; one address per screen (`/scout`, `/symbols` …), so links and the back button work |
| Look | The design tokens of the [brief](dashboard-design-brief.md) as CSS custom properties (light and dark); Claude Design's React components as the starting point |
| Data on the page | Fetched from the API every 30 min and on demand, updated in place; controls save without reloading the page |
| Charts | Scout's own SVG order pictures; a small chart library only if the balance and profit charts need one |
| API | Plain **PHP 8.3**, no framework; Google sign-in through a well-known library, not our own code; files outside `public_html`, no database |
| Contract | `web/dashboard/openapi.yaml` describes every API call; the screens' TypeScript types are generated from it, so the two sides cannot drift apart |
| Tests | Vitest (screens), PHPUnit (API) |

**Why:** the screens and the machines (M2M, §5.3) use **one** API, so there is one set of rules to secure and test instead of pages plus an API. The contract is the fixed point between the two halves: on a plan with Node.js or a VPS later, the PHP API can be replaced (Next.js server code, or .NET like the bots) without touching a screen, and the screens can move unchanged. It runs on the plan already paid for — the server has PHP 8.3 but no Node.js (checked 05-Nov-2026), so a Next.js server cannot run there.

## 5. Sign-in and roles (owner, 04–05-Nov-2026)

Two ways in, one set of roles. More sign-in methods can be added later beside them (each is one module behind the same roles).

### 5.1 Roles

| Role | Can | Who |
|---|---|---|
| **Owner** | Everything: all pages, all controls, the Users page | `zia.zeeshan@gmail.com` — fixed in `config.php` on the server, so it can never be removed or demoted from the web |
| **Administrator** | All pages and all controls (Scout's options, §3.4); **not** the Users page | Machine accounts given this role — now one: `claude` (§5.3) |
| **Viewer** | All pages, read only: controls are shown but disabled ("view only") | Any Google account the owner has approved (§5.2) |

Every page and every API call checks the role on the server; a hidden button is never the only protection.

### 5.2 People: "Sign in with Google"

- No passwords of our own. Google checks who you are (with your usual Google security, including 2-step verification); the site only learns the e-mail address and whether Google has verified it.
- **Others can watch** (owner, 05-Nov-2026): anyone may sign in with Google, but an address the site does not know lands on **"Access requested — waiting for approval"** and sees nothing. The owner sees the request on the Users page and approves it as **viewer** (or refuses it). Approved viewers can be removed at any time.
- **Setup (owner, once, step 3):** in Google Cloud Console a project "Trader Dashboard" with an **OAuth client** (the ID and secret that let our site use Google sign-in): type *Web application*, redirect address `https://trader.uzlabz.com/auth/callback`, only the sign-in scopes `openid email` (no Gmail, Drive or other access). Consent screen **published** ("In production"), so people other than you can sign in; with only these basic scopes Google asks for no review. Who gets in is decided by our own list (above), not by Google. The client secret goes straight into `config.php` on the server, never into Git or chat. To publish, Google needs a home page (`https://trader.uzlabz.com`) and a privacy policy (`https://trader.uzlabz.com/privacy.html`, from `web/dashboard/ui/public/privacy.html`) on the authorised domain `uzlabz.com`. **Done 05-Nov-2026:** project `trader-dashboard-513816`, client "Trader Dashboard web"; the downloaded client file is kept on the laptop as `~/.config/trader/google-oauth-client.json` (600).
- **How it is checked:** the standard sign-in flow (OpenID Connect, authorisation code with PKCE): a one-time `state` and `nonce` against forged replies; the reply's issuer, audience (our client ID), expiry and signature (Google's published keys) are checked; `email_verified` must be true; Google's fixed account number (`sub`) is recorded at the first sign-in and must match afterwards.
- **Session:** after sign-in a **session cookie** (a small token the browser keeps, sent back with each page) keeps you in: HTTPS only, not readable by scripts, not sent by other sites. Idle timeout 14 days (owner), 7 days (viewers); **Sign out** ends it at once. Every change also carries an anti-forgery token, so another site cannot make it for you.

### 5.3 Machines: "M2M sign-in" (owner, 05-Nov-2026)

**M2M** (machine-to-machine) sign-in is for programs, not people — first for **Claude** (the AI assistant working on this project from the laptop), so it can read the dashboard and set Scout's options as administrator without a Google account.

- **Machine account:** a name (`claude`), a role (administrator), a **client ID** and a **client secret** (a long random key). Made on the laptop with `scripts/dashboard-m2m.sh create claude --role admin`: the script writes the secret to `~/.config/trader/dashboard-m2m.env` (mode 600) and sends only its **hash** (one-way scrambled form) to the server over SSH. The secret never passes through a browser, chat or Git.
- **Signing in:** the program sends its client ID and secret to `POST https://trader.uzlabz.com/api/auth/token` (HTTPS, in the request header, never in the address) and gets an **access token** valid for 1 hour; it then calls the dashboard's **API** with it. This is the standard "client credentials" way machines sign in.
- **The API** is the one the screens use themselves (§4.1), so a machine gets the same data, as JSON (`/api/scout/setups`, `/api/accounts`, `/api/conditions`, `/api/status`, `/api/options` …), and — for administrators — `POST /api/controls` to change an option, under exactly the same rules as the page (catalogue, values, history, Scout's acknowledgement; §3.4). Every call is logged with the machine's name.
- **Limits:** a machine account can never use the Users page or create other accounts; after 5 wrong secrets in 15 min its sign-in is locked for 15 min and the owner is told; the owner can **revoke** it at any time (Users page or laptop) and **rotate** its secret from the laptop (`scripts/dashboard-m2m.sh rotate claude` — the new secret has to land on the laptop, so this is not done from the web).
- **On the sign-in page** the M2M way is shown as a second option, "Machine sign-in (M2M)", which explains it and links to the API description — there is no form to paste a secret into, so a secret is never typed into a web page.

### 5.4 Users page (owner only)

People and machines with their sign-in method, role, last seen and address; access requests to approve or refuse; remove a viewer; revoke a machine (rotating its secret is done on the laptop, §5.3). Every sign-in, refused sign-in, approval and control change is logged with time, account and address. Search engines are told not to list the site.

## 6. Safety rules

1. **The site never trades.** Nothing in this project places, changes or closes an order. The laptop program only asks (§3.2); the site has no route to cTrader (§4). The only effect of the web on trading is through Scout's control file, which can set only the options the laptop's catalogue lists, to the values it allows (§3.4); Scout checks each entry again and does any cancelling itself, of its own orders only.
2. **No secrets leave the laptop.** Only allowlisted files are sent (§3.1). Before each upload, `dashboard-push.sh` refuses to send if any file to be sent is outside the allowlist, or if any contains a value from `secrets.env` (the export program, which reads that file anyway to connect, checks this and never prints the values). A refused upload sends a Critical notification ([Open Code Pieces §7](open-code-pieces.md#7-notifications)) and nothing is sent.
3. **Roles are checked on the server** for every page and API call (§5.1): viewers can never change anything; machines can never manage users; the owner cannot be removed from the web.
4. **Credentials stay outside Git:** laptop files in `~/.config/trader/` mode 600; `config.php` and `users.json` on the server mode 600; machine secrets stored on the server only as hashes. The diff is checked for secrets before each commit, as for every project.
5. **Nothing in `~/tradebot/logs` is changed or deleted** — the program only adds its own `Dashboard/` files.
6. **The bots are not touched** by the upload: no start, stop or restart; it only reads their logs and asks systemd for their state. Scout's change to read the control file is deployed and restarted only with your OK.

## 7. Run and operate

```bash
scripts/deploy-dashboard.sh                                   # build the screens; put them and the API on Hostinger
systemctl --user enable --now trader-dashboard.timer          # start the 30-min uploads
systemctl --user enable --now trader-dashboard-sync.timer     # start the 1-min control sync
systemctl --user list-timers 'trader-dashboard*'              # when are the next ones?
systemctl --user start trader-dashboard.service               # upload now
journalctl --user -u trader-dashboard -n 50                   # last upload's output
systemctl --user disable --now trader-dashboard-sync.timer    # stop taking controls from the web (Scout keeps the last file)
scripts/dashboard-m2m.sh create claude --role admin           # make a machine account (secret → ~/.config/trader/dashboard-m2m.env)
scripts/dashboard-m2m.sh rotate claude                        # new secret for it
scripts/dashboard-m2m.sh revoke claude                        # remove it
```

`scripts/dashboard-push.sh --dry-run` lists what would be sent and checks it, sending nothing. To clear all controls at once on the laptop: delete `~/tradebot/control/scout-*.json` is **not** the way (the next sync brings them back) — switch them back on the site, or stop the sync timer and then remove the file.

## 8. Build steps

| Step | Content | Status |
|---|---|---|
| 0 | Hosting: SSH on (owner), upload key added (owner), server identity pinned, subdomain trader.uzlabz.com, HTTPS (Lifetime SSL) | Done 04-Nov-2026 |
| 1 | This document and the design brief (owner's OK) | Done 05-Nov-2026 |
| 2 | Design in Claude Design from the brief (owner); the result saved with the brief | Desktop design ready for the owner's review 05-Nov-2026: [`design/HANDOFF.md`](design/HANDOFF.md). Phone layouts deferred (owner) |
| 3 | Google sign-in client (owner, §5.2); site skeleton with both sign-ins, roles, Users page, access requests, the API's sign-in; Claude's machine account; the API contract (`openapi.yaml`); the Next.js app from Claude Design's components; Status page; `deploy-dashboard.sh` | Started 05-Nov-2026: Google project `trader-dashboard-513816` and web client made (owner), `config.php` on the server; privacy page and a temporary home page live (§5.2) |
| 4 | Data upload: allowlist, secrets check, `status.json`, `dashboard-push.sh`, timer — no cTrader connection yet. Dry run, then the first real upload. Scout page (view only) | — |
| 5 | Account snapshot (§3.2, §3.3) — read-only cTrader questions; Accounts, Trading conditions with the "what if" box, TM trades, Audit pages | — |
| 6 | Controls: the options catalogue with today's three options (symbol on/off, setup keep/skip, symbol timeframe); controls drawn from it on the site; `dashboard-sync.sh` and its timer; Scout reads the control file and acknowledges each entry ([Scout](bots/scout.md) document first; tested with `--dry-run`, then deployed and restarted with your OK) | — |
| 7 | A few days' use; fixes; this document brought up to date | — |

## 9. Decisions

| # | Decision | Why |
|---|---|---|
| 1 | ~~PHP site~~ → Next.js screens with a small PHP API (decision 17), on the existing Hostinger web hosting, not a VPS | Already paid for; a VPS costs more and the dashboard needs nothing running there (owner, 04-Nov-2026) |
| 2 | The laptop pushes data and fetches controls; the server never connects to the laptop | Hostinger cannot reach a home laptop; nothing on the laptop is opened to the internet |
| 3 | No cTrader credentials on the server | A break-in there can never trade |
| 4 | Data every 30 min, at :02 and :32 (owner, 04-Nov-2026) | You check Scout's findings every 30 min to trade 1H; :02 is just after the hourly candle closes |
| 5 | ~~Own login page, one user~~ → **Sign in with Google**, owner `zia.zeeshan@gmail.com` (owner, 04-Nov-2026) | No password of ours to keep; Google's own protections |
| 6 | CSV files, no database | Files are small and already in a format the pages can read; one thing less to run |
| 7 | Trading conditions and "largest size now" shown (owner, 04-Nov-2026) | To decide how far a trade can be scaled |
| 8 | Code in this repository | The site reads these bots' log formats; one place for code and documents |
| 9 | Upload by key only, server identity pinned | No password on the laptop; uploads cannot be sent to a fake server |
| 10 | ~~Read-only dashboard~~ → **switches for symbols and setups**, kept as small files Scout reads back (owner, 04-Nov-2026) | To steer Scout from the phone without the laptop |
| 11 | ~~Controls can only hold Scout back~~ → controls set **only options listed in the laptop's catalogue, only to the values it allows**; Scout checks each entry again and does any cancelling itself (owner, 05-Nov-2026: options such as timeframe must be possible) | The limits are set on the laptop, never from the web; only Scout touches Scout's orders (ownership rule) |
| 12 | Controls fetched every minute, separately from the 30-min data | A skip must reach Scout before its stop order fills; 30 min would often be too late |
| 13 | Look designed in Claude Design from a written brief (owner, 04-Nov-2026) | A considered design before code |
| 14 | **Extensible options** (owner, 05-Nov-2026): one catalogue of options on the laptop (key, scope, type, allowed values, default, help), one control file per bot and account holding only what you changed, the site's controls drawn from the catalogue, Scout acknowledging each entry | A new option needs a catalogue entry and Scout's support, not a new page, file format or design; first one: timeframe per symbol |
| 15 | **Others may watch** (owner, 05-Nov-2026): any Google account can ask; the owner approves it as viewer (read only) | Share the dashboard without sharing control; nobody sees anything before approval |
| 16 | **M2M sign-in for machines**, first `claude` as administrator (owner, 05-Nov-2026): client ID + secret → 1-hour token → JSON API; the secret made on the laptop, only its hash on the server; machines cannot manage users | Claude can read and steer without a Google account; a leaked token expires within the hour and the secret can be revoked |
| 17 | **Next.js + React screens (static export) with a small PHP API**, one contract between them (owner, 05-Nov-2026; §4.1) | One API for screens and machines; either half can be replaced later behind the same contract; Claude Design's React work carries over; runs on the current plan, which has no Node.js |

## 10. Open items

- **Skip arrives after the fill:** a stop order can fill within minutes of being placed; a grey switch then comes too late (the page says so). If this happens often: a short *hold* before Scout places each order, so you can skip first — a Scout rule change, decided in Scout's documents.
- Can the upload key be limited on the server to rsync of `~/trader-dashboard/data/` and `controls/` only (an `authorized_keys` restriction)? Hostinger manages that file from hPanel; check in step 4. Until then the key can log in fully — it stays mode 600 on the laptop.
- Owner of a closed deal: to be confirmed in step 5 that the order history gives each deal's label (else Scout's `closed` rows and TM's logs are matched by position ID).
- Uploads and control syncs stop while the laptop sleeps or nobody is logged in (linger off, [shared §3](shared.md#3-machine-and-services)); the page shows the data's age and *saved, not yet applied*. Moves with the bots when they go to a VPS.
- **Timeframe change and pending orders** (proposed, §3.4): orders found on the old chart stay until filled or expired. Alternative: cancel them at the switch. Owner to confirm in step 6.
- **Timeframes Scout supports:** its spec defines the setup for 1H, 4H and 1D (`scout.json`: H1, H4, D1); each timeframe's higher timeframe and the detector's settings per timeframe to be checked in Scout's spec before the timeframe option goes live.
- **What viewers see:** now every page (including balances). If some pages should stay owner-only (e.g. Accounts), that becomes a per-page role setting.
- **Viewer access requests** may come from strangers who find the address; they see nothing until approved, and the owner can switch requests off (then unknown addresses are simply refused).
- Further options (candidates, each decided in Scout's document first): order size within set limits, watch / trade mode, order expiry; options for TM through its own catalogue; a push message when Scout finds a setup ([Open Code Pieces §7](open-code-pieces.md#7-notifications)).

## 11. History

| Version | Date | Change |
|---|---|---|
| v1.0 | 04 Nov 2026 | First issue (draft shown to the owner, not committed): pages, upload, account snapshot with trading conditions (owner), password login, read-only, safety rules, build steps. Hosting prepared the same day: SSH on, upload key, subdomain trader.uzlabz.com with HTTPS |
| v1.1 | 04 Nov 2026 | Owner: switches for symbols (§2.2) and keep / skip per setup (§2.1), kept in small control files Scout reads back, fetched every minute (§3.4; decisions 10–12); **Sign in with Google**, administrator `zia.zeeshan@gmail.com`, replaces the password login (§5, decision 5); look designed in Claude Design from [the brief](dashboard-design-brief.md) (decision 13); build steps re-ordered (§8) |
| v1.2 | 05 Nov 2026 | Owner: the design must be extensible, first new option a symbol's timeframe (1H → 1D). An options catalogue on the laptop defines what the web may set and to which values; one control file per bot and account; the site draws its controls from the catalogue; Scout applies and acknowledges each entry (§3.4, decisions 11 and 14); Symbols page becomes Symbols and settings, plus a Settings page (§2.2) |
| v1.3 | 05 Nov 2026 | Owner: others may sign in with Google and watch once approved (viewer role); Claude gets an administrator account through a new **M2M sign-in** (client ID + secret → token → JSON API) with no Google account; roles owner / administrator / viewer; Users page (§2.8, §5; decisions 15, 16). Google consent screen published instead of testing |
| v1.4 | 05 Nov 2026 | Owner: **stack** — screens in Next.js + React + TypeScript, exported as plain files, with a small PHP API and one API contract (`openapi.yaml`) between them, instead of PHP pages (§4.1, decision 17); server folders, deploy, tests and build step 3 to match |
| v1.5 | 05 Nov 2026 | Design result (build step 2) saved in `docs/design/`: design tokens (`tokens.css`), the 12 screen sources from Claude Design, and `HANDOFF.md` for step 3 (shared components, states, sample data to replace, gaps). Desktop only; phone layouts deferred (owner) |
| v1.6 | 05 Nov 2026 | Build step 1 done (owner's OK); step 3 started: Google project and web sign-in client (owner), `config.php` on the server, privacy page and a temporary home page live with `.htaccess` (HTTPS only, uzlabz.com/trader redirected, not listed by search engines) — Google needs both pages to publish the sign-in (§5.2, §4) |
