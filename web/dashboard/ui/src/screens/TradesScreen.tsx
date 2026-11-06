"use client";

// TM trades (dashboard.md §2.3, Trades.dc.html): what is open now, and one block per trade with activity on the day — its whole
// result over every leg, its picture (candles, entry, stop-loss and hedge level over time, every fill) and its story: the broker's
// fills merged with TM's actions, refusals in red and counted. From the laptop's snapshot, deals.csv and TM's actions.csv.
import { useEffect, useMemo, useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock, NotYet } from "@/components/DataBlock";
import { Chip } from "@/components/Chips";
import { usePrefs } from "@/lib/prefs";
import { useAccount, useStatus } from "@/lib/status";
import { useData, type Data } from "@/lib/useData";
import { dayLabel, hhmm, parseUtc, shortStamp, zoneLabel, type TimeZoneChoice } from "@/lib/time";
import type { OpenPosition, Trade, TradeEvent, TradesDay } from "@/api/types";
import { chartUrl, usd } from "./ScoutScreen";

export function TradesScreen() {
  return (
    <AppShell>
      <TradesBody />
    </AppShell>
  );
}

export const tradeChartUrl = (file: string) => `/api/trades/chart?file=${encodeURIComponent(file)}`;

const resultColor = (v: number | null | undefined) => (v == null ? undefined : v < 0 ? "var(--red)" : "var(--green)");

function TradesBody() {
  const { account } = useAccount();
  const status = useStatus();
  const { zone } = usePrefs();
  const [day, setDay] = useState<string | null>(null);
  const [symbol, setSymbol] = useState("");
  const [owners, setOwners] = useState<string[] | null>(null); // null = the default (shown_owners)
  useEffect(() => {
    setDay(null);
  }, [account?.id]);

  const path = account ? `/api/trades?account=${encodeURIComponent(account.id)}${day ? `&day=${day}` : ""}` : null;
  const data = useData<TradesDay>(path, { isEmpty: (d) => d.trades.length === 0 && d.open_positions.length === 0 });
  const d = data.data;
  const allOwners = useMemo(() => [...new Set([...(d?.shown_owners ?? []), ...(d?.trades ?? []).map((t) => t.owner)])], [d]);
  const chosen = owners ?? d?.shown_owners ?? [];
  const sym = symbol.trim().toUpperCase();
  const trades = (d?.trades ?? []).filter((t) => chosen.includes(t.owner) && t.symbol.includes(sym));
  const hidden = (d?.trades ?? []).filter((t) => !chosen.includes(t.owner)).length;

  if (status.status !== "loading" && !account) {
    return (
      <>
        <PageHead title="Trades" />
        <NotYet title="Trades"><strong>No data uploaded yet</strong></NotYet>
      </>
    );
  }

  const days = d?.days ?? [];
  const i = d ? days.indexOf(d.day) : -1;
  const go = (n: number) => d && days[i + n] && setDay(i + n === days.length - 1 ? null : days[i + n]);
  const today = new Date().toISOString().slice(0, 10);

  return (
    <>
      <PageHead title="Trades" note={<>{account?.name ?? "…"}{d?.snapshot_utc ? <> · snapshot {shortStamp(parseUtc(d.snapshot_utc), zone)} {zoneLabel(zone)}</> : null}</>} />
      <OpenNow data={data} zone={zone} />
      <section className="card" aria-labelledby="trades-h">
        <div className="ch">
          <h2 id="trades-h">Trades of the day</h2>
          <div className="daypick">
            <button type="button" aria-label="Previous day" disabled={!d || i <= 0} onClick={() => go(-1)}>‹</button>
            <span>{d ? `${d.day === today ? "Today · " : ""}${dayLabel(parseUtc(`${d.day} 00:00:00`), "utc")}` : "…"}</span>
            <button type="button" aria-label="Next day" disabled={!d || i < 0 || i >= days.length - 1} onClick={() => go(1)}>›</button>
          </div>
          <label className="filter">
            <span className="sr-only">Filter by symbol</span>
            <input placeholder="Filter symbol" value={symbol} onChange={(e) => setSymbol(e.target.value)} />
          </label>
          <div className="toolbar" role="group" aria-label="Owners shown">
            {allOwners.map((o) => {
              const on = chosen.includes(o);
              return (
                <button key={o} type="button" className={`fchip${on ? " on" : ""}`} aria-pressed={on}
                  onClick={() => setOwners(on ? chosen.filter((x) => x !== o) : [...chosen, o])}>{o}</button>
              );
            })}
          </div>
          <span style={{ flex: 1 }} />
          {d && (
            <span className="num totals">
              <span>{trades.length} trades</span>
              {hidden > 0 && <span>{hidden} hidden (other owners)</span>}
              <strong style={{ color: resultColor(trades.reduce((s, t) => s + t.result_usd, 0)) }}>
                Net {usd(trades.reduce((s, t) => s + t.result_usd, 0))}
              </strong>
            </span>
          )}
          {day !== null && <button type="button" className="btn" onClick={() => setDay(null)}>Latest day</button>}
        </div>
        {data.status === "loading" && <div className="blkb"><div className="skeleton" style={{ height: 18 }} /><div className="skeleton" style={{ height: 18, width: "80%" }} /></div>}
        {data.status === "error" && <p className="blkb empty" role="alert" style={{ margin: 0 }}>Couldn’t load trades. <button type="button" className="btn" onClick={data.refresh}>Try again</button></p>}
        {d && trades.length === 0 && <p className="blkb empty" style={{ margin: 0 }}>No trades {sym ? `on ${sym} ` : ""}with a fill or a TM action on this day.</p>}
      </section>
      {trades.map((t) => <TradeBlock key={t.trade} t={t} zone={zone} />)}
    </>
  );
}

