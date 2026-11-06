"use client";

// Audit (dashboard.md §2.7, Audit.dc.html): the Audit Bot's comparison of IC Markets and Pepperstone, per symbol side by side,
// the better figure of each pair marked (in words for screen readers, not by colour alone), and each broker's totals.
import { useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock } from "@/components/DataBlock";
import { Chip } from "@/components/Chips";
import { useData } from "@/lib/useData";
import type { AuditFigures, AuditView } from "@/api/types";

export function AuditScreen() {
  return (
    <AppShell>
      <AuditBody />
    </AppShell>
  );
}

const PERIOD: Record<string, string> = { day: "Whole day", main: "Main hours", offpeak: "Off-peak", QuietHour: "Quiet hour", LondonOpen: "London open", NewYorkOpen: "New York open" };
export const periodLabel = (p: string) => PERIOD[p] ?? p.replace(/([a-z])([A-Z])/g, "$1 $2");

type Col = { key: keyof AuditFigures; label: string; spread?: boolean; probe?: boolean; digits?: number };
const COLS: Col[] = [
  { key: "spread_median", label: "Spread median", spread: true },
  { key: "spread_p95", label: "Spread 95 %", spread: true },
  { key: "slip_avg", label: "Slippage avg", probe: true },
  { key: "slip_worst", label: "Slippage worst", probe: true },
  { key: "latency_median_ms", label: "Latency median ms", probe: true, digits: 0 },
  { key: "rejected", label: "Rejected", probe: true, digits: 0 },
];

function AuditBody() {
  const [report, setReport] = useState<string | null>(null);
  const [period, setPeriod] = useState("day");
  const path = `/api/audit?${new URLSearchParams({ ...(report ? { report } : {}), period }).toString()}`;
  const data = useData<AuditView>(path, { isEmpty: (d) => !d.available });
  const d = data.data;
  const names = d?.brokers ?? {};
  const ids = Object.keys(names);
  const spreadPeriod = ["day", "main", "offpeak"].includes(period);
  const cols = COLS.filter((c) => (spreadPeriod ? c.spread || period === "day" : c.probe));

  return (
    <>
      <PageHead title="Audit" note={d?.report ? (d.report.kind === "summary" ? `Final summary ${d.report.from} – ${d.report.to}` : `Day ${d.report.from}`) : "…"}>
        {d?.available && (d.broker_choice
          ? <Chip tone="green">Broker chosen: {d.broker_choice}</Chip>
          : <Chip tone="amber">Broker not chosen yet</Chip>)}
      </PageHead>
      <DataBlock<AuditView> title="IC Markets and Pepperstone, side by side" data={data}
        empty={<><strong>No Audit Bot reports uploaded yet</strong></>}
        actions={d?.available ? (
          <div className="toolbar">
            <label className="filter">
              <span className="sr-only">Report</span>
              <select value={report ?? "summary"} onChange={(e) => setReport(e.target.value === "summary" ? null : e.target.value)} style={{ border: 0, background: "transparent", font: "inherit", fontSize: 13, color: "var(--text)" }}>
                {d.reports.map((r) => <option key={r.id} value={r.id}>{r.kind === "summary" ? `Summary ${r.from} – ${r.to}` : `Day ${r.from}`}</option>)}
              </select>
            </label>
            <div className="seg" role="group" aria-label="Period">
              {d.periods.map((p) => <button key={p} type="button" className={p === period ? "on" : ""} aria-pressed={p === period} onClick={() => setPeriod(p)}>{periodLabel(p)}</button>)}
            </div>
          </div>
        ) : null}>
        {(x) => (
          <div className="tbl-wrap">
            <table className="tbl audit">
              <thead>
                <tr>
                  <th scope="col" rowSpan={2}>Symbol</th>
                  <th scope="col" rowSpan={2}>Unit</th>
                  {cols.map((c) => <th key={c.key} scope="colgroup" colSpan={2} className="r pair">{c.label}</th>)}
                </tr>
                <tr>{cols.flatMap((c) => ids.map((id) => <th key={`${c.key}-${id}`} scope="col" className="r">{names[id]}</th>))}</tr>
              </thead>
              <tbody>
                {x.symbols.map((s) => (
                  <tr key={s.symbol}>
                    <td style={{ fontWeight: 600 }}>{s.symbol}</td>
                    <td className="upd">{s.unit === "pips" ? "pips" : "USD / lot"}</td>
                    {cols.flatMap((c) => ids.map((id, n) => {
                      const v = s.brokers?.[id]?.[c.key] as number | null | undefined;
                      const best = s.better?.[c.key] === id;
                      return (
                        <td key={`${c.key}-${id}`} className={`num r${n === 0 ? " pair" : ""}${best ? " better" : ""}`}>
                          {v == null ? "—" : v.toFixed(c.digits ?? 2)}
                          {best && <span className="sr-only"> (better)</span>}
                        </td>
                      );
                    }))}
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="upd" style={{ padding: "8px 16px", margin: 0 }}>
              Highlighted: the better of the two. Slippage is negative when it went against us. Forex and XAGUSD are compared in pips; the rest in USD per lot, because the brokers count pips differently there.
            </p>
          </div>
        )}
      </DataBlock>
      {d?.available && <Totals d={d} />}
    </>
  );
}

const TOTALS: [string, string][] = [
  ["orders", "Probe orders"], ["filled", "Filled"], ["rejected", "Rejected"], ["order_errors", "Order errors"],
  ["latency_avg_ms", "Latency avg ms"], ["latency_median_ms", "Latency median ms"], ["latency_max_ms", "Latency max ms"], ["commission_usd", "Commission USD"],
  ["instances", "Bot runs"], ["connection_drops", "Connection drops (shared)"], ["spread_alerts", "Spread alerts"], ["slippage_alerts", "Slippage alerts"],
  ["missed_windows", "Missed probe windows"], ["log_warnings", "Log warnings"], ["log_errors", "Log errors"],
];

function Totals({ d }: { d: AuditView }) {
  const ids = Object.keys(d.brokers);
  return (
    <section className="card" aria-labelledby="totals-h">
      <div className="ch"><h2 id="totals-h">Totals and reliability</h2><span className="upd">All symbols together</span></div>
      <div className="tbl-wrap">
        <table className="tbl">
          <thead><tr><th scope="col">Figure</th>{ids.map((id) => <th key={id} scope="col" className="r">{d.brokers[id]}</th>)}</tr></thead>
          <tbody>
            {TOTALS.map(([k, label]) => (
              <tr key={k}>
                <td>{label}</td>
                {ids.map((id) => {
                  const v = d.totals?.[id]?.[k];
                  return <td key={id} className="num r">{v == null ? "—" : k === "commission_usd" ? v.toFixed(2) : v.toLocaleString("en-US")}</td>;
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
