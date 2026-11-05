# Dashboard design — handoff to code

The design result of build step 2 ([dashboard.md §8](../dashboard.md)), made in Claude Design from the [design brief](../dashboard-design-brief.md) v1.3 on 05-Nov-2026. It is the reference for the screens of step 3. Where this design and `dashboard.md` disagree, `dashboard.md` wins, so stop and ask.

| Item | Value |
|---|---|
| Canvas (owner only) | Claude Design canvas "Trader Dashboard". The source files are copied here, so the canvas link is not needed for coding |
| Scope | **Desktop web (1440 px) only.** The owner decided on 05-Nov-2026 that phone layouts wait. `ScoutPhone.dc.html` is kept as a reference for later |
| Tokens | [`tokens.css`](tokens.css): light and dark sets, type scale, spacing, radii |
| Screens | [`screens/`](screens/): one `.dc.html` file per screen (format below), plus `canvas.json` (the canvas layout) |

## 1. Files

| File | Screen | Route | Brief |
|---|---|---|---|
| `SignIn.dc.html` | Sign in: Google button, M2M panel, signing in / access requested / refused / session expired | `/` (signed out) | §6.1 |
| `Main.dc.html` | Scout home: last-hour strip, setup cards with Keep/Skip, detail side panel, orders of the day, undo toast, loading/empty/error | `/scout` | §6.2, §6.3 |
| `Symbols.dc.html` | Symbols: grouped rows, timeframe choice, on/off, expanded setting rows, both confirm sheets | `/symbols` | §6.4 |
| `Settings.dc.html` | Settings: catalogue-driven rows grouped by scope; empty state | `/settings` | §6.5 |
| `Trades.dc.html` | Trades: open now, history by trade with TM's action timeline, filters | `/trades` | §6.6 |
| `Accounts.dc.html` | Accounts: headline numbers, today by owner, balance & equity line, profit-per-day bars (with a table view) | `/accounts` | §6.7 |
| `Conditions.dc.html` | Conditions table and the working What-if calculator | `/conditions` | §6.8 |
| `Status.dc.html` | Bot tiles, warnings and errors, uploads, control history | `/status` | §6.9 |
| `Audit.dc.html` | IC Markets vs Pepperstone side by side, better cell marked | `/audit` | §6.10 |
| `Users.dc.html` | Access requests, people, machines (revoke with confirm), sign-in log | `/users` (owner only) | §6.11 |
| `Components.dc.html` | Component sheet: tokens, type scale, every Keep/Skip state, setting rows in 4 types × all delivery states, view-only, data-block states, chips, pills, banners | — | §7, §12.2 |
| `ScoutPhone.dc.html` | Scout home on a phone: reference only, not in scope now | — | — |

### How to read a `.dc.html` file

Each file is one self-contained HTML page in Claude Design's format:

- The markup is inside `<x-dc>`. Styles are **inline** (`style="…"`) and use `var(--token)`. A few shared classes (`.card`, `.seg`, `.sw` …) are defined in the `<helmet><style>` block at the top. Every file repeats the token values there; `tokens.css` is the single clean copy to use in code.
- `{{name}}` inserts a value. `<sc-for list="{{items}}" as="x">` repeats its contents for each item, and `<sc-if value="{{flag}}">` shows its contents only when the flag is true.
- The `<script type="text/x-dc">` block at the bottom holds the behaviour as one `class Component` with `state`, handlers and `renderVals()`. It is close to a React class component, and it also holds the **sample data** each screen uses.
- `data-props` on that script lists the screen's **tweaks**, which are the variants shown on the canvas: `theme` (light/dark), `role` (owner/viewer), `freshness`, `dataState`, `content`, `confirm`, `screen`, `brokerChoice`. Each tweak is a state the real component must support.

**Translate them into React + TypeScript components.** Don't load the `.dc.html` files at runtime.

## 2. Build these shared components first

Every screen is made from these pieces. Build each one once, typed, styled only with tokens. Use no UI library (dashboard.md §4.1: keep dependencies few).

