"use client";

// Scout (dashboard.md §2.1, Main.dc.html): the last hour at a glance, the day's setups grouped by candle with their order
// pictures, the selected setup's detail and story, and the orders of the day. View only: Keep / Skip arrive in step 6.
import { useEffect, useMemo, useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock, NotYet } from "@/components/DataBlock";
import { StatePill, Chip, type Tone } from "@/components/Chips";
import { Lock } from "@/components/Icons";
import { usePrefs } from "@/lib/prefs";
import { useAccount, useStatus } from "@/lib/status";
import { useData, type Data } from "@/lib/useData";
import { dayLabel, hhmm, parseUtc, shortStamp, zoneLabel, type TimeZoneChoice } from "@/lib/time";
import type { ScoutDay, ScoutOrder, ScoutSetup, TimelineItem } from "@/api/types";

const HOUR = 3600_000;

export function ScoutScreen() {
  return (
    <AppShell>
      <ScoutBody />
    </AppShell>
  );
}

export const chartUrl = (file: string) => `/api/scout/chart?file=${encodeURIComponent(file)}`;

function ScoutBody() {
  const { account } = useAccount();
  const status = useStatus();
  const { zone } = usePrefs();
  const [day, setDay] = useState<string | null>(null); // null = the latest day with Scout files
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => {
    setDay(null);
    setSelected(null);
  }, [account?.id]);

  const path = account ? `/api/scout?account=${encodeURIComponent(account.id)}${day ? `&day=${day}` : ""}` : null;
  const data = useData<ScoutDay>(path, { isEmpty: (d) => d.setups.length === 0 && d.orders.length === 0 });
  const d = data.data;
  const sel = d?.setups.find((s) => s.key === selected) ?? d?.setups[0] ?? null;
  const now = new Date();

  if (status.status !== "loading" && !account) {
    return (
      <>
        <PageHead title="Scout" />
        <NotYet title="Setups">
          <strong>No data uploaded yet</strong>
          <span className="upd">The laptop’s upload brings Scout’s setups and orders here every 30 minutes.</span>
        </NotYet>
      </>
    );
  }

  return (
    <>
      <PageHead title="Scout" note={<>{d ? dayLabel(parseUtc(`${d.day} 00:00:00`), "utc") : "…"} · {account?.name ?? "…"}</>}>
        <span className="pill outline" title="Keep / Skip switches arrive with the control sync (build step 6)"><Lock size={12} />View only for now</span>
      </PageHead>
      <LastHour d={d} now={now} zone={zone} scoutState={status.data?.available ? status.data.units?.find((u) => u.name === "Scout")?.state : undefined} />
      <div className="scout-cols">
        <div className="scout-main">
          <DataBlock<ScoutDay> title="Setups" data={data}
            empty={<><strong>No setups on this day</strong><span className="upd">New setups appear here after each candle closes and the next upload (:02 and :32).</span></>}>
            {(x) => <Groups setups={x.setups} now={now} zone={zone} selected={sel?.key ?? null} onSelect={setSelected} />}
          </DataBlock>
          <Orders data={data} day={day} setDay={setDay} zone={zone} onSelect={(key) => d?.setups.some((s) => s.key === key) && setSelected(key)} />
        </div>
        {sel && <Detail s={sel} zone={zone} />}
      </div>
    </>
  );
}

// --- Last hour ------------------------------------------------------------------------------

