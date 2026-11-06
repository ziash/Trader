"use client";

// Trading conditions (dashboard.md §2.5, Conditions.dc.html): per symbol and per lot in USD, as the broker stated them at the last
// snapshot — leverage, lot, sizes, margin, pip value, spread, commission, swap — and the largest size the free margin allows.
// The "what if" box works on these numbers in the browser; it sends nothing anywhere.
import { useMemo, useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock, NotYet } from "@/components/DataBlock";
import { Chip } from "@/components/Chips";
import { usePrefs } from "@/lib/prefs";
import { useAccount, useStatus } from "@/lib/status";
import { useData } from "@/lib/useData";
import { parseUtc, shortStamp, zoneLabel } from "@/lib/time";
import type { ConditionsRow, ConditionsView, MarginPoint } from "@/api/types";
import { market } from "./ScoutScreen";
import { money } from "./AccountsScreen";

export function ConditionsScreen() {
  return (
    <AppShell>
      <ConditionsBody />
    </AppShell>
  );
}

const fmt = (v: number | null | undefined, d = 2) => (v == null ? "—" : v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d }));
const signed = (v: number | null | undefined) => (v == null ? "—" : `${v < 0 ? "−" : "+"}$${fmt(Math.abs(v))}`);
const lots = (v: number) => v.toLocaleString("en-US", { maximumFractionDigits: 8 });

/** The broker's margin for a size, straight between its curve points and from zero (as the laptop reads the largest size). */
export function marginFor(points: MarginPoint[], size: number, side: "buy" | "sell"): number | null {
  if (points.length === 0 || !(size > 0)) return null;
  let prev = { lots: 0, m: 0 };
  for (const p of points) {
    const m = (side === "buy" ? p.buy : p.sell) ?? 0;
    const at = p.lots ?? 0;
    if (size <= at) return prev.m + ((m - prev.m) * (size - prev.lots)) / (at - prev.lots || 1);
    prev = { lots: at, m };
  }
  return prev.lots > 0 ? (prev.m * size) / prev.lots : null;
}

export type WhatIf = { margin: number | null; pipValue: number | null; spread: number | null; commission: number | null; swapNight: number | null; risk: number | null };

/** The "what if" figures for a size (lots) and a stop-loss distance, in pips or in price. */
export function whatIf(r: ConditionsRow, size: number, side: "buy" | "sell", distance: number, unit: "pips" | "price"): WhatIf {
  const pipValue = r.pip_value_per_lot_usd == null ? null : r.pip_value_per_lot_usd * size;
  const pips = unit === "pips" ? distance : distance / r.pip_size;
  return {
    margin: marginFor(r.margin, size, side),
    pipValue,
    spread: r.spread_per_lot_usd == null ? null : r.spread_per_lot_usd * size,
    commission: r.commission_per_lot_usd == null ? null : r.commission_per_lot_usd * size,
    swapNight: (side === "buy" ? r.swap_long_per_lot_usd : r.swap_short_per_lot_usd) == null ? null : (side === "buy" ? r.swap_long_per_lot_usd! : r.swap_short_per_lot_usd!) * size,
    risk: pipValue == null || !(pips > 0) ? null : pips * pipValue,
  };
}

function ConditionsBody() {
  const { account } = useAccount();
  const status = useStatus();
  const { zone } = usePrefs();
  const path = account ? `/api/conditions?account=${encodeURIComponent(account.id)}` : null;
  const data = useData<ConditionsView>(path, { isEmpty: (d) => !d.available });
  const d = data.data;

  if (status.status !== "loading" && !account) {
    return (
      <>
        <PageHead title="Conditions" />
        <NotYet title="Trading conditions"><strong>No data uploaded yet</strong></NotYet>
      </>
    );
  }
  return (
    <>
      <PageHead title="Conditions" note={<>{account?.name ?? "…"}{d?.snapshot_utc ? <> · as the broker stated them at {shortStamp(parseUtc(d.snapshot_utc), zone)} {zoneLabel(zone)}</> : null}</>} />
      <DataBlock<ConditionsView> title="Trading conditions per lot" data={data}
        empty={<><strong>No conditions yet</strong><span className="upd">The laptop asks the broker for them at each upload (:02 and :32).</span></>}
        actions={d?.available ? <span className="upd num">Free margin {money(d.free_margin)} · level {d.margin_level_pct == null ? "—" : `${Math.round(d.margin_level_pct)} %`} · account {d.leverage}</span> : null}>
        {(x) => <Table rows={x.symbols} />}
      </DataBlock>
      {d?.available && <WhatIfBox rows={d.symbols} free={d.free_margin} />}
    </>
  );
}

