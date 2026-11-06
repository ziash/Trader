"use client";

// Accounts (dashboard.md §2.4, Accounts.dc.html): the account's money and margin level now, what is open by owner, today's
// result by owner, profit per day and the balance / equity lines, each chart with a table view. From the laptop's snapshot (§3.2).
import { useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock, NotYet } from "@/components/DataBlock";
import { Chip, type Tone } from "@/components/Chips";
import { usePrefs } from "@/lib/prefs";
import { useAccount, useStatus } from "@/lib/status";
import { useData } from "@/lib/useData";
import { dayLabel, parseUtc, shortStamp, zoneLabel } from "@/lib/time";
import type { AccountView } from "@/api/types";
import { usd } from "./ScoutScreen";

export function AccountsScreen() {
  return (
    <AppShell>
      <AccountsBody />
    </AppShell>
  );
}

export const money = (v: number | null | undefined) => (v == null ? "—" : `${v < 0 ? "−" : ""}$${Math.abs(v).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`);

/** TM's levels (tm.md §5.1): below the rescue level TM closes trades; below the warning level it opens no mirrors. */
export function levelTone(level: number | null | undefined, rescue: number, warn: number): { tone: Tone; text: string } {
  if (level == null) return { tone: "plain", text: "No positions" };
  if (level < rescue) return { tone: "red", text: `Below ${rescue} % · TM closes trades (margin rescue)` };
  if (level < warn) return { tone: "amber", text: `Below ${warn} % · TM opens no mirrors` };
  return { tone: "green", text: "Enough margin" };
}

function AccountsBody() {
  const { account } = useAccount();
  const status = useStatus();
  const { zone } = usePrefs();
  const path = account ? `/api/accounts?account=${encodeURIComponent(account.id)}` : null;
  const data = useData<AccountView>(path, { isEmpty: (d) => !d.available });

  if (status.status !== "loading" && !account) {
    return (
      <>
        <PageHead title="Accounts" />
        <NotYet title="Account"><strong>No data uploaded yet</strong></NotYet>
      </>
    );
  }
  const d = data.data;
  return (
    <>
      <PageHead title="Accounts" note={<>{account?.name ?? "…"}{d?.snapshot_utc ? <> · snapshot {shortStamp(parseUtc(d.snapshot_utc), zone)} {zoneLabel(zone)}</> : null}</>} />
      <DataBlock<AccountView> title="Money now" data={data}
        empty={<><strong>No account snapshot yet</strong><span className="upd">The laptop asks cTrader for it at each upload (:02 and :32).</span></>}>
        {(x) => <MoneyNow d={x} />}
      </DataBlock>
      {d?.available && (
        <div className="grid-2">
          <OpenByOwner d={d} />
          <Today d={d} />
        </div>
      )}
      {d?.available && <BalanceChart d={d} />}
      {d?.available && <PerDayChart d={d} />}
    </>
  );
}

function MoneyNow({ d }: { d: AccountView }) {
  const s = d.status!;
  const lvl = levelTone(s.margin_level_pct, d.levels.rescue_pct ?? 100, d.levels.warn_pct ?? 300);
  const color = lvl.tone === "red" ? "var(--red)" : lvl.tone === "amber" ? "var(--amber)" : "var(--green)";
  const width = Math.min(100, ((s.margin_level_pct ?? 0) / 500) * 100);
  return (
    <>
      <dl className="money">
        <div><dt>Balance</dt><dd className="num">{money(s.balance)}</dd></div>
        <div><dt>Equity</dt><dd className="num">{money(s.equity)}</dd><span className="upd num">{usd(s.equity - s.balance)} open</span></div>
        <div><dt>Margin used</dt><dd className="num">{money(s.margin_used)}</dd></div>
        <div><dt>Free margin</dt><dd className="num" style={{ color: s.free_margin < 0 ? "var(--red)" : undefined }}>{money(s.free_margin)}</dd></div>
        <div>
          <dt>Margin level</dt>
          <dd className="num" style={{ color }}>{s.margin_level_pct == null ? "—" : `${Math.round(s.margin_level_pct)} %`}</dd>
          <div className="level-bar" aria-hidden="true"><span style={{ width: `${width}%`, background: color }} /></div>
        </div>
        <div><dt>Open</dt><dd className="num">{s.positions} <span className="upd">positions</span></dd><span className="upd num">{s.pending_orders} orders · {s.leverage}</span></div>
      </dl>
      <div className="ch" style={{ borderTop: "1px solid var(--border)", borderBottom: 0 }}>
        <Chip tone={lvl.tone}>{lvl.text}</Chip>
        <span className="upd">Margin level = equity ÷ margin used. IC Markets closes positions itself at 50 %.</span>
      </div>
    </>
  );
}

