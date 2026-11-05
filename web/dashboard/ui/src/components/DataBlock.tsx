"use client";

// The wrapper for every block fetched from the API (HANDOFF §2, Components → "Data block states").
import Link from "next/link";
import type { ReactNode } from "react";
import type { Data } from "@/lib/useData";
import { hhmm } from "@/lib/time";
import { usePrefs } from "@/lib/prefs";
import { Refresh } from "./Icons";

export function DataBlock<T>({ title, data, children, empty, actions, headingId }: {
  title: string;
  data: Data<T>;
  children: (d: T) => ReactNode;
  empty?: ReactNode;
  actions?: ReactNode;
  headingId?: string;
}) {
  const { zone } = usePrefs();
  const id = headingId ?? `blk-${title.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`;
  const signedOut = data.error?.status === 401;
  return (
    <section className="card" aria-labelledby={id} aria-busy={data.status === "loading" || data.status === "refreshing"}>
      <div className="ch">
        <h2 id={id}>{title}</h2>
        <span style={{ flex: 1 }} />
        {actions}
        <span className="upd" role="status">
          {data.status === "loading" && "Loading…"}
          {data.status === "refreshing" && <span style={{ color: "var(--blue)" }}>↻ Updating…</span>}
          {data.status === "error" && <span style={{ color: "var(--red)" }}>✕ Not loaded</span>}
          {(data.status === "ready" || data.status === "empty") && data.updatedAt && <>Updated <span className="num">{hhmm(data.updatedAt, zone)}</span></>}
        </span>
        <button type="button" className="icon-btn" aria-label={`Refresh ${title}`} onClick={data.refresh} disabled={data.status === "loading" || data.status === "refreshing"}>
          <Refresh size={14} />
        </button>
      </div>
      {data.status === "loading" && (
        <div className="blkb">
          <div className="skeleton" style={{ height: 22, width: "60%" }} />
          <div className="skeleton" style={{ height: 12, width: "45%" }} />
          <div className="skeleton" style={{ height: 12, width: "80%" }} />
        </div>
      )}
      {data.status === "error" && (
        <div className="blkb" role="alert">
          {signedOut ? (
            <>
              <span>Your session ended. Nothing you changed was lost.</span>
              <Link href="/" style={{ fontWeight: 600 }}>Sign in again</Link>
            </>
          ) : (
            <>
              <span style={{ color: "var(--text-2)" }}>Couldn’t load this block{data.error && data.error.status > 0 ? ` (error ${data.error.status})` : ""}: {data.error?.message}</span>
              <button type="button" className="btn" style={{ alignSelf: "flex-start" }} onClick={data.refresh}>Try again</button>
            </>
          )}
        </div>
      )}
      {data.status === "empty" && <div className="blkb empty">{empty ?? <strong>Nothing here yet</strong>}</div>}
      {(data.status === "ready" || data.status === "refreshing") && data.data !== null && children(data.data)}
    </section>
  );
}

/** A block whose data does not exist yet (later build steps). */
export function NotYet({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="card" aria-label={title}>
      <div className="ch"><h2>{title}</h2></div>
      <div className="blkb empty">{children}</div>
    </section>
  );
}
