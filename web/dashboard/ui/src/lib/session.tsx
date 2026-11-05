"use client";

// Who is signed in (GET /api/me). Screens other than Sign in send a signed-out visitor to "/".
import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { api, ApiError, setCsrf } from "./api";
import type { Me } from "@/api/types";

type SessionState =
  | { state: "loading" }
  | { state: "signedOut" }
  | { state: "signedIn"; me: Me }
  | { state: "error"; message: string };

type Session = SessionState & { signOut: () => Promise<void>; reload: () => void };

const SessionContext = createContext<Session | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [s, setS] = useState<SessionState>({ state: "loading" });
  const [n, setN] = useState(0);

  useEffect(() => {
    let live = true;
    api.get<Me>("/api/me").then(
      (me) => {
        setCsrf(me.csrf ?? null);
        if (live) setS({ state: "signedIn", me });
      },
      (e) => {
        setCsrf(null);
        if (live) setS(e instanceof ApiError && e.status === 401 ? { state: "signedOut" } : { state: "error", message: e instanceof Error ? e.message : String(e) });
      },
    );
    return () => {
      live = false;
    };
  }, [n]);

  const signOut = useCallback(async () => {
    try {
      await api.post("/api/auth/logout");
    } finally {
      setCsrf(null);
      window.location.assign("/");
    }
  }, []);
  const reload = useCallback(() => setN((x) => x + 1), []);

  return <SessionContext.Provider value={{ ...s, signOut, reload }}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const s = useContext(SessionContext);
  if (!s) throw new Error("useSession outside SessionProvider");
  return s;
}
