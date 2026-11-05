"use client";

// Status (dashboard.md §2.6, Status.dc.html): bots and timers, today's warnings and errors, uploads and sync, control history.
import { useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock } from "@/components/DataBlock";
import { StatePill } from "@/components/Chips";
import { FilterChips } from "@/components/Controls";
import { usePrefs } from "@/lib/prefs";
import { useStatus } from "@/lib/status";
import { hhmm, hhmmss, parseUtc, shortStamp, zoneLabel, type TimeZoneChoice } from "@/lib/time";
import type { ControlChange, Status, StatusEvent, StatusUnit } from "@/api/types";

export function StatusScreen() {
  return (
    <AppShell>
      <StatusBody />
    </AppShell>
  );
}

function StatusBody() {
  const status = useStatus();
  const { zone } = usePrefs();
  const at = status.data?.available ? parseUtc(status.data.generated_utc) : null;
  return (
    <>
      <PageHead title="Status" note={at ? <>As reported by the laptop at <span className="num">{hhmm(at, zone)}</span> {zoneLabel(zone)}</> : "As reported by the laptop at each upload"} />
      <DataBlock<Status> title="Bots and timers" data={status}
        empty={<><strong>No data uploaded yet</strong><span className="upd">The laptop’s first upload brings each bot’s state here (build step 4).</span></>}>
        {(s) => <Units units={s.units ?? []} zone={zone} />}
      </DataBlock>
      {status.data?.available && (
        <>
          <div className="grid-2">
            <Events events={status.data.events ?? []} zone={zone} />
            <Uploads s={status.data} zone={zone} />
          </div>
          <Controls changes={status.data.controls ?? []} zone={zone} />
        </>
      )}
    </>
  );
}

function Units({ units, zone }: { units: StatusUnit[]; zone: TimeZoneChoice }) {
  const when = (v: string | null | undefined) => shortStamp(parseUtc(v), zone);
  return (
    <div className="grid-tiles" style={{ padding: 16 }}>
      {units.map((u) => {
        const rows: [string, string][] =
          u.kind === "bot"
            ? [[u.state === "running" ? "Since" : "Stopped", when(u.since_utc)], ["Build", u.build ?? "—"], ["Last log line", when(u.last_log_utc)]]
            : [["Kind", `timer, ${u.schedule ?? ""}`.replace(/, $/, "")], ["Last", `${when(u.last_run_utc)}${u.last_result === "failed" ? " ✕ failed" : u.last_result === "ok" ? " ✓" : ""}`], ["Next", when(u.next_run_utc)]];
        return (
          <section key={u.name} className="card" aria-label={u.name} style={{ padding: 16, display: "flex", flexDirection: "column", gap: 10, boxShadow: "none" }}>
            <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
              <h3 style={{ margin: 0, fontSize: 15, fontWeight: 600, flex: 1 }}>{u.name}</h3>
              <StatePill state={u.state} />
            </div>
            <dl className="kv">
              {rows.map(([k, v]) => (
                <div key={k} style={{ display: "contents" }}><dt>{k}</dt><dd className="num">{v}</dd></div>
              ))}
            </dl>
            {u.kind === "bot" && (
              <div style={{ display: "flex", gap: 8 }}>
                <span className={`chip ${u.errors_today ? "t-red" : "t-plain"}`}>✕ {u.errors_today ?? 0} errors</span>
                <span className={`chip ${u.warnings_today ? "t-amber" : "t-plain"}`}>▲ {u.warnings_today ?? 0} warnings</span>
              </div>
            )}
          </section>
        );
      })}
    </div>
  );
}

function Events({ events, zone }: { events: StatusEvent[]; zone: TimeZoneChoice }) {
  const bots = ["All", ...Array.from(new Set(events.map((e) => e.bot)))];
  const [bot, setBot] = useState("All");
  const shown = bot === "All" ? events : events.filter((e) => e.bot === bot);
  return (
    <section className="card" aria-labelledby="ev-h">
      <div className="ch"><h2 id="ev-h">Today’s warnings and errors</h2><span style={{ flex: 1 }} /><FilterChips label="Bot" value={bot} options={bots} onChange={setBot} /></div>
      {shown.map((e, i) => (
        <div key={i} className="event-row">
          <span aria-label={e.level} style={{ color: e.level === "error" ? "var(--red)" : "var(--amber)", fontWeight: 700 }}>{e.level === "error" ? "✕" : "▲"}</span>
          <span className="num" style={{ color: "var(--text-3)" }}>{hhmmss(parseUtc(e.time_utc), zone)}</span>
          <span style={{ fontWeight: 600 }}>{e.bot}</span>
          <span>{e.message}</span>
        </div>
      ))}
      {shown.length === 0 && <p className="blkb empty" style={{ margin: 0 }}>No warnings or errors today{bot === "All" ? "" : ` for ${bot}`}.</p>}
    </section>
  );
}

function Uploads({ s, zone }: { s: Status; zone: TimeZoneChoice }) {
  const t = (v: string | null | undefined) => hhmm(parseUtc(v), zone);
  const cells: [string, string, string][] = [
    ["Last data upload", t(s.upload?.last_utc), s.upload?.last_utc ? `${s.upload.seconds ?? "?"} s · ${s.upload.files ?? "?"} files` : "none yet"],
    ["Next upload", t(s.upload?.next_utc), "every 30 min, at :02 and :32"],
    ["Last control sync", t(s.sync?.last_utc), s.sync?.ack_received ? "ack from Scout received" : "no ack yet"],
    ["Next sync", t(s.sync?.next_utc), "every minute"],
  ];
  return (
    <section className="card" aria-labelledby="up-h">
      <div className="ch"><h2 id="up-h">Uploads and sync</h2></div>
      <dl className="quad">
        {cells.map(([k, v, note]) => (
          <div key={k}><dt className="upd">{k}</dt><dd className="num">{v}</dd><span className="upd">{note}</span></div>
        ))}
      </dl>
    </section>
  );
}

function Controls({ changes, zone }: { changes: ControlChange[]; zone: TimeZoneChoice }) {
  const tone = { waiting: "t-blue", applied: "t-green", refused: "t-amber" } as const;
  const label = (c: ControlChange) =>
    c.applied === "applied" ? `✓ applied ${hhmm(parseUtc(c.applied_utc), zone)}` : c.applied === "refused" ? `▲ refused: ${c.reason ?? "no reason given"}` : "◷ waiting";
  return (
    <section className="card" aria-labelledby="ctl-h">
      <div className="ch"><h2 id="ctl-h">Control history</h2><span className="upd">Last 50 changes · newest first</span></div>
      {changes.length === 0 ? (
        <p className="blkb empty" style={{ margin: 0 }}>No control changes yet. Switches for Scout arrive in build step 6.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead><tr><th scope="col">Time {zoneLabel(zone)}</th><th scope="col">Change</th><th scope="col">By</th><th scope="col">Applied</th></tr></thead>
            <tbody>
              {changes.map((c, i) => (
                <tr key={i}><td className="num">{shortStamp(parseUtc(c.time_utc), zone)}</td><td>{c.change}</td><td style={{ color: "var(--text-2)" }}>{c.by}</td><td><span className={`chip ${tone[c.applied]}`}>{label(c)}</span></td></tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