function LastHour({ d, now, zone, scoutState }: { d: ScoutDay | null; now: Date; zone: TimeZoneChoice; scoutState?: "running" | "stopped" | "failed" | "waiting" }) {
  const recent = (t: string | null | undefined) => (parseUtc(t)?.getTime() ?? 0) > now.getTime() - HOUR;
  const found = d?.setups.filter((s) => s.timeline.some((i) => i.kind === "found" && recent(i.time_utc))).length;
  const placed = d?.orders.filter((o) => recent(o.placed_utc)).length;
  const candle = new Date(Math.floor(now.getTime() / HOUR) * HOUR);
  const other: TimeZoneChoice = zone === "utc" ? "local" : "utc";
  return (
    <section aria-label="Last hour" className="card strip">
      <div><span className="upd">New setups · last hour</span><strong className="num">{found ?? "—"}</strong></div>
      <div><span className="upd">Orders placed · last hour</span><strong className="num">{placed ?? "—"}</strong></div>
      <div className="wide">
        <span className="upd">Last 1H candle closed</span>
        <span style={{ fontSize: 16, fontWeight: 600 }}><span className="num">{hhmm(candle, zone)}</span> {zoneLabel(zone)} <span className="num upd">{hhmm(candle, other)} {zoneLabel(other)}</span></span>
      </div>
      <div><span className="upd">Scout</span>{scoutState ? <StatePill state={scoutState} /> : <span className="upd">—</span>}</div>
    </section>
  );
}

// --- Setups ---------------------------------------------------------------------------------

function Groups({ setups, now, zone, selected, onSelect }: { setups: ScoutSetup[]; now: Date; zone: TimeZoneChoice; selected: string | null; onSelect: (k: string) => void }) {
  const groups = useMemo(() => {
    const m = new Map<string, ScoutSetup[]>();
    for (const s of setups) m.set(s.candle_close_utc, [...(m.get(s.candle_close_utc) ?? []), s]);
    return [...m.entries()].sort((a, b) => b[0].localeCompare(a[0]));
  }, [setups]);
  const other: TimeZoneChoice = zone === "utc" ? "local" : "utc";
  return (
    <div className="groups">
      {groups.map(([close, list]) => {
        const at = parseUtc(close);
        const daily = list.every((s) => s.timeframe === "D1");
        const last = (at?.getTime() ?? 0) > now.getTime() - HOUR;
        const label = daily ? `Daily candle ${hhmm(at, zone)}` : `${hhmm(at, zone)} ${zoneLabel(zone)}`;
        return (
          <section key={close} aria-label={`Candle closed ${label}`} className="grp">
            <div className="grp-head">
              <h3 className="num">{label}</h3>
              <span className="num upd">{hhmm(at, other)} {zoneLabel(other)}</span>
              {last && <span className="chip t-accent last-hour">Last hour</span>}
              <span className="rule" aria-hidden="true" />
            </div>
            <div className={`grp-cards${last ? " hot" : ""}`}>
              {list.map((s) => <SetupCard key={s.key} s={s} zone={zone} selected={s.key === selected} onSelect={() => onSelect(s.key)} />)}
            </div>
          </section>
        );
      })}
    </div>
  );
}

const EVENT: Record<string, string> = {
  NewStop: "New", NewReversal: "New reversal", StopMoved: "Moved", ReversalMoved: "Moved",
  StopFilled: "Filled", ReversalFilled: "Filled", Cancelled: "Ended", TrendTurned: "Trend turned",
};

const MARKET: [RegExp, string][] = [
  [/^(XAU|XAG|XPT|XPD)/, "Metals"], [/^(XTI|XBR|XNG|WTI|BRENT|NAT)/, "Energies"], [/^(BTC|ETH|SOL|XRP|LTC|ADA|DOGE)/, "Crypto"],
  [/^[A-Z]{6}$/, "Forex"], [/./, "Indices"],
];
export const market = (symbol: string) => MARKET.find(([r]) => r.test(symbol))![1];
export const tfLabel = (tf: string) => ({ H1: "1H", H4: "4H", D1: "1D", W1: "1W", M30: "30m", M15: "15m" })[tf] ?? tf;

/** |level − stop-loss| with the level's own digits. */
export function distance(level?: string | null, sl?: string | null): string {
  if (!level || !sl) return "—";
  const digits = level.split(".")[1]?.length ?? 0;
  return Math.abs(parseFloat(level) - parseFloat(sl)).toFixed(digits);
}

export const usd = (v: number | null | undefined) => (v == null ? "" : `${v < 0 ? "−" : "+"}$${Math.abs(v).toFixed(2)}`);