| Component | What it does | Seen in |
|---|---|---|
| `AppShell` | Left sidebar (sections, Scout badge, Users badge for the owner only) and the top bar: freshness pill, account selector, UTC/Local toggle, "View only" badge, user menu with role badge | every screen |
| `FreshnessPill` | Green under 45 min, amber 45 min–2 h, red over 2 h ("Laptop offline? Last data 3 h ago"), each with an icon | top bar |
| `DataBlock` | The wrapper for **every** block that is fetched from the API: a header with title and "Updated HH:MM · next HH:MM" (plus Refresh where useful), then one body: `loading` (skeleton), `ready`, `refreshing` (old content stays, header says "Updating…"), `empty` (message), `error` (message and Try again), `stale` (content with the age beside it) | Components → "Data block states"; Main |
| `KeepSkip` | Segmented "Keep \| Skip" for one setup with every state of brief §7.1: keep, skip saved, skip applied, keep saved/applied, too late (locked), not available, refused. One tap; no dialog; an Undo toast for 5 s; a skipped card is greyed, never hidden | Main, Components |
| `SettingRow` | One catalogue entry (dashboard.md §3.4) drawn as `switch`, `choice` (segmented for ≤ 4 values, else dropdown), `multi` (toggle chips) or `number` (stepper with min/max/step). It shows label, ⓘ help, the configured value and when it takes effect, the delivery state, a "changed" marker with Reset to configured, and a confirm sheet only when the entry has a `confirm` sentence | Symbols, Settings, Components |
| `DeliveryState` | The shared status line: *as configured* → *saving…* → *saved · waiting for laptop* (clock) → *applied by Scout HH:MM* (check) / *refused: reason* (amber, warning) / *needs a newer Scout* (disabled, info) | KeepSkip, SettingRow |
| `ViewOnly` | For viewers every control shows its current value but is disabled, with a lock and "View only · set by owner". Never hidden, and a tap never shows an error | Main, Symbols, Settings |
| `ConfirmSheet` | A modal with title, the catalogue's sentence, Cancel and an action button. A sheet on phone, a centred dialog on desktop | Symbols, Users |
| `Toast` | Dark bar at the bottom with an Undo action, lasting 5 s | Main |
| `OrderPicture` | The fixed white SVG (720 × 400) inside a `--frame` mat with 4–5 px padding, so it sits well on a dark page. Thumbnail about 200 × 111 on desktop; tap opens the detail | Main |
| Chips and tags | Order status (Pending, Filled, Expired, Cancelled, Skipped, Closed ±, Refused), bot state dot (Running ● / Stopped ○ / Failed ◆ / Waiting clock), owner tags (Scout, TM, Manual), TM role chips (MAIN, MIR filled; HMAIN, HMIR outlined), timeframe chip (1H/4H dark, **1D accent** so it stands out), trend tag (With / Against with ▲ amber), profit figure (always signed: +$12.40 green, −$8.15 red), direction (↑ Buy stop / ↓ Sell stop, **neutral colour**) | Components |
| `Switch`, `Segmented`, `Stepper`, `DayPicker` | Basic controls with real `<button role="switch" aria-checked>`, `role="radiogroup"` and `aria-label`. Every touch target is at least 44 px | everywhere |
| `LineChart`, `BarChart` | Plain inline SVG. Balance is a solid accent line and equity a dashed `--text-2` line; there is a legend; light gridlines; one y-axis. Bars are `--chart-pos` above zero and `--chart-neg` below, and every chart has a **table view** toggle | Accounts |

Suggested types (to align with `openapi.yaml` once it exists; the generated types win):

```ts
type Delivery =
  | { kind: "configured" }
  | { kind: "saving" }
  | { kind: "waiting"; savedUtc: string }
  | { kind: "applied"; appliedUtc: string; note?: string }   // note: "1 order cancelled"
  | { kind: "refused"; reason: string }
  | { kind: "needsNewerScout"; since: string };

type CatalogueEntry = {
  key: string; scope: "scout" | "account" | "symbol" | "setup";
  type: "switch" | "choice" | "multi" | "number";
  values?: string[]; min?: number; max?: number; step?: number; unit?: string;
  label: string; help: string; confirm?: string; applies: "now" | "nextCandle"; since: string;
};

type BlockStatus = "loading" | "ready" | "refreshing" | "empty" | "error" | "stale";
```