function Table({ rows }: { rows: ConditionsRow[] }) {
  return (
    <div className="tbl-wrap">
      <table className="tbl cond">
        <thead>
          <tr>
            <th scope="col">Symbol</th>
            <th scope="col" className="r">Largest<br />size now</th>
            <th scope="col">Leverage</th>
            <th scope="col">1 lot is</th>
            <th scope="col" className="r">Min / step / max lots</th>
            <th scope="col" className="r">Margin per lot<br />buy / sell</th>
            <th scope="col" className="r">Pip value<br />per lot</th>
            <th scope="col" className="r">Spread now<br />pips · per lot</th>
            <th scope="col" className="r">Commission<br />open + close</th>
            <th scope="col" className="r">Swap per night<br />long / short</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.symbol}>
              <td>
                <div className="two">
                  <span style={{ fontWeight: 600 }}>{r.symbol}</span>
                  <span className="upd">{market(r.symbol)}{!r.market_open && <> · <Chip tone="grey">closed</Chip></>}</span>
                </div>
              </td>
              <td className="num r" style={{ fontWeight: 600 }}>{r.largest_lots_now == null ? "—" : lots(r.largest_lots_now)}</td>
              <td className="num">
                <div className="two">
                  <span>{r.symbol_leverage || "—"}</span>
                  <span className="upd">{r.tiers.length > 1 ? `tiers ${r.tiers.join(", ")}` : `account ${r.account_leverage}`}</span>
                </div>
              </td>
              <td className="num">{lots(r.lot_units)} {r.lot_asset}</td>
              <td className="num r">{lots(r.min_lots)} / {lots(r.step_lots)} / {lots(r.max_lots)}</td>
              <td className="num r">${fmt(r.margin_buy_per_lot_usd)}<br /><span className="upd">${fmt(r.margin_sell_per_lot_usd)}</span></td>
              <td className="num r">${fmt(r.pip_value_per_lot_usd, r.pip_value_per_lot_usd != null && r.pip_value_per_lot_usd < 0.1 ? 4 : 2)}<br /><span className="upd">pip {r.pip_size}</span></td>
              <td className="num r">{r.market_open ? <>{fmt(r.spread_pips, 1)}<br /><span className="upd">${fmt(r.spread_per_lot_usd)}</span></> : <span className="upd">closed</span>}</td>
              <td className="num r">${fmt(r.commission_per_lot_usd)}</td>
              <td className="num r">
                <span style={{ color: (r.swap_long_per_lot_usd ?? 0) < 0 ? "var(--red)" : undefined }}>{signed(r.swap_long_per_lot_usd)}</span><br />
                <span style={{ color: (r.swap_short_per_lot_usd ?? 0) < 0 ? "var(--red)" : undefined }}>{signed(r.swap_short_per_lot_usd)}</span>
                {r.triple_swap_day && r.triple_swap_day !== "None" && <><br /><span className="upd">×3 on {r.triple_swap_day}</span></>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="upd" style={{ padding: "8px 16px", margin: 0 }}>
        Largest size now: the free margin read against the broker’s own margin figures, down to the size step, for a new position on its own.
        On a hedged account an opposite position can need less. Swap on crypto is a yearly % of the value ÷ 360.
      </p>
    </div>
  );
}