/** Where the setup's order stands now, in words and colour (never colour alone). */
export function orderState(o: ScoutOrder | null | undefined, zone: TimeZoneChoice): { tone: Tone; text: string } {
  if (!o) return { tone: "plain", text: "No order" };
  const t = hhmm(parseUtc(o.state_utc), zone);
  switch (o.state) {
    case "pending": return { tone: "blue", text: `Pending${o.expires_utc ? ` · expires ${shortStamp(parseUtc(o.expires_utc), zone)}` : ""}` };
    case "filled": return { tone: "accent", text: `Filled ${t} · managed by TM` };
    case "closed": return { tone: (o.net_usd ?? 0) < 0 ? "red" : "green", text: `Closed ${usd(o.net_usd)}` };
    case "cancelled": return { tone: "grey", text: `Cancelled ${t}${o.reason ? ` · ${o.reason}` : ""}` };
    case "expired": return { tone: "grey", text: `Expired ${t}` };
    default: return { tone: "amber", text: `${o.state === "rejected" ? "Rejected" : "Not placed"}${o.reason ? ` · ${o.reason}` : ""}` };
  }
}

function Side({ side }: { side?: string | null }) {
  if (!side) return null;
  return <span className="side">{side === "Buy" ? "↑ Buy stop" : "↓ Sell stop"}</span>;
}

function Trend({ s }: { s: ScoutSetup }) {
  if (s.with_higher == null || !s.higher_tf) return null;
  const arrow = s.higher_trend === "up" ? "↑" : s.higher_trend === "down" ? "↓" : "";
  return s.with_higher
    ? <span className="chip t-plain">With {tfLabel(s.higher_tf)} {arrow}</span>
    : <span className="chip t-amber">▲ Against {tfLabel(s.higher_tf)} {arrow}</span>;
}

function Picture({ file, symbol, big }: { file?: string | null; symbol: string; big?: boolean }) {
  if (!file) return <div className={`pic none${big ? " big" : ""}`}><span className="upd">No picture</span></div>;
  // eslint-disable-next-line @next/next/no-img-element -- pictures come from the API after sign-in, not from the build
  return <img className={`pic${big ? " big" : ""}`} src={chartUrl(file)} alt={`Order picture for ${symbol}`} loading="lazy" width={720} height={400} />;
}

function SetupCard({ s, zone, selected, onSelect }: { s: ScoutSetup; zone: TimeZoneChoice; selected: boolean; onSelect: () => void }) {
  const st = orderState(s.order, zone);
  const gone = s.order?.state === "cancelled" || s.order?.state === "expired" || (!s.order && s.event === "Cancelled");
  return (
    <article className={`card setup${selected ? " is-sel" : ""}${gone ? " is-gone" : ""}`} aria-label={`${s.symbol} setup`}>
      <button type="button" className="thumbbtn" onClick={onSelect} aria-label={`Open ${s.symbol} order picture and details`}>
        <Picture file={s.order?.chart} symbol={s.symbol} />
      </button>
      <div className="setup-body">
        <div className="row">
          <span className="sym">{s.symbol}</span>
          <span className="mkt">{market(s.symbol)}</span>
          <span className={`tf num${s.timeframe === "D1" ? " daily" : ""}`}>{tfLabel(s.timeframe)}</span>
          <span className="upd">{EVENT[s.event] ?? s.event} <span className="num">{hhmm(parseUtc(s.event_utc), zone)}</span></span>
        </div>
        <div className="row">
          <Side side={s.side} />
          {s.role && <span className="upd">{s.role === "R" ? "Reversal" : "Main"}</span>}
          <Trend s={s} />
        </div>
        <dl className="figs">
          <div><dt>Level</dt><dd className="num">{s.level ?? "—"}</dd></div>
          <div><dt>Stop-loss</dt><dd className="num">{s.stop_loss ?? "—"}</dd></div>
          <div><dt>Distance</dt><dd className="num">{distance(s.level, s.stop_loss)}</dd></div>
          <div><dt>Size</dt><dd className="num">{s.lots != null ? `${s.lots.toFixed(2)} lot` : "—"}</dd></div>
        </dl>
      </div>
      <div className="setup-state">
        <Chip tone={st.tone}>{st.text}</Chip>
        {s.reason && !s.order && <span className="upd">{s.reason}</span>}
      </div>
    </article>
  );
}

