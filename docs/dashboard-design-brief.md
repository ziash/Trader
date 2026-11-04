# Design brief — Trader Dashboard

Brief for **Claude Design**. Paste this whole document into Claude Design as the starting prompt. The product rules behind it are in [`dashboard.md`](dashboard.md) (sections referenced as §n); where the two differ, `dashboard.md` wins.

| Item | Value |
|---|---|
| Brief version | v1.2 (05-Nov-2026), for [dashboard.md](dashboard.md) v1.3 |
| Product | Trader Dashboard — `https://trader.uzlabz.com` |
| Users | The owner (everything); approved viewers (read only), both signing in with Google; machine accounts (Claude) through M2M sign-in, which use the API, not the screens |
| Deliverables | §12 |

---

## 1. The product in one paragraph

A private web dashboard for one person who runs automated trading bots on demo accounts. A bot called **Scout** watches 18 markets on the 1-hour chart, spots a pattern ("a pause in a trend") and places a *stop order* (an order that only fills if the price moves through a level). A second bot, **TM**, then manages each trade (copies, hedges, protective stops). The dashboard shows what both bots found and did, the accounts' money and trading conditions, and the bots' health. It also **steers Scout**: turn a market (symbol) on or off, **keep (green) or skip (grey)** each setup Scout found, and change settings such as the chart timeframe Scout watches for a symbol (1H → 1D). **This set of controls will grow over time**, so the design must be **extensible**: controls are generated from a list of options (an *options catalogue*) the laptop supplies — see §7.2. The dashboard never buys or sells anything itself — there are no buy/sell buttons anywhere.

## 2. The users and how they use it