function OpenByOwner({ d }: { d: AccountView }) {
  return (
    <section className="card" aria-labelledby="open-h">
      <div className="ch"><h2 id="open-h">Open now, by owner</h2></div>
      {d.open_by_owner.length === 0 ? <p className="blkb empty" style={{ margin: 0 }}>Nothing open.</p> : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead><tr><th scope="col">Owner</th><th scope="col" className="r">Positions</th><th scope="col" className="r">Orders</th><th scope="col" className="r">Open result</th></tr></thead>
            <tbody>
              {d.open_by_owner.map((r) => (
                <tr key={r.owner}>
                  <td style={{ fontWeight: 600 }}>{r.owner}</td>
                  <td className="num r">{r.positions}</td>
                  <td className="num r">{r.orders}</td>
                  <td className="num r" style={{ color: (r.open_result_usd ?? 0) < 0 ? "var(--red)" : "var(--green)" }}>{usd(r.open_result_usd)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function Today({ d }: { d: AccountView }) {
  const t = d.today;
  return (
    <section className="card" aria-labelledby="today-h">
      <div className="ch">
        <h2 id="today-h">Closed today, by owner</h2>
        <span className="upd">{t.day ? dayLabel(parseUtc(`${t.day} 00:00:00`), "utc") : ""} UTC · net of swap and commission</span>
        <span style={{ flex: 1 }} />
        <strong className="num" style={{ color: (t.net_usd ?? 0) < 0 ? "var(--red)" : "var(--green)" }}>{usd(t.net_usd)}</strong>
      </div>
      {(t.by_owner ?? []).length === 0 ? <p className="blkb empty" style={{ margin: 0 }}>Nothing closed today.</p> : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead><tr><th scope="col">Owner</th><th scope="col" className="r">Closes</th><th scope="col" className="r">Net</th></tr></thead>
            <tbody>
              {(t.by_owner ?? []).map((r) => (
                <tr key={r.owner}>
                  <td style={{ fontWeight: 600 }}>{r.owner}</td>
                  <td className="num r">{r.closes}</td>
                  <td className="num r" style={{ color: (r.net_usd ?? 0) < 0 ? "var(--red)" : "var(--green)" }}>{usd(r.net_usd)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function TableToggle({ table, setTable }: { table: boolean; setTable: (v: boolean) => void }) {
  return (
    <div className="seg" role="group" aria-label="Chart or table">
      <button type="button" className={table ? "" : "on"} aria-pressed={!table} onClick={() => setTable(false)}>Chart</button>
      <button type="button" className={table ? "on" : ""} aria-pressed={table} onClick={() => setTable(true)}>Table</button>
    </div>
  );
}

const W = 900, H = 240, PAD_L = 64, PAD_R = 12, PAD_T = 12, PAD_B = 28;

function scale(values: number[]) {
  let lo = Math.min(...values), hi = Math.max(...values);
  if (!(hi > lo)) { hi += 1; lo -= 1; }
  const pad = (hi - lo) * 0.08;
  return { lo: lo - pad, hi: hi + pad, y: (v: number) => PAD_T + ((hi + pad - v) / (hi - lo + 2 * pad)) * (H - PAD_T - PAD_B) };
}

function Grid({ lo, hi, y }: { lo: number; hi: number; y: (v: number) => number }) {
  const ticks = [0, 1, 2, 3, 4].map((i) => lo + ((hi - lo) * i) / 4);
  return (
    <g>
      {ticks.map((t) => (
        <g key={t}>
          <line x1={PAD_L} x2={W - PAD_R} y1={y(t)} y2={y(t)} stroke="var(--grid, var(--border))" />
          <text x={PAD_L - 6} y={y(t) + 4} textAnchor="end" fontSize="11" fill="var(--text-3)" className="num">{Math.round(t).toLocaleString("en-US")}</text>
        </g>
      ))}
    </g>
  );
}

function BalanceChart({ d }: { d: AccountView }) {
  const [table, setTable] = useState(false);
  const { zone } = usePrefs();
  const bal = d.balance.map((p) => ({ t: parseUtc(p.time_utc)!.getTime(), v: p.balance_usd! }));
  const eq = d.equity.filter((p) => p.equity_usd != null).map((p) => ({ t: parseUtc(p.time_utc)!.getTime(), v: p.equity_usd! }));
  const all = [...bal, ...eq];
  return (
    <section className="card" aria-labelledby="bal-h">
      <div className="ch">
        <h2 id="bal-h">Balance and equity</h2>
        <span className="upd">Balance after each close (the broker’s figure); equity from each snapshot (every 30 min, from {eq.length ? shortStamp(new Date(eq[0].t), zone) : "the first snapshot"})</span>
        <span style={{ flex: 1 }} />
        <TableToggle table={table} setTable={setTable} />
      </div>
      {all.length === 0 ? <p className="blkb empty" style={{ margin: 0 }}>No history yet.</p> : table ? (
        <div className="tbl-wrap" style={{ maxHeight: 320, overflowY: "auto" }}>
          <table className="tbl">
            <thead><tr><th scope="col">Time {zoneLabel(zone)}</th><th scope="col">Figure</th><th scope="col" className="r">USD</th></tr></thead>
            <tbody>
              {[...bal.map((p) => ({ ...p, k: "Balance" })), ...eq.map((p) => ({ ...p, k: "Equity" }))].sort((a, b) => b.t - a.t).map((p, i) => (
                <tr key={i}><td className="num">{shortStamp(new Date(p.t), zone)}</td><td>{p.k}</td><td className="num r">{money(p.v)}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <LineChart bal={bal} eq={eq} />
      )}
    </section>
  );
}

function LineChart({ bal, eq }: { bal: { t: number; v: number }[]; eq: { t: number; v: number }[] }) {
  const all = [...bal, ...eq];
  const t0 = Math.min(...all.map((p) => p.t)), t1 = Math.max(...all.map((p) => p.t), t0 + 1);
  const x = (t: number) => PAD_L + ((t - t0) / (t1 - t0)) * (W - PAD_L - PAD_R);
  const s = scale(all.map((p) => p.v));
  const path = (pts: { t: number; v: number }[], step: boolean) =>
    pts.map((p, i) => (i === 0 ? `M${x(p.t)} ${s.y(p.v)}` : step ? `H${x(p.t)} V${s.y(p.v)}` : `L${x(p.t)} ${s.y(p.v)}`)).join(" ");
  const days: number[] = [];
  for (let t = Math.ceil(t0 / 86400000) * 86400000; t <= t1; t += 86400000) days.push(t);
  return (
    <>
      <div className="chart-legend">
        <span><i style={{ borderColor: "var(--accent)" }} />Balance</span>
        <span><i style={{ borderColor: "var(--text-2)", borderTopStyle: "dashed" }} />Equity</span>
      </div>
      <div className="chart-box">
        <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Balance and equity over time">
          <Grid {...s} />
          {days.map((t) => (
            <text key={t} x={x(t) + 3} y={H - 8} fontSize="11" fill="var(--text-3)">{new Date(t).toISOString().slice(5, 10)}</text>
          ))}
          {bal.length > 0 && <path d={path(bal, true)} fill="none" stroke="var(--accent)" strokeWidth="2" />}
          {eq.length > 0 && <path d={path(eq, false)} fill="none" stroke="var(--text-2)" strokeWidth="1.5" strokeDasharray="5 4" />}
          {eq.length === 1 && <circle cx={x(eq[0].t)} cy={s.y(eq[0].v)} r="3" fill="var(--text-2)" />}
        </svg>
      </div>
    </>
  );
}

function PerDayChart({ d }: { d: AccountView }) {
  const [table, setTable] = useState(false);
  const rows = d.per_day;
  return (
    <section className="card" aria-labelledby="perday-h">
      <div className="ch">
        <h2 id="perday-h">Profit per day</h2>
        <span className="upd">UTC days · every close and commission, all owners</span>
        <span style={{ flex: 1 }} />
        <TableToggle table={table} setTable={setTable} />
      </div>
      {rows.length === 0 ? <p className="blkb empty" style={{ margin: 0 }}>No deals yet.</p> : table ? (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead><tr><th scope="col">Day</th><th scope="col" className="r">Closes</th><th scope="col" className="r">Net</th></tr></thead>
            <tbody>
              {[...rows].reverse().map((r) => (
                <tr key={r.day}><td className="num">{r.day}</td><td className="num r">{r.closes}</td>
                  <td className="num r" style={{ color: (r.net_usd ?? 0) < 0 ? "var(--red)" : "var(--green)" }}>{usd(r.net_usd)}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="chart-box">
          <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Net profit per day">
            {(() => {
              const s = scale([0, ...rows.map((r) => r.net_usd ?? 0)]);
              const slot = (W - PAD_L - PAD_R) / rows.length;
              return (
                <>
                  <Grid {...s} />
                  <line x1={PAD_L} x2={W - PAD_R} y1={s.y(0)} y2={s.y(0)} stroke="var(--border-strong)" />
                  {rows.map((r, i) => {
                    const v = r.net_usd ?? 0;
                    const x = PAD_L + i * slot + slot * 0.15;
                    return (
                      <g key={r.day}>
                        <rect x={x} width={slot * 0.7} y={Math.min(s.y(v), s.y(0))} height={Math.max(1, Math.abs(s.y(v) - s.y(0)))}
                          fill={v < 0 ? "var(--chart-neg)" : "var(--chart-pos)"}><title>{`${r.day}: ${usd(v)} (${r.closes} closes)`}</title></rect>
                        <text x={x + slot * 0.35} y={H - 8} textAnchor="middle" fontSize="11" fill="var(--text-3)">{(r.day ?? "").slice(5)}</text>
                      </g>
                    );
                  })}
                </>
              );
            })()}
          </svg>
        </div>
      )}
    </section>
  );
}