// --- Detail ---------------------------------------------------------------------------------

const DOT: Record<TimelineItem["kind"], string> = {
  found: "var(--border-strong)", placed: "var(--accent)", moved: "var(--accent)", filled: "var(--accent)",
  closed: "var(--green)", cancelled: "var(--grey-solid)", info: "var(--border-strong)",
};

function Detail({ s, zone }: { s: ScoutSetup; zone: TimeZoneChoice }) {
  const o = s.order;
  const at = (v?: string | null) => (v ? `${shortStamp(parseUtc(v), zone)} ${zoneLabel(zone)}` : "—");
  const fields: [string, string][] = [
    ["Placed", at(o?.placed_utc)],
    ["Expiry", at(o?.expires_utc)],
    ["Level", s.level ?? "—"],
    ["Stop-loss", s.stop_loss ?? "—"],
    ["Distance", distance(s.level, s.stop_loss)],
    ["Size", s.lots != null ? `${s.lots.toFixed(2)} lot` : "—"],
    ["Order ID", o?.order_id ?? "—"],
    [`Higher TF (${s.higher_tf ? tfLabel(s.higher_tf) : "—"})`, s.with_higher == null ? "—" : `${s.with_higher ? "With" : "Against"} ${s.higher_trend === "up" ? "↑" : s.higher_trend === "down" ? "↓" : ""}`],
    ["Pause", s.pause_candles != null ? `${s.pause_candles} candles · ${s.pause_height_atr ?? "?"} ATR` : "—"],
    ["Move before", s.move_atr != null ? `${s.move_atr} ATR` : "—"],
  ];
  if (o?.price_fill) fields.push(["Filled at", `${o.price_fill}${o.slip_pips != null ? ` · ${o.slip_pips} pips slip` : ""}`]);
  if (o?.position_id) fields.push(["Position", `P${o.position_id}`]);
  return (
    <aside aria-label="Setup detail" className="card detail">
      <div className="ch">
        <span style={{ fontSize: 18, fontWeight: 700 }}>{s.symbol}</span>
        <span className={`tf num${s.timeframe === "D1" ? " daily" : ""}`}>{tfLabel(s.timeframe)}</span>
        <span className="upd">{s.side ? `${s.side} stop` : ""}{s.role ? ` · ${s.role === "R" ? "reversal" : "main"}` : ""}</span>
      </div>
      <div className="detail-body">
        {o?.chart ? (
          <a className="frame" href={chartUrl(o.chart)} target="_blank" rel="noopener" aria-label={`Open ${s.symbol} picture full size`}>
            <Picture file={o.chart} symbol={s.symbol} big />
          </a>
        ) : (
          <div className="frame"><Picture symbol={s.symbol} big /></div>
        )}
        <dl className="detail-figs">
          {fields.map(([k, v]) => <div key={k}><dt>{k}</dt><dd className="num">{v}</dd></div>)}
        </dl>
        <div>
          <h3 className="sub">Timeline</h3>
          <ol className="timeline">
            {s.timeline.map((i, n) => (
              <li key={n}>
                <span className="tdot" aria-hidden="true" style={{ background: DOT[i.kind] }} />
                <span>{i.text}</span>
                <span className="num upd">{shortStamp(parseUtc(i.time_utc), zone)}</span>
              </li>
            ))}
          </ol>
        </div>
        <p className="upd" style={{ margin: 0 }}>TM’s actions on this trade come with the Trades page (build step 5).</p>
      </div>
    </aside>
  );
}

// --- Orders of the day ----------------------------------------------------------------------