function WhatIfBox({ rows, free }: { rows: ConditionsRow[]; free: number | null | undefined }) {
  const [symbol, setSymbol] = useState(rows[0]?.symbol ?? "");
  const [size, setSize] = useState("0.1");
  const [side, setSide] = useState<"buy" | "sell">("buy");
  const [distance, setDistance] = useState("20");
  const [unit, setUnit] = useState<"pips" | "price">("pips");
  const r = rows.find((x) => x.symbol === symbol) ?? rows[0];
  const n = parseFloat(size);
  const w = useMemo(() => (r ? whatIf(r, n, side, parseFloat(distance), unit) : null), [r, n, side, distance, unit]);
  if (!r) return null;
  const offStep = n > 0 && Math.abs(n / r.step_lots - Math.round(n / r.step_lots)) > 1e-6;
  const tooSmall = n > 0 && n < r.min_lots;
  const tooBig = n > r.max_lots;
  return (
    <section className="card" aria-labelledby="whatif-h">
      <div className="ch">
        <h2 id="whatif-h">What if</h2>
        <span className="upd">Works on the numbers above, in your browser; sends nothing.</span>
      </div>
      <form className="whatif" onSubmit={(e) => e.preventDefault()}>
        <label>Symbol
          <select value={r.symbol} onChange={(e) => setSymbol(e.target.value)}>{rows.map((x) => <option key={x.symbol}>{x.symbol}</option>)}</select>
        </label>
        <label>Side
          <select value={side} onChange={(e) => setSide(e.target.value as "buy" | "sell")}><option value="buy">Buy</option><option value="sell">Sell</option></select>
        </label>
        <label>Size (lots)
          <input inputMode="decimal" value={size} onChange={(e) => setSize(e.target.value)} aria-describedby="whatif-note" />
        </label>
        <label>Stop-loss distance
          <input inputMode="decimal" value={distance} onChange={(e) => setDistance(e.target.value)} />
        </label>
        <label>in
          <select value={unit} onChange={(e) => setUnit(e.target.value as "pips" | "price")}><option value="pips">pips</option><option value="price">price (e.g. 0.122)</option></select>
        </label>
      </form>
      <dl className="money whatif-out">
        <div><dt>Margin</dt><dd className="num">{money(w?.margin)}</dd>
          {w?.margin != null && free != null && (
            <span className="upd" style={{ color: w.margin > free ? "var(--red)" : undefined }}>{w.margin > free ? "more than the free margin" : `of ${money(free)} free`}</span>
          )}</div>
        <div><dt>Pip value</dt><dd className="num">{money(w?.pipValue)}</dd><span className="upd">per pip of {r.pip_size}</span></div>
        <div><dt>Spread to pay now</dt><dd className="num">{r.market_open ? money(w?.spread) : "closed"}</dd></div>
        <div><dt>Commission</dt><dd className="num">{money(w?.commission)}</dd><span className="upd">open and close</span></div>
        <div><dt>Swap per night</dt><dd className="num">{signed(w?.swapNight)}</dd></div>
        <div><dt>At risk to the stop-loss</dt><dd className="num" style={{ color: "var(--red)" }}>{w?.risk == null ? "—" : `−${money(w.risk)}`}</dd><span className="upd">plus spread and commission</span></div>
      </dl>
      <p id="whatif-note" className="upd" style={{ padding: "0 16px 14px", margin: 0 }}>
        {tooSmall ? `Below the smallest order (${lots(r.min_lots)} lot). ` : ""}
        {offStep ? `Not a multiple of the ${lots(r.step_lots)}-lot step. ` : ""}
        {tooBig ? `Above the largest single order (${lots(r.max_lots)} lots). ` : ""}
        {r.largest_lots_now != null ? `The free margin allows up to ${lots(r.largest_lots_now)} lot${r.largest_lots_now === 1 ? "" : "s"} now.` : ""}
      </p>
    </section>
  );
}