- **Who:** mainly the owner — a telecommunications network engineer, technically fluent, not a professional developer or designer. Trades by hand as well, on the same 1-hour charts.
- **When:** every 30 minutes during trading hours, mostly **on a phone**, for under a minute: *"What did Scout find since I last looked? Do I want it? Is everything running?"* Longer sessions on a laptop in the evening: reviewing trades, profit, conditions.
- **Main job:** scan new setups → look at the chart picture → keep or skip → maybe place the same trade by hand elsewhere.
- **What matters most:** speed of scanning, being sure a switch actually reached the bot, and knowing how fresh the data is.
- **Viewers:** a few people the owner lets watch — same pages, **read only**. They must see at once that they cannot change anything, without the page feeling broken.
- **Machines:** Claude (the owner's AI assistant) signs in by M2M and uses a JSON API; it never sees the screens, so it needs no design — except its entry on the sign-in page and the Users page.

## 3. Design principles

1. **Five-second scan.** The home screen answers "anything new in the last hour?" at a glance: newest first, last hour highlighted, one card per setup.
2. **State is never ambiguous.** Every switch shows where the decision is: *saved on the site* → *applied by the bot at 10:03*, or *too late*. Data freshness is always visible.
3. **Calm, professional, dense.** A tool for a trader, not a crypto casino: no neon, no gradients on data, no confetti. Numbers in tabular figures, aligned. Information density like a good banking or monitoring app.
4. **Colour carries meaning, never alone.** Every coloured state also has an icon or word (colour-blind safe).
5. **Phone first, desktop richer.** Same content; the desktop shows more columns and side-by-side panels.
6. **Nothing looks like a trade button.** Switches look like settings (toggles, segmented controls), not like "Buy" / "Sell".
7. **Built to grow.** New options will be added without a redesign: design a small set of **generic setting controls** (§7.2) and layouts that take any number of them — per symbol, per account, for the whole bot — rather than one-off widgets.

## 4. Technical constraints (the build)

- Pages are **rendered on the server in PHP** (classic web pages, one URL per screen), then enhanced with **small vanilla JavaScript** — no React / single-page app, no build step. Design for HTML + CSS that can be cut into templates.
- **Design tokens as CSS custom properties** (colours, type scale, spacing, radii, shadows), light and dark sets; dark mode follows the system setting with a manual toggle.
- Fonts: one sans-serif for text and a monospaced or tabular-figure face for numbers (e.g. Inter + JetBrains Mono, or IBM Plex Sans + Plex Mono), from Google Fonts or the system stack.
- Breakpoints: phone 360–430 px, tablet 768 px, desktop 1280–1440 px. No horizontal page scroll on phones; wide tables scroll inside their own box with the first column pinned.
- Charts (balance line, profit bars): simple, drawn with a small library or inline SVG; design them as plain line and bar charts with light gridlines.
- **Order pictures already exist** and are fixed: SVG images 720 × 400, **white background**, candles teal `#26a69a` (up) and red `#ef5350` (down), the pause shaded amber `#f59e0b`, the order level blue `#1e66f5`, stop-loss rose `#e11d48`, grey text `#6b7280`. They stay white in dark mode — design a frame / card that makes a white picture sit well on a dark page. Thumbnail at about 240 × 133 on phones.
- Pages may be loaded every 30 minutes on mobile data: keep them light.

## 5. Navigation and global elements

**Sections:** Scout (home) · Symbols · Trades (TM) · Accounts · Conditions · Settings · Status · Audit · Users (owner only).

- **Phone:** bottom tab bar with Scout, Symbols, Trades, Accounts, **More** (Conditions, Settings, Status, Audit, Users, Sign out). Badge on Scout with the number of setups in the last hour.
- **Desktop:** left sidebar with all sections; content up to ~1200 px wide.
- **Top bar on every page:**
  - **Freshness pill** — "Data 10:02 UTC · 6 min ago": green under 45 min, amber 45 min–2 h, red over 2 h with the text "Laptop offline? Last data 3 h ago".
  - **Account selector** — *IC Markets demo* / *Pepperstone demo* / *Both* (Scout trades IC Markets only at present; TM trades both).
  - **Time display toggle** — UTC (default) or local; the other shown smaller beside each time.
  - **User menu** — Google avatar and e-mail, role badge (**Owner** / **Viewer**), Sign out. Viewers also get a slim "View only" badge in the top bar.

## 6. Screens

Sample data in §11. For each screen: what it is for, its content, and the states to design.

### 6.1 Sign in

- Product name "Trader Dashboard", one line ("Scout, TM and accounts — private"), and the **sign-in options** as a short list, designed so more can be added later:
  1. **"Sign in with Google"** — the main button (Google's own button style rules).
  2. **"Machine sign-in (M2M)"** — a quiet secondary link for programs; it opens a short panel: "For programs such as Claude. Machines sign in with a client ID and secret through the API, not on this page." and a link "API description". **No form** — a secret is never typed into this page.
- No password fields, no sign-up form.
- States: default · signing in (button busy) · **access requested** (an unknown Google account: "Thanks, someone@gmail.com — your request to view this dashboard is waiting for the owner's approval." with Sign out) · **refused** ("The owner has not approved this account.") · session expired ("Please sign in again").

### 6.2 Scout (home)

Purpose: what Scout found and did; keep or skip.

**A. "Last hour" strip** (top): count of new setups and orders in the last hour, last candle close time ("Last 1H candle closed 10:00 UTC"), Scout's status dot (running / stopped).

**B. Setup cards**, newest first, grouped by candle close ("10:00", "09:00"…; a 1D setup sits under the daily close, labelled "Daily candle 00:00"); the last hour's group visually highlighted. Each card:
- Symbol (large) and market group tag (Forex, Metals, Indices, Energies, Crypto); timeframe chip ("1H", "4H" or "1D" — symbols can be on different timeframes, so the chip must stand out enough to tell them apart).
- Direction: **Buy stop ↑** / **Sell stop ↓** with an arrow icon; *Main* or *Reversal*.
- Level and stop-loss, distance in pips; size in lots.
- Trend tag: **with** or **against** the higher timeframe (4H) — "against" deserves a subtle warning tone.
- Event: New / Moved / Filled / Cancelled / Expired / Trend turned, with time.
- **Thumbnail of the order picture**; tap opens the detail (6.3).
- **Keep / Skip control** (component 7.1) on the right (desktop) or bottom (phone).

**C. Orders of the day** (below, or a second tab "Orders"): a compact table/list — time, symbol, side, M/R, level, stop-loss, lots, status chip, net profit if closed, thumbnail. Day picker (‹ Today ›, calendar), symbol filter (chips or a search box), day totals: orders · filled · closed · net profit.

States: no setups yet today ("Nothing new since 08:00 — Scout is watching 18 symbols") · data stale (top banner) · Scout stopped (red banner on this page) · a filtered symbol that is switched off (card shows "Symbol off" ribbon).

### 6.3 Setup / order detail (sheet on phone, side panel on desktop)

- The **order picture** large (fit width, pinch-zoom on phone).
- All fields: placed, expiry, level, stop-loss, pips, lots, order ID, higher-timeframe trend, main/reversal.
- **Timeline:** Found → Order placed → (Moved) → Filled at 1.35032 (slippage 0.2 pips) → Closed +$12.40 net — or Skipped by you → Cancelled by Scout.
- **TM's actions** for this trade (mirror order, hedges, stop moves) as a sub-timeline, link "Open in Trades".
- The Keep / Skip control, larger, with a one-line explanation of what it will do.

### 6.4 Symbols (and their settings)

Purpose: switch markets on/off for Scout and set each symbol's options.

- Grouped list by market (Forex majors, Forex crosses, Metals, Indices, Energies, Crypto). Each row (collapsed): symbol, **timeframe** (a compact choice control: 1H · 4H · 1D), size (e.g. "0.10 lot"), today's setups / orders count, the **on/off switch**, and a marker when anything differs from the laptop's configured value ("changed").
- **Expanded row:** all per-symbol options from the catalogue, each as a **setting row** (component 7.2) — today on/off and timeframe; later more (e.g. order size, watch/trade mode, order expiry). The layout must take 2 or 12 options gracefully.
- Group header with "All on / All off" for the group.
- Turning a symbol **off while it has pending orders** opens a confirm sheet: "Switch off XAUUSD? Scout will cancel 1 pending order (Buy stop 2 650.20). Filled trades stay with TM." [Cancel] [Switch off].
- **Changing the timeframe** opens a confirm sheet with the catalogue's sentence: "Change XAUUSD from 1H to 1D? Scout will look for setups on the daily chart from its next candle. The pending order found on 1H stays until filled or expired." [Cancel] [Change].
- Note at the bottom: "Symbols are added or removed on the laptop (scout.json)."
- States: all on · some off · a symbol on 1D among 1H ones · a change waiting for the laptop · a change refused · laptop offline (controls still usable, with "will apply when the laptop is back") · an option greyed "needs a newer Scout".

### 6.5 Settings

Purpose: options that are not per symbol — for all of Scout or per account — generated from the catalogue. Today the list may be empty: design the **empty state** ("No general options yet — they appear here when added on the laptop") and a filled example with three made-up options (a switch "Pause all new orders", a choice "Order expiry: 12 h · 24 h · 48 h", a number "Max open Scout orders: 10 (1–30)"). Grouped by scope (Scout · IC Markets demo · Pepperstone demo), each a setting row (7.2), with **Reset to configured** per row.

### 6.6 Trades (TM)

- **Open now:** cards/rows per open position — symbol, side, lots, entry, stop-loss, open profit (green/red with sign), owner tag (**Scout** / **TM** / **Manual**), account.
- **History by trade:** one expandable block per trade (title: symbol, side, opened time, result), inside a compact timeline of TM's actions — role chip (MAIN, MIR, HMAIN, HMIR), action (Mirror order placed, Hedge placed, Stop moved, Position closed…), price, latency in ms, result (✓ ok or ✕ refused in red).
- Filters: account, day, owner, only refused.

### 6.7 Accounts

Per account (two columns on desktop, swipe or tabs on phone):
- **Headline numbers:** Balance, Equity, Free margin, Margin level %, Leverage 1:500 — big tabular figures.
- **Today:** closed profit net, split by Scout / TM / Manual (small stacked bar or three figures).
- **Charts:** balance & equity over time (line, 7d / 30d / all), profit per day (bars, green above zero, red below).
- **Open positions / pending orders** counts with owner split.

### 6.8 Conditions (trading conditions and "what if")

Purpose: decide how far a trade can be scaled.

- Table per account, one row per symbol: Lot size (e.g. "100 000 EUR"), Min / Step / Max lots, Leverage (with tiers as a small popover), Margin per lot, Pip value per lot, Spread now (pips and $), Commission per lot, Swap long / short, **Largest size now** (emphasised).
- Phone: each symbol as a compact card with the four most important figures (margin/lot, pip value/lot, cost/lot, largest size now) and "more" to expand.
- **"What if" calculator** (sticky panel on desktop, bottom sheet on phone): choose symbol, type lots (stepper respects min/step/max), type stop-loss distance in pips → shows margin needed, pip value, spread cost, commission, **money at risk**, and % of free margin used; warns when above "largest size now". Pure calculation, no submit button.
- Timestamp "Conditions as of 10:02 UTC".

### 6.9 Status

- **Bot tiles:** Scout, TM, Audit Bot, Audit report (timer), Data upload (timer), Control sync (timer) — state (Running / Stopped / Failed / Waiting), since when, build name (e.g. `Scout-ae41f2c`), last log line time, warnings / errors today.
- **Recent warnings and errors:** list with level icon, time, bot, message; filter by bot.
- **Uploads:** last data upload (time, duration, files), next due; last control sync, next due.
- **Control history:** last 50 switch changes — time, what (e.g. "Skip GBPJPY 1H 14:00 setup"), by whom, applied at (or "waiting").

### 6.10 Audit

- Comparison table: per symbol, IC Markets vs Pepperstone on adjacent rows or side-by-side columns — typical spread, near-worst spread (95th percentile), slippage, latency, rejections — the better broker's cell subtly highlighted per metric.
- Period selector (each day or the whole run 18-Oct → 04-Nov-2026); a banner for the recorded broker choice when it exists.

### 6.11 Users (owner only)

- **Access requests** on top (badge in the nav when there are any): e-mail, Google name and picture, requested when → **Approve as viewer** / **Refuse**.
- **People:** e-mail, role (Owner — fixed, no actions; Viewer), last seen, Remove.
- **Machines:** name (`claude`), role (Administrator), client ID (shown), last call, calls today, **Revoke** (with confirm). A note: "New machines and new secrets are made on the laptop."
- **Sign-in log:** last 50 sign-ins, refused sign-ins and approvals.
- A switch "Accept access requests" (off = unknown accounts are simply refused).

## 7. Key components

### 7.1 Keep / Skip control (per setup)

The most important control. Owner's words: *skip = grey, keep = green.*

| State | Look | Text |
|---|---|---|
| **Keep** (default) | Green, filled, check icon | "Keep" |
| **Skip — saved** | Card turns grey (desaturated, picture dimmed), control grey with a small clock | "Skipped · waiting for laptop" |
| **Skip — applied** | Grey with a check | "Skipped · applied 10:03" (and "1 order cancelled") |
| **Keep — saved / applied** after a skip | Green with clock → check | "Kept · applied 10:05 · order again at next candle" |
| **Too late** | Locked, neutral, lock icon | "Filled 09:41 — managed by TM" |
| **Not available** | Hidden or disabled | Setup expired or cancelled |
| **Refused / error** | Amber outline, warning icon | "Not applied: symbol not in Scout's list" |

- One tap toggles; no confirmation dialog; an **Undo** toast for 5 seconds.
- A segmented control "Keep | Skip" or a large toggle — designer's choice, but it must read as a decision, not as Buy/Sell.
- Skipped cards stay in the list (greyed), never vanish.

### 7.2 Setting rows — generic, catalogue-driven controls

All options except keep/skip are drawn from the **options catalogue** the laptop supplies. Each catalogue entry has: a label, a help text, a **scope** (all of Scout / account / symbol / setup), a **type**, the allowed values, a default ("as configured on the laptop"), an optional confirm sentence, and when it takes effect ("now" or "next candle"). Design **one setting row** that renders any entry, in four types:

| Type | Control | Example |
|---|---|---|
| `switch` | On/off toggle (on = green, off = grey) | Symbol on/off; "Pause all new orders" |
| `choice` | Segmented control for ≤ 4 values, dropdown / sheet for more | Timeframe 1H · 4H · 1D; Order expiry 12 h · 24 h · 48 h |
| `multi` | Chips that can be toggled (several at once) | Timeframes to watch: 1H, 4H |
| `number` | Stepper with min / max / step and unit | Max open Scout orders 10 (1–30) |

Every setting row has the same anatomy: label · ⓘ help · control · **delivery state** · "changed from configured" marker with **Reset to configured** · optional "takes effect at next candle" note.

**Delivery states** (shared with 7.1): *as configured* (no marker) → *saved · waiting for laptop* (clock) → *applied by Scout 10:03* (check) — or *refused: reason* (amber, warning icon) — or *needs a newer Scout* (disabled, info icon). A confirm sheet appears only when the catalogue entry has a confirm sentence (e.g. symbol off with pending orders, timeframe change).

The row must work at phone width (control under the label) and desktop width (control right-aligned), and stack any number of rows in a group without looking crowded.

### 7.3 View-only state

For viewers, every control (7.1, 7.2) is shown in its current state but **disabled**, with a lock icon and "View only" on hover/tap — no hidden controls (viewers should see what the owner has set), no error after a tap. Design it for the keep/skip control, a switch, a choice and a number.

### 7.4 Other components

Freshness pill (3 states) · status chips for orders (Pending, Filled, Expired, Cancelled, Skipped, Closed +/−, Refused) · bot state dot (Running green, Stopped grey, Failed red, Waiting blue) · owner tags (Scout, TM, Manual — three distinct neutral hues, not green/red) · role chips (MAIN, MIR, HMAIN, HMIR) · profit figure (+$12.40 green, −$8.15 red, always with sign) · direction marker (↑ Buy / ↓ Sell, not coloured green/red, to keep green/red for money) · thumbnail with tap-to-zoom · day picker · empty states · stale-data banner · toast.

## 8. Colour and type

- **Reserved meanings:** green = keep / on / running / profit; red = loss / failed / refused; grey = skipped / off / stopped; amber = warning / against-trend / data getting old; blue = waiting / informational. Direction (buy/sell) uses arrows and neutral colour so it is never confused with profit/loss.
- Neutral base (cool greys), one restrained brand accent for navigation and focus (not green, not red).
- Harmonise with the order pictures' palette (teal / red candles, amber pause, blue level, rose stop-loss).
- Numbers: tabular figures, right-aligned in tables; prices with the symbol's digits (EURUSD 1.08452, USDJPY 152.418, XAUUSD 2 650.20, BTCUSD 69 420.5).
- AA contrast in both themes; focus rings visible; touch targets ≥ 44 px.

## 9. Content style

- Plain, short English. Times like "10:02 UTC" with local beside ("15:02 PKT"). Dates "Wed 04 Nov".
- Explain trading terms on first contact with a small ⓘ: *stop order, stop-loss, pip, lot, margin, free margin, equity, leverage, spread, swap, slippage, 1R, hedge, mirror*.
- Never say "trade now", "buy", "sell" as actions.

## 10. Out of scope

No order entry, no buy/sell or close buttons, no live price ticker, no options other than those the catalogue supplies (nothing typed free-form), no screens for machines (they use the API), no notifications settings.

## 11. Sample data (illustrative)

**Setups (IC Markets demo, 04-Nov-2026)**

| Time UTC | Symbol | Event | Side | M/R | Level | Stop-loss | Pips | Lots | Higher TF | State |
|---|---|---|---|---|---|---|---|---|---|---|
| 10:00 | GBPUSD | New | Buy stop | M | 1.35030 | 1.34546 | 48.4 | 0.10 | with ↑ | Keep · pending |
| 10:00 | XAUUSD | New | Sell stop | M | 2 650.20 | 2 661.80 | 116.0 | 0.01 | against ↑ | Skipped · applied 10:03 |
| 10:00 | BTCUSD | Moved | Buy stop | M | 69 420.5 | 68 910.0 | 510.5 | 0.02 | with ↑ | Keep · pending |
| 09:00 | GBPJPY | New | Buy stop | M | 212.686 | 212.474 | 21.2 | 0.10 | with ↑ | Too late · filled 09:41 |
| 09:00 | EURUSD | Cancelled | Sell stop | R | 1.08210 | 1.08460 | 25.0 | 0.10 | with ↓ | — (trend turned) |
| 08:00 | XNGUSD | New | Sell stop | M | 3.412 | 3.498 | 86.0 | 0.50 | with ↓ | Keep · pending |

**Orders closed today:** GBPJPY Buy 0.10 → closed +$12.40 net; ETHUSD Buy 0.05 → closed −$8.15 net; AUDJPY Sell 0.10 → expired.

**TM trade (GBPJPY, P692232259):** 09:41:02 MAIN filled 212.688 · 09:41:03 MIR Mirror placed 0.10 buy, 212 ms ✓ · 09:41:04 HMAIN Hedge placed sell-stop 212.474 ✓ · 09:41:04 HMIR Hedge placed ✓ · 10:15:20 MAIN Stop moved to break-even 212.688 ✓ · 10:15:21 HMAIN Hedge cancelled ✓.

**Accounts:** IC Markets demo — balance $10 182.40, equity $10 209.75, margin used $96.30, free margin $10 113.45, margin level 10 602 %, leverage 1:500, today +$4.25 (Scout +$4.25, TM $0.00, Manual $0.00). Pepperstone demo — balance $9 874.10, equity $9 874.10, leverage 1:200, no open positions.

**Conditions (IC Markets, illustrative):**

| Symbol | 1 lot = | Min / step / max | Margin per lot | Pip value per lot | Spread | Commission per lot | Swap L / S per night | Largest size now |
|---|---|---|---|---|---|---|---|---|
| EURUSD | 100 000 EUR | 0.01 / 0.01 / 200 | $217 | $10.00 | 0.1 pip ($1.00) | $7.00 | −$9.80 / +$3.10 | 46.60 |
| XAUUSD | 100 oz | 0.01 / 0.01 / 100 | $530 | $10.00 per $0.10 | 0.12 ($12.00) | $7.00 | −$45.20 / +$22.10 | 19.08 |
| BTCUSD | 1 BTC | 0.01 / 0.01 / 50 | $3 471 | $1.00 per $1 | 12.0 ($12.00) | $0.00 | −$58.00 / −$29.00 | 2.91 |
| XNGUSD | 10 000 MMBtu | 0.50 / 0.10 / 50 | $683 | $10.00 per $0.001 | 0.004 ($40.00) | $7.00 | −$12.00 / +$4.00 | 14.80 |

**Status:** Scout Running since 04 Nov 18:07, build `Scout-ae41f2c`, 0 errors, 2 warnings today · TM Running since 03 Nov 18:15, `TM-41ca4d0` · Audit Bot Stopped (run finished 04 Nov 18:11) · Audit report Waiting (next 00:15) · Data upload last 10:02 (3 s, 14 files), next 10:32 · Control sync last 10:05, next 10:06.

**Symbols (Scout, IC Markets):** Forex majors EURUSD, GBPUSD, USDJPY · Forex crosses GBPJPY, EURJPY, AUDJPY · Metals XAUUSD, XAGUSD, XPTUSD · Indices US500, USTEC, DE40 · Energies XTIUSD, XBRUSD, XNGUSD (off) · Crypto BTCUSD, ETHUSD, SOLUSD (off). All on 1H except **XAUUSD on 1D** (changed from configured 1H, applied 10:05) and **US500 changed to 4H, saved · waiting for laptop**.

**Options catalogue (today):** per symbol — `enabled` (switch, configured on), `timeframe` (choice H1 / H4 / D1, configured H1, confirm sentence, next candle); per setup — `decision` (keep / skip, §7.1). Use the three made-up general options of §6.5 to show the Settings page filled.

## 12. What to deliver from Claude Design

1. **Screens, phone (390 px) and desktop (1440 px), light and dark:** Sign in (with the M2M panel open), Access requested, Refused, Scout home (owner and viewer) (with a mix of card states from §11), Setup detail, Symbols (a row collapsed and expanded, with both confirm sheets), Settings (empty and filled), Trades, Accounts, Conditions (with the what-if panel open), Status, Audit, Users.
2. **Component sheet** with every state of 7.1, the setting row of 7.2 in all four types and all delivery states, the view-only state (7.3), role badges, chips, pills, tags, banners, empty states, toasts.
3. **Design tokens** as CSS custom properties (light and dark), and the type scale.
4. **Clickable prototype** of five flows: (a) sign in → Scout → skip a setup → *waiting* → *applied*; (b) switch a symbol off with pending orders → confirm → applied; (c) change a symbol's timeframe 1H → 1D → confirm → applied, and its next setup card shows "1D"; (d) Conditions → what-if with a size above "largest size now" (warning); (e) a new person signs in with Google → access requested → the owner approves on Users → the person sees Scout in view-only.
5. Exported **HTML/CSS** of the screens where possible, to be turned into PHP templates.

## 13. Glossary for the designer

| Term | Meaning |
|---|---|
| Symbol | A market: a currency pair (EURUSD), metal (XAUUSD = gold), index (US500), energy (XTIUSD = oil) or crypto (BTCUSD) |
| Setup | A pattern Scout found: a pause in a trend, with a level to enter beyond it |
| Stop order (buy stop / sell stop) | An order waiting above (buy) or below (sell) the price; it fills only if the price gets there |
| Main / Reversal (M/R) | Scout's normal order in the trend's direction / an order the other way after a failed move |
| Stop-loss | The price at which a losing trade is closed automatically |
| Pip | The smallest usual price step (EURUSD 0.0001) |
| Lot | The trade size unit; 1 lot of EURUSD = 100 000 euros |
| Margin / free margin | Money the broker holds for open trades / money still available for new ones |
| Equity | Balance plus the profit or loss of open trades |
| Leverage | Position size allowed per dollar of margin, e.g. 1:500 |
| Spread | Difference between buy and sell price — a cost |
| Swap | Overnight fee or credit for holding a position |
| Slippage | Difference between the requested and the actual fill price |
| Higher timeframe, with / against | The 4-hour trend; a setup in its direction is "with", the other way "against" |
| TM roles MAIN, MIR, HMAIN, HMIR | The trade, its mirror copy, the hedge of the main, the hedge of the mirror |
| Demo account | A practice account with play money at a real broker |

## 14. History

| Version | Date | Change |
|---|---|---|
| v1.0 | 04 Nov 2026 | First issue, for dashboard.md v1.1 |
| v1.1 | 05 Nov 2026 | Owner: extensible design. Controls generated from an options catalogue: generic setting rows in four types (§7.2), timeframe per symbol as the first new option (§6.4), a Settings page (§6.5), timeframe chips on setup cards, a fourth prototype flow; for dashboard.md v1.2 |
| v1.2 | 05 Nov 2026 | Owner: viewers (read only, approved by the owner) and machine sign-in (M2M, for Claude): sign-in options list with Google and M2M (§6.1), access-requested and refused states, Users page (§6.11), view-only state of controls (§7.3), role badges, a fifth prototype flow; for dashboard.md v1.3 |
