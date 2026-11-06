"use client";

// Symbols and settings (dashboard.md §2.2): each of Scout's symbols on or off and its timeframe, from the laptop's catalogue
// (§3.4). A change is saved here, fetched by the laptop within a minute, and applied — or refused — by Scout itself.
import { useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock, NotYet } from "@/components/DataBlock";
import { ConfirmSheet, Switch } from "@/components/Controls";
import { Lock } from "@/components/Icons";
import { usePrefs } from "@/lib/prefs";
import { useAccount, useStatus } from "@/lib/status";
import { ControlStateChip, useControls, type ControlChange } from "@/lib/controls";
import type { ControlState, ControlsView } from "@/api/types";
import { market, tfLabel } from "./ScoutScreen";

export function SymbolsScreen() {
  return (
    <AppShell>
      <SymbolsBody />
    </AppShell>
  );
}

type Pending = { change: ControlChange; title: string; text: string };

function SymbolsBody() {
  const { account } = useAccount();
  const status = useStatus();
  const { zone } = usePrefs();
  const c = useControls(account?.id ?? null);
  const [pending, setPending] = useState<Pending | null>(null);

  if (status.status !== "loading" && !account) {
    return (
      <>
        <PageHead title="Symbols" />
        <NotYet title="Symbols"><strong>No data uploaded yet</strong></NotYet>
      </>
    );
  }
  const d = c.data.data;
  const enabled = c.option("symbol", "enabled");
  const timeframe = c.option("symbol", "timeframe");
  const ask = (change: ControlChange, title: string, confirm?: string | null) =>
    confirm ? setPending({ change, title, text: confirm }) : void c.change(change);

  return (
    <>
      <PageHead title="Symbols" note={<>{account?.name ?? "…"} · Scout’s symbols, as configured on the laptop and as you set them here</>}>
        {d && !d.can_change && <span className="pill outline"><Lock size={12} />View only</span>}
      </PageHead>
      {c.error && <p className="note" role="alert" style={{ color: "var(--red)", margin: 0 }}>Not saved: {c.error}</p>}
      <DataBlock<ControlsView> title="Symbols" data={c.data}
        empty={<><strong>No controls yet</strong><span className="upd">The laptop uploads Scout’s options with its data (:02 and :32).</span></>}>
        {(x) => !x.available ? <p className="blkb empty" style={{ margin: 0 }}>Scout has no symbols on this account, or the laptop has not uploaded its options yet.</p> : (
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Symbol</th><th scope="col">As configured</th>
                  <th scope="col">{enabled?.label ?? "Trade this symbol"}</th><th scope="col">{timeframe?.label ?? "Timeframe"}</th>
                  <th scope="col">State</th><th scope="col" />
                </tr>
              </thead>
              <tbody>
                {x.symbols.map((s) => {
                  const set = (s.set ?? {}) as Record<string, unknown>;
                  const on = set.enabled !== false;
                  const tf = (set.timeframe as string | undefined) ?? s.configured?.timeframe ?? "H1";
                  const changed = Object.keys(set).length > 0;
                  const busy = c.busy?.startsWith(`symbol|${s.symbol}|`);
                  const tfKnown = c.knows("symbol", "timeframe");
                  const tfOk = (s.charts ?? 1) === 1;
                  return (
                    <tr key={s.symbol}>
                      <td><span style={{ fontWeight: 600 }}>{s.symbol}</span> <span className="upd">{market(s.symbol ?? "")}</span></td>
                      <td className="upd">{tfLabel(s.configured?.timeframe ?? "")} · {s.configured?.mode === "trade" ? "trades" : "watches"}</td>
                      <td>
                        {enabled && (
                          <Switch on={on} label={`Trade ${s.symbol}`} disabled={!x.can_change || busy || !c.knows("symbol", "enabled")}
                            onChange={(v) => ask({ scope: "symbol", target: s.symbol!, key: "enabled", value: v }, v ? `Trade ${s.symbol} again?` : `Switch ${s.symbol} off?`, v ? null : enabled.confirm)} />
                        )}
                        {!c.knows("symbol", "enabled") && <span className="upd"> needs a newer Scout</span>}
                      </td>
                      <td>
                        {timeframe && (
                          <select value={tf} disabled={!x.can_change || busy || !tfKnown || !tfOk} aria-label={`${s.symbol} timeframe`}
                            onChange={(e) => ask({ scope: "symbol", target: s.symbol!, key: "timeframe", value: e.target.value }, `${s.symbol} on ${tfLabel(e.target.value)}?`, timeframe.confirm)}
                            style={{ font: "inherit", fontSize: 13, height: 34, borderRadius: 8, border: "1px solid var(--border-strong)", background: "var(--surface)", color: "var(--text)", padding: "0 8px" }}>
                            {(timeframe.values ?? []).map((v) => <option key={v} value={v}>{tfLabel(v)}{v === s.configured?.timeframe ? " (configured)" : ""}</option>)}
                          </select>
                        )}
                        {!tfOk && <span className="upd"> several charts: set in scout.json</span>}
                        {!tfKnown && <span className="upd"> needs a newer Scout</span>}
                      </td>
                      <td>
                        <div style={{ display: "flex", flexDirection: "column", gap: 4, alignItems: "flex-start" }}>
                          {Object.entries((s.state ?? {}) as Record<string, ControlState>).map(([k, st]) => <ControlStateChip key={k} state={st} zone={zone} />)}
                          {!changed && <span className="upd">As configured</span>}
                        </div>
                      </td>
                      <td>
                        {changed && x.can_change && (
                          <button type="button" className="btn" disabled={busy} onClick={async () => {
                            for (const k of Object.keys(set)) await c.change({ scope: "symbol", target: s.symbol!, key: k, value: null });
                          }}>Reset to configured</button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <p className="upd" style={{ padding: "8px 16px", margin: 0 }}>
              A change is saved here at once; the laptop fetches it within a minute and Scout applies it, or refuses it with a reason.
              Adding a symbol stays a change to scout.json on the laptop. {x.ack?.error ? `Scout could not read the last file: ${x.ack.error}` : ""}
            </p>
          </div>
        )}
      </DataBlock>
      {pending && (
        <ConfirmSheet title={pending.title} action="Save" onCancel={() => setPending(null)} busy={c.busy !== null}
          onConfirm={async () => { await c.change(pending.change); setPending(null); }}>
          {pending.text}
        </ConfirmSheet>
      )}
    </>
  );
}