## 3. Behaviour to keep

- **Five-second scan (Scout):** newest first; setups grouped by candle close ("10:00 UTC", or "Daily candle 00:00" for 1D); the last hour's group has an accent rail and a "Last hour" tag.
- **Controls save in place** (no page reload). Optimistic: the control flips at once and the delivery line shows *waiting for laptop* until the acknowledgement arrives on the next sync (dashboard.md §3.4).
- **Refresh in place** every 30 min and on demand. Keep the old content while refreshing; never blank the page.
- **Confirm only when the catalogue says so**: a symbol switched off while it has pending orders, or a timeframe change. Keep/Skip never asks.
- **What-if is pure calculation** in the browser on the uploaded numbers and sends nothing. The stepper respects min/step/max; above "largest size now" it shows an amber warning. Money at risk = SL pips × pip value × lots + spread + commission.
- **Times:** UTC by default with local time (PKT) beside it; the toggle swaps them. Dates look like "Wed 04 Nov".
- **Explain terms** with a small ⓘ on first contact (brief §9): stop order, stop-loss, pip, lot, margin, free margin, equity, leverage, spread, swap, slippage, hedge, mirror.
- **Never** a buy/sell/close button, and never the words "trade now", "buy" or "sell" used as actions.

## 4. Accessibility (as drawn)

Body text contrast is at least 4.5:1 in both themes; `--text-3` was chosen to pass on `--surface` and `--bg`. The focus ring is a 2 px accent outline. Colour is never the only signal: each coloured state also has an icon or a word. Icon-only buttons have an `aria-label`, and decorative SVGs have `aria-hidden`. Live status text uses `role="status"` or `aria-live="polite"`. Tables have `scope` on header cells, and wide tables scroll inside their own box with the first column pinned (`position: sticky`).

## 5. Sample data — replace with the API

The screens use the brief's §11 sample data. These values are **made up**, so the API must supply the real ones:

- Audit figures (all of `Audit.dc.html`)
- the 18-day balance history and the equity offsets (Accounts)
- the XAUUSD leverage tiers popover (Conditions)
- the two Scout warning messages (Status)
- the open GBPJPY mirror at +$27.35 (Trades and Accounts; it is equity minus balance)
- the order IDs other than P692232259
- the AUDJPY/ETHUSD prices, which show as `[price]` and `[level]` placeholders
- the client ID `m2m_claude_7f3a9c21`
- the sign-in log addresses, which are documentation IP ranges
- "owner@gmail.com" and "viewer@gmail.com", which stand in for real addresses

## 6. Gaps for step 3 to close

1. **Viewer state.** The controls that steer Scout are on Scout, Symbols and Settings, and those screens have the `role` tweak. Viewers never see Users, and its nav item is hidden for them. Filters and view toggles (account, day, chart period, UTC/Local) stay usable for viewers, because they change nothing.
2. **Loading, empty and error states** are drawn on Scout and on the component sheet. Use `DataBlock` for every block on every page.
3. **Flow (c)** (brief §12.4): after a timeframe change is applied, the symbol's *next* setup card must show the new chip ("1D") and sit under "Daily candle 00:00".
4. **Flows (a) and (e)** work screen by screen. In the app they follow from real routing: sign in → Scout → skip → waiting → applied; and access requested → owner approves on Users → the viewer sees Scout in view-only.
5. **Dark mode toggle:** the canvas uses a tweak. In the app, follow the system setting and add a manual toggle that sets `data-theme` on `<html>` (see `tokens.css`).
6. **Fonts:** Inter and JetBrains Mono. Self-host them with the static export instead of loading Google Fonts at runtime, which keeps pages light on mobile data.
7. **Google button:** the canvas shows a placeholder where the "G" logo goes. Use Google's official button assets and style rules.