function Orders({ data, day, setDay, zone, onSelect }: {
  data: Data<ScoutDay>; day: string | null; setDay: (d: string | null) => void; zone: TimeZoneChoice; onSelect: (key: string) => void;
}) {
  const [filter, setFilter] = useState("");
  const d = data.data;
  const days = d?.days ?? [];
  const i = d ? days.indexOf(d.day) : -1;
  const today = new Date().toISOString().slice(0, 10);
  const go = (n: number) => d && days[i + n] && setDay(i + n === days.length - 1 ? null : days[i + n]);
  const shown = (d?.orders ?? []).filter((o) => o.symbol.includes(filter.trim().toUpperCase()));
  const picker = (
    <div className="daypick">
      <button type="button" aria-label="Previous day" disabled={!d || i <= 0} onClick={() => go(-1)}>‹</button>
      <span>{d ? `${d.day === today ? "Today · " : ""}${dayLabel(parseUtc(`${d.day} 00:00:00`), "utc")}` : "…"}</span>
      <button type="button" aria-label="Next day" disabled={!d || i < 0 || i >= days.length - 1} onClick={() => go(1)}>›</button>
    </div>
  );
  return (
    <section className="card" aria-labelledby="orders-h">
      <div className="ch">
        <h2 id="orders-h">Orders of the day</h2>
        {picker}
        <label className="filter">
          <span className="sr-only">Filter by symbol</span>
          <input placeholder="Filter symbol" value={filter} onChange={(e) => setFilter(e.target.value)} />
        </label>
        <span style={{ flex: 1 }} />
        {d && (
          <span className="num totals">
            <span>{d.totals.orders} orders</span><span>{d.totals.filled} filled</span><span>{d.totals.closed} closed</span>
            <strong style={{ color: d.totals.net_usd < 0 ? "var(--red)" : "var(--green)" }}>Net {usd(d.totals.net_usd)}</strong>
          </span>
        )}
        {day !== null && <button type="button" className="btn" onClick={() => setDay(null)}>Latest day</button>}
      </div>
      {data.status === "loading" && <div className="blkb"><div className="skeleton" style={{ height: 18 }} /><div className="skeleton" style={{ height: 18, width: "80%" }} /></div>}
      {data.status === "error" && <p className="blkb empty" role="alert" style={{ margin: 0 }}>Couldn’t load orders. <button type="button" className="btn" onClick={data.refresh}>Try again</button></p>}
      {d && shown.length === 0 && <p className="blkb empty" style={{ margin: 0 }}>{filter ? `No ${filter.toUpperCase()} orders on this day.` : "No orders on this day."}</p>}
      {d && shown.length > 0 && (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Placed {zoneLabel(zone)}</th><th scope="col">Symbol</th><th scope="col">Side</th><th scope="col">M/R</th>
                <th scope="col" className="r">Level</th><th scope="col" className="r">Stop-loss</th><th scope="col" className="r">Distance</th>
                <th scope="col" className="r">Lots</th><th scope="col">Status</th><th scope="col" className="r">Net</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((o) => {
                const st = orderState(o, zone);
                return (
                  <tr key={o.order_id} onClick={() => onSelect(o.setup_key)} className="clickable">
                    <td className="num">{hhmm(parseUtc(o.placed_utc), zone)}</td>
                    <td style={{ fontWeight: 600 }}>{o.symbol} <span className="upd">{tfLabel(o.timeframe)}</span></td>
                    <td><Side side={o.side} /></td>
                    <td className="upd">{o.role}</td>
                    <td className="num r">{o.level ?? "—"}</td>
                    <td className="num r">{o.stop_loss ?? "—"}</td>
                    <td className="num r">{distance(o.level, o.stop_loss)}</td>
                    <td className="num r">{o.lots?.toFixed(2) ?? "—"}</td>
                    <td><Chip tone={st.tone}>{st.text}</Chip></td>
                    <td className="num r" style={{ fontWeight: 600, color: o.net_usd == null ? undefined : o.net_usd < 0 ? "var(--red)" : "var(--green)" }}>{usd(o.net_usd)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
