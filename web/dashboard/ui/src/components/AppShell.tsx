"use client";

// Sidebar and top bar around every signed-in screen (HANDOFF §2 AppShell). Signed-out visitors go to Sign in.
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useSession } from "@/lib/session";
import { usePrefs, type ThemeChoice } from "@/lib/prefs";
import { StatusProvider, useAccount, useStatus } from "@/lib/status";
import { useData } from "@/lib/useData";
import type { UsersOverview } from "@/api/types";
import { FreshnessPill } from "./FreshnessPill";
import { RoleBadge } from "./Chips";
import { Segmented } from "./Controls";
import { Chevron, Lock } from "./Icons";

const SECTIONS = [
  ["/scout/", "Scout"],
  ["/symbols/", "Symbols"],
  ["/trades/", "Trades"],
  ["/accounts/", "Accounts"],
  ["/conditions/", "Conditions"],
  ["/settings/", "Settings"],
  ["/status/", "Status"],
  ["/audit/", "Audit"],
] as const;

export function AppShell({ children }: { children: ReactNode }) {
  const session = useSession();
  const router = useRouter();

  useEffect(() => {
    if (session.state === "signedOut") router.replace("/?access=expired");
  }, [session.state, router]);

  if (session.state === "error") {
    return (
      <main className="center-page">
        <div className="card" role="alert" style={{ padding: 20, maxWidth: 420 }}>
          <h1 style={{ fontSize: 18, margin: "0 0 8px" }}>The dashboard is not answering</h1>
          <p style={{ margin: "0 0 12px", color: "var(--text-2)" }}>{session.message}</p>
          <button type="button" className="btn" onClick={session.reload}>Try again</button>
        </div>
      </main>
    );
  }
  if (session.state !== "signedIn") {
    return <main className="center-page" aria-busy="true"><span className="upd">Loading…</span></main>;
  }
  return (
    <StatusProvider>
      <Shell role={session.me.role} id={session.me.id} signOut={session.signOut}>{children}</Shell>
    </StatusProvider>
  );
}

function Shell({ role, id, signOut, children }: { role: "owner" | "admin" | "viewer"; id: string; signOut: () => Promise<void>; children: ReactNode }) {
  const path = usePathname();
  const { zone, setZone } = usePrefs();
  const status = useStatus();
  const { accounts, account, setAccount } = useAccount();
  const isOwner = role === "owner";
  const users = useData<UsersOverview>(isOwner ? "/api/users" : null);
  const waiting = users.data?.requests.length ?? 0;
  const here = (href: string) => path === href || path === href.slice(0, -1);

  return (
    <div className="shell">
      <nav className="nav" aria-label="Sections">
        <div className="brand">
          <div className="logo" aria-hidden="true">TD</div>
          <div><div style={{ fontWeight: 600 }}>Trader Dashboard</div><div className="upd">Private</div></div>
        </div>
        {SECTIONS.map(([href, label]) => (
          <Link key={href} href={href} className={here(href) ? "on" : ""} aria-current={here(href) ? "page" : undefined}>{label}</Link>
        ))}
        {isOwner && (
          <Link href="/users/" className={here("/users/") ? "on" : ""} aria-current={here("/users/") ? "page" : undefined}>
            Users
            {waiting > 0 && <span className="count t-amber" aria-label={`${waiting} access request${waiting > 1 ? "s" : ""}`}>{waiting}</span>}
          </Link>
        )}
      </nav>
      <div className="main-col">
        <header className="topbar">
          <FreshnessPill generatedUtc={status.data?.available ? status.data.generated_utc : null} zone={zone} />
          {accounts.length > 1 && account && (
            <Segmented label="Account" value={account.id} onChange={setAccount} options={accounts.map((a) => ({ value: a.id, label: a.name }))} />
          )}
          <Segmented label="Time display" value={zone} onChange={setZone} options={[{ value: "utc", label: "UTC" }, { value: "local", label: "Local" }]} />
          <span style={{ flex: 1 }} />
          {role === "viewer" && <span className="pill outline"><Lock size={12} />View only</span>}
          <UserMenu id={id} role={role} signOut={signOut} />
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}

function UserMenu({ id, role, signOut }: { id: string; role: "owner" | "admin" | "viewer"; signOut: () => Promise<void> }) {
  const [open, setOpen] = useState(false);
  const { theme, setTheme } = usePrefs();
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent | KeyboardEvent) => {
      if (e instanceof KeyboardEvent ? e.key === "Escape" : !box.current?.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", close);
    window.addEventListener("keydown", close);
    return () => {
      window.removeEventListener("mousedown", close);
      window.removeEventListener("keydown", close);
    };
  }, [open]);
  return (
    <div ref={box} style={{ position: "relative" }}>
      <button type="button" className="user-btn" aria-haspopup="true" aria-expanded={open} aria-label="User menu" onClick={() => setOpen(!open)}>
        <span className="av sm" aria-hidden="true">{id.slice(0, 1).toUpperCase()}</span>
        <span className="user-id">{id}</span>
        <RoleBadge role={role} />
        <Chevron size={14} open={open} />
      </button>
      {open && (
        <div className="menu">
          <div className="upd" style={{ padding: "4px 4px 8px" }}>Theme</div>
          <Segmented<ThemeChoice> label="Theme" value={theme} onChange={setTheme}
            options={[{ value: "system", label: "System" }, { value: "light", label: "Light" }, { value: "dark", label: "Dark" }]} />
          <hr />
          <button type="button" className="btn" style={{ width: "100%" }} onClick={() => void signOut()}>Sign out</button>
        </div>
      )}
    </div>
  );
}

/** Page title row. */
export function PageHead({ title, note, children }: { title: string; note?: ReactNode; children?: ReactNode }) {
  return (
    <div className="page-head">
      <div><h1>{title}</h1>{note && <span className="upd">{note}</span>}</div>
      <span style={{ flex: 1 }} />
      {children}
    </div>
  );
}