function OpenNow({ data, zone }: { data: Data<TradesDay>; zone: TimeZoneChoice }) {
  return (
    <DataBlock<TradesDay> title="Open now" data={data}
      empty={<><strong>Nothing open and no trades yet</strong><span className="upd">The laptop’s snapshot (:02 and :32) brings positions here.</span></>}>
      {(d) => {
        const byTrade = new Map<string, OpenPosition[]>();
        for (const p of d.open_positions) byTrade.set(p.trade, [...(byTrade.get(p.trade) ?? []), p]);
        if (byTrade.size === 0) return <p className="blkb empty" style={{ margin: 0 }}>No open positions.</p>;
        return (
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Trade</th><th scope="col">Leg</th><th scope="col">Owner</th><th scope="col">Symbol</th><th scope="col">Side</th>
                  <th scope="col" className="r">Lots</th><th scope="col" className="r">Entry</th><th scope="col" className="r">Stop-loss</th>
                  <th scope="col" className="r">Open result</th><th scope="col" className="r">Margin</th><th scope="col">Opened {zoneLabel(zone)}</th>
                </tr>
              </thead>
              <tbody>
                {[...byTrade.entries()].flatMap(([trade, legs]) => legs.map((p, n) => (
                  <tr key={p.position_id}>
                    <td className="num upd">{n === 0 ? trade : ""}</td>
                    <td><span className="role">{p.role}</span></td>
                    <td>{p.owner}</td>
                    <td style={{ fontWeight: 600 }}>{p.symbol}</td>
                    <td className="side">{p.side === "buy" ? "↑ Buy" : "↓ Sell"}</td>
                    <td className="num r">{p.lots}</td>
                    <td className="num r">{p.entry}</td>
                    <td className="num r">{p.stop_loss ?? <span className="upd">none</span>}</td>
                    <td className="num r" style={{ color: resultColor(p.open_result_usd) }}>{usd(p.open_result_usd)}</td>
                    <td className="num r">${p.margin_usd.toFixed(2)}</td>
                    <td className="num">{shortStamp(parseUtc(p.opened_utc), zone)}</td>
                  </tr>
                )))}
              </tbody>
            </table>
            {d.open_orders.length > 0 && <p className="upd" style={{ padding: "8px 16px", margin: 0 }}>{d.open_orders.length} pending orders (entries, mirrors and hedge orders waiting at their level).</p>}
          </div>
        );
      }}
    </DataBlock>
  );
}

