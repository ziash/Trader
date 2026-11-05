"use client";

// Sign in (brief §6.1, SignIn.dc.html): Google for people; machines sign in through the API, never on this page.
import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useSession } from "@/lib/session";
import { Chevron, Clock, Cross, GoogleG, Robot, Spinner, Warn } from "@/components/Icons";

export type Access = "none" | "requested" | "refused" | "error" | "expired";

export function readAccess(search: string): Access {
  const v = new URLSearchParams(search).get("access");
  return v === "requested" || v === "refused" || v === "error" || v === "expired" ? v : "none";
}

export function SignIn() {
  const session = useSession();
  const router = useRouter();
  const [access, setAccess] = useState<Access>("none");
  const [busy, setBusy] = useState(false);
  const [m2m, setM2m] = useState(false);

  useEffect(() => setAccess(readAccess(window.location.search)), []);
  useEffect(() => {
    if (session.state === "signedIn") router.replace("/scout/");
  }, [session.state, router]);

  const showOptions = access === "none" || access === "expired" || access === "error";
  const again = () => {
    window.history.replaceState(null, "", "/");
    setAccess("none");
  };

  return (
    <main className="signin">
      <div className="signin-card">
        <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
          <div className="logo big" aria-hidden="true">TD</div>
          <h1 className="display">Trader Dashboard</h1>
          <p style={{ margin: 0, color: "var(--text-2)" }}>Scout, TM and accounts — private</p>
        </div>

        {access === "expired" && <div role="status" className="note t-blue">Your session has ended. Please sign in again.</div>}
        {access === "error" && (
          <div role="alert" className="note t-amber"><Warn size={16} /> Google’s answer could not be checked. Please try again.</div>
        )}

        {showOptions && (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <a href="/auth/google" className="google-btn" aria-busy={busy} aria-disabled={busy}
              onClick={(e) => { if (busy) e.preventDefault(); else setBusy(true); }}>
              {busy ? <Spinner size={18} /> : <GoogleG />}
              {busy ? "Signing in…" : "Sign in with Google"}
            </a>
            <button type="button" className="link-btn" aria-expanded={m2m} aria-controls="m2m-panel" onClick={() => setM2m(!m2m)}>
              <Robot size={16} /> Machine sign-in (M2M) <Chevron size={14} open={m2m} />
            </button>
            {m2m && (
              <div id="m2m-panel" className="m2m-panel">
                <p style={{ margin: 0 }}>For programs such as Claude. Machines sign in with a client ID and secret through the API (<span className="num">POST /api/auth/token</span>), not on this page.</p>
              </div>
            )}
          </div>
        )}

        {access === "requested" && (
          <div className="result-card">
            <div className="result-title" style={{ color: "var(--blue)" }}><Clock size={18} />Access requested</div>
            <p>Thanks — your request to view this dashboard is waiting for the owner’s approval. Sign in again once you have been told it is approved.</p>
            <button type="button" className="btn tall" onClick={again}>Back to sign in</button>
          </div>
        )}
        {access === "refused" && (
          <div className="result-card">
            <div className="result-title" style={{ color: "var(--red)" }}><Cross size={18} />Not approved</div>
            <p>The owner has not approved this account.</p>
            <button type="button" className="btn tall" onClick={again}>Use another account</button>
          </div>
        )}

        <p className="upd" style={{ margin: 0 }}>
          No passwords and no sign-up here. New people ask for access with Google; the owner approves them. <a href="/privacy.html">Privacy</a>
        </p>
      </div>
    </main>
  );
}
