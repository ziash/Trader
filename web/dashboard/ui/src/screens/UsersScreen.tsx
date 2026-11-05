"use client";

// Users (owner only; dashboard.md §5.4, Users.dc.html): access requests, people, machines, the sign-in log.
import { useState } from "react";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock } from "@/components/DataBlock";
import { Chip, RoleBadge, type Tone } from "@/components/Chips";
import { ConfirmSheet, FilterChips, Switch } from "@/components/Controls";
import { Robot } from "@/components/Icons";
import { api, ApiError } from "@/lib/api";
import { usePrefs } from "@/lib/prefs";
import { useSession } from "@/lib/session";
import { useData, type Data } from "@/lib/useData";
import { parseUtc, shortStamp, zoneLabel, type TimeZoneChoice } from "@/lib/time";
import type { LogRow, UsersOverview } from "@/api/types";

export function UsersScreen() {
  return (
    <AppShell>
      <UsersBody />
    </AppShell>
  );
}

function UsersBody() {
  const session = useSession();
  const isOwner = session.state === "signedIn" && session.me.role === "owner";
  const users = useData<UsersOverview>(isOwner ? "/api/users" : null);
  const { zone } = usePrefs();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<null | { kind: "remove" | "revoke"; name: string }>(null);

  if (!isOwner) {
    return (
      <>
        <PageHead title="Users" />
        <section className="card"><div className="blkb empty"><strong>Only the owner sees this page.</strong></div></section>
      </>
    );
  }

  const act = async (path: string, body: Record<string, unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await api.post(path, body);
      users.refresh();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setBusy(false);
      setConfirm(null);
    }
  };

  const accept = users.data?.settings.access_requests ?? true;
  return (
    <>
      <PageHead title="Users" note="Who may enter, and how. Only you see this page.">
        <label className="setting-box">
          <span style={{ display: "flex", flexDirection: "column", lineHeight: 1.3 }}>
            <span style={{ fontWeight: 500, fontSize: 13 }}>Accept access requests</span>
            <span className="upd">{accept ? "Unknown accounts can ask" : "Unknown accounts are refused"}</span>
          </span>
          <Switch label="Accept access requests" on={accept} disabled={busy || !users.data} onChange={(v) => void act("/api/users/settings", { access_requests: v })} />
        </label>
      </PageHead>
      {error && <div role="alert" className="note t-red">{error}</div>}

      <Requests users={users} zone={zone} busy={busy}
        onApprove={(email) => void act("/api/users/approve", { email })}
        onRefuse={(email) => void act("/api/users/refuse", { email })} />

      <DataBlock<UsersOverview> title="People" data={users}>
        {(u) => (
          <div className="tbl-wrap">
            <table className="tbl">
              <thead><tr><th scope="col">Person</th><th scope="col">Role</th><th scope="col">Last seen</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                <tr>
                  <td><Person email={u.owner.email ?? ""} /></td>
                  <td><RoleBadge role="owner" /></td>
                  <td className="num" style={{ color: "var(--text-2)" }}>{shortStamp(parseUtc(u.owner.last_seen_utc), zone)}</td>
                  <td style={{ textAlign: "right" }}><span className="upd">Fixed on the server</span></td>
                </tr>
                {u.people.map((p) => (
                  <tr key={p.email}>
                    <td><Person email={p.email ?? ""} note={p.last_seen_utc ? undefined : `approved ${shortStamp(parseUtc(p.approved_utc), zone)}, not signed in since`} /></td>
                    <td><RoleBadge role={p.role ?? "viewer"} /></td>
                    <td className="num" style={{ color: "var(--text-2)" }}>{p.last_seen_utc ? shortStamp(parseUtc(p.last_seen_utc), zone) : "never"}</td>
                    <td style={{ textAlign: "right" }}><button type="button" className="btn dng" disabled={busy} onClick={() => setConfirm({ kind: "remove", name: p.email ?? "" })}>Remove</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </DataBlock>

      <DataBlock<UsersOverview> title="Machines" data={users} actions={<span className="upd">M2M sign-in · client ID + secret → 1-hour token</span>}>
        {(u) => (
          <>
            {u.machines.length === 0 ? (
              <p className="blkb empty" style={{ margin: 0 }}>No machine accounts yet. They are made on the laptop with <span className="num">scripts/dashboard-m2m.sh</span>.</p>
            ) : (
              <div className="tbl-wrap">
                <table className="tbl">
                  <thead><tr><th scope="col">Name</th><th scope="col">Role</th><th scope="col">Client ID</th><th scope="col">Last sign-in</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead>
                  <tbody>
                    {u.machines.map((m) => (
                      <tr key={m.name}>
                        <td><span style={{ display: "inline-flex", alignItems: "center", gap: 10 }}><span className="av sm t-blue" style={{ borderRadius: 8 }} aria-hidden="true"><Robot /></span><span className="num">{m.name}</span></span></td>
                        <td>{m.revoked_utc ? <Chip tone="red">Revoked</Chip> : <RoleBadge role={m.role ?? "viewer"} />}</td>
                        <td className="num" style={{ fontSize: 12 }}>{m.client_id}</td>
                        <td className="num" style={{ color: "var(--text-2)" }}>{m.last_seen_utc ? shortStamp(parseUtc(m.last_seen_utc), zone) : "never"}</td>
                        <td style={{ textAlign: "right" }}>
                          {m.revoked_utc ? <span className="upd">since {shortStamp(parseUtc(m.revoked_utc), zone)}</span>
                            : <button type="button" className="btn dng" disabled={busy} onClick={() => setConfirm({ kind: "revoke", name: m.name ?? "" })}>Revoke</button>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <p className="upd" style={{ margin: 0, padding: "12px 16px", borderTop: "1px solid var(--border)" }}>
              New machines and new secrets are made on the laptop (scripts/dashboard-m2m.sh). Machines can read and change Scout’s options, never this page.
            </p>
          </>
        )}
      </DataBlock>

      <SignInLog users={users} zone={zone} />

      {confirm?.kind === "revoke" && (
        <ConfirmSheet title={`Revoke ${confirm.name}?`} action="Revoke" danger busy={busy} onCancel={() => setConfirm(null)}
          onConfirm={() => void act("/api/users/machines/revoke", { name: confirm.name })}>
          Its sign-in stops at once and any token it holds stops working. To let it back in you make a new secret on the laptop.
        </ConfirmSheet>
      )}
      {confirm?.kind === "remove" && (
        <ConfirmSheet title={`Remove ${confirm.name}?`} action="Remove" danger busy={busy} onCancel={() => setConfirm(null)}
          onConfirm={() => void act("/api/users/remove", { email: confirm.name })}>
          They are signed out at once. If they sign in again, it comes back to you as a new access request.
        </ConfirmSheet>
      )}
    </>
  );
}

function Person({ email, note }: { email: string; note?: string }) {
  return (
    <span style={{ display: "flex", alignItems: "center", gap: 10 }}>
      <span className="av sm t-grey" aria-hidden="true">{email.slice(0, 1).toUpperCase()}</span>
      <span><span style={{ fontWeight: 500 }}>{email}</span>{note && <span className="upd" style={{ display: "block" }}>{note}</span>}</span>
    </span>
  );
}

function Requests({ users, zone, busy, onApprove, onRefuse }: {
  users: Data<UsersOverview>; zone: TimeZoneChoice; busy: boolean; onApprove: (e: string) => void; onRefuse: (e: string) => void;
}) {
  const n = users.data?.requests.length ?? 0;
  return (
    <div style={n ? { borderRadius: "var(--r-xl)", outline: "1px solid var(--amber-line)" } : undefined}>
      <DataBlock<UsersOverview> title="Access requests" data={users} actions={n > 0 ? <Chip tone="amber">{n} waiting</Chip> : undefined}>
        {(u) => u.requests.length === 0 ? (
          <p className="blkb empty" style={{ margin: 0 }}>No requests waiting.</p>
        ) : (
          <>
            {u.requests.map((r) => (
              <div key={r.email} className="request-row">
                <span className="av t-grey" aria-hidden="true">{(r.email ?? "?").slice(0, 1).toUpperCase()}</span>
                <div style={{ flex: "1 1 240px", minWidth: 0 }}>
                  <div style={{ fontWeight: 600 }}>{r.email}</div>
                  <div className="upd">Google · asked <span className="num">{shortStamp(parseUtc(r.requested_utc), zone)}</span>{r.last_try_utc !== r.requested_utc && <> · last tried <span className="num">{shortStamp(parseUtc(r.last_try_utc), zone)}</span></>} · from <span className="num">{r.ip}</span></div>
                </div>
                <button type="button" className="btn dng" disabled={busy} onClick={() => onRefuse(r.email ?? "")}>Refuse</button>
                <button type="button" className="btn pri" disabled={busy} onClick={() => onApprove(r.email ?? "")}>Approve as viewer</button>
              </div>
            ))}
          </>
        )}
      </DataBlock>
    </div>
  );
}

const EVENTS: Record<string, { text: string; mark: string; tone: Tone }> = {
  signin: { text: "Signed in", mark: "✓", tone: "green" },
  signin_refused: { text: "Refused", mark: "✕", tone: "red" },
  access_requested: { text: "Access requested", mark: "◷", tone: "blue" },
  signout: { text: "Signed out", mark: "−", tone: "grey" },
  m2m_token: { text: "Token issued", mark: "✓", tone: "green" },
  m2m_refused: { text: "Wrong secret", mark: "✕", tone: "red" },
  m2m_locked: { text: "Locked out", mark: "▲", tone: "amber" },
  approve: { text: "Approved as viewer", mark: "✓", tone: "green" },
  refuse: { text: "Request refused", mark: "✕", tone: "red" },
  remove: { text: "Viewer removed", mark: "−", tone: "grey" },
  revoke: { text: "Machine revoked", mark: "✕", tone: "red" },
  settings: { text: "Setting changed", mark: "•", tone: "plain" },
  api: { text: "API call", mark: "•", tone: "plain" },
};

export function describe(row: LogRow): { text: string; mark: string; tone: Tone; who: string; how: string } {
  const e = EVENTS[row.event ?? ""] ?? { text: row.event ?? "?", mark: "•", tone: "plain" as Tone };
  const byOwner = ["approve", "refuse", "remove", "revoke", "settings"].includes(row.event ?? "");
  const machine = (row.event ?? "").startsWith("m2m") || row.event === "api";
  return {
    ...e,
    who: byOwner ? row.detail ?? "" : row.who || "—",
    how: byOwner ? `by ${row.who}` : machine ? (row.event === "api" ? `M2M · ${row.detail}` : "M2M") : row.event === "signin_refused" ? `Google · ${row.detail}` : "Google",
  };
}

function SignInLog({ users, zone }: { users: Data<UsersOverview>; zone: TimeZoneChoice }) {
  const [filter, setFilter] = useState<"Sign-ins" | "All">("Sign-ins");
  return (
    <DataBlock<UsersOverview> title="Sign-in log" data={users}
      actions={<FilterChips<"Sign-ins" | "All"> label="Log filter" value={filter} options={["Sign-ins", "All"]} onChange={setFilter} />}>
      {(u) => {
        const rows = u.log.filter((r) => filter === "All" || r.event !== "api").slice(0, 50);
        return rows.length === 0 ? <p className="blkb empty" style={{ margin: 0 }}>Nothing logged yet.</p> : (
          <div className="tbl-wrap">
            <table className="tbl">
              <thead><tr><th scope="col">Time {zoneLabel(zone)}</th><th scope="col">Event</th><th scope="col">Account</th><th scope="col">How</th><th scope="col">Address</th></tr></thead>
              <tbody>
                {rows.map((r, i) => {
                  const d = describe(r);
                  return (
                    <tr key={i}>
                      <td className="num">{shortStamp(parseUtc(r.time_utc), zone)}</td>
                      <td><Chip tone={d.tone}>{d.mark} {d.text}</Chip></td>
                      <td>{d.who}</td>
                      <td style={{ color: "var(--text-2)" }}>{d.how}</td>
                      <td className="num" style={{ color: "var(--text-3)", fontSize: 12 }}>{r.ip}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        );
      }}
    </DataBlock>
  );
}