function TradeBlock({ t, zone }: { t: Trade; zone: TimeZoneChoice }) {
  return (
    <article className="card trade" aria-label={`${t.symbol} trade ${t.trade}`}>
      <div className="trade-head">
        <span className="sym">{t.symbol}</span>
        <span className="num upd">{t.trade}</span>
        <Chip tone="plain">{t.owner}</Chip>
        {t.open ? <Chip tone="blue">Open</Chip> : <Chip tone="grey">Closed {t.last_utc ? shortStamp(parseUtc(t.last_utc), zone) : ""}</Chip>}
        <span className="upd">{t.legs} {t.legs === 1 ? "position" : "positions"}</span>
        {t.refused > 0 && <Chip tone="red">{t.refused.toLocaleString("en-US")} refused</Chip>}
        {t.scout_order?.chart && <a className="upd" href={chartUrl(t.scout_order.chart)} target="_blank" rel="noopener">Scout order {t.scout_order.order_id} picture</a>}
        <span className="res num" style={{ color: resultColor(t.result_usd) }} title="Every leg: closed net plus the open legs' result">
          {usd(t.result_usd)}{t.open && <span className="upd"> so far</span>}
        </span>
      </div>
      <div className="trade-body">
        {t.chart ? (
          <a className="frame" href={tradeChartUrl(t.chart)} target="_blank" rel="noopener" aria-label={`Open the ${t.symbol} trade picture full size`}>
            {/* eslint-disable-next-line @next/next/no-img-element -- pictures come from the API after sign-in */}
            <img className="tpic" src={tradeChartUrl(t.chart)} alt={`${t.symbol} trade ${t.trade}: candles, levels and fills`} loading="lazy" width={900} height={440} />
          </a>
        ) : (
          <div className="frame"><div className="tpic" style={{ display: "grid", placeItems: "center" }}><span className="upd">No picture{t.owner === "AuditBot" ? " for other bots’ trades" : " yet"}</span></div></div>
        )}
        <div className="trade-events">
          <table className="tbl">
            <thead><tr><th scope="col">{zoneLabel(zone)}</th><th scope="col">Leg</th><th scope="col">What</th><th scope="col" className="r">Result</th></tr></thead>
            <tbody>
              {t.timeline.map((e, n) => <EventRow key={n} e={e} zone={zone} />)}
            </tbody>
          </table>
        </div>
      </div>
    </article>
  );
}

function EventRow({ e, zone }: { e: TradeEvent; zone: TimeZoneChoice }) {
  const until = e.until_utc ? ` – ${hhmm(parseUtc(e.until_utc), zone)}` : "";
  return (
    <tr className={e.ok ? (e.source === "fill" ? "fill" : "") : "bad"}>
      <td className="num">{shortStamp(parseUtc(e.time_utc), zone)}{until}</td>
      <td><span className="role">{e.role}</span></td>
      <td>
        {e.source === "fill" ? <strong>{e.what}</strong> : e.what}
        {e.price ? <span className="num"> at {e.price}</span> : null}
        {e.lots != null && e.source === "fill" ? <span className="num upd"> · {e.lots} lot</span> : null}
        {!e.ok && <span> · refused {e.result}</span>}
        {e.count > 1 && <span className="num"> · ×{e.count.toLocaleString("en-US")}</span>}
      </td>
      <td className="num r" style={{ color: resultColor(e.net_usd) }}>{e.net_usd != null ? usd(e.net_usd) : ""}</td>
    </tr>
  );
}
