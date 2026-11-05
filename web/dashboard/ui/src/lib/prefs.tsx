"use client";

// Per-viewer conveniences kept in this browser only: theme, time display and the chosen account.
import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import type { TimeZoneChoice } from "./time";

export type ThemeChoice = "system" | "light" | "dark";

type Prefs = {
  theme: ThemeChoice;
  setTheme: (t: ThemeChoice) => void;
  zone: TimeZoneChoice;
  setZone: (z: TimeZoneChoice) => void;
  /** An account id ("brokerA-49123893") as chosen last; checked against the uploaded list where it is used (useAccount). */
  account: string | null;
  setAccount: (a: string) => void;
};

const PrefsContext = createContext<Prefs | null>(null);

function stored<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const v = window.localStorage.getItem(key);
    return v && (allowed as readonly string[]).includes(v) ? (v as T) : fallback;
  } catch {
    return fallback;
  }
}

function store(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    /* private window: the choice lasts for this page only */
  }
}

export function applyTheme(theme: ThemeChoice): void {
  if (theme === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
}

/** Runs before the page paints (inline in <head>) so a chosen theme never flashes. */
export const THEME_BOOT_SCRIPT = `try{var t=localStorage.getItem("td-theme");if(t==="light"||t==="dark")document.documentElement.setAttribute("data-theme",t)}catch(e){}`;

export function PrefsProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<ThemeChoice>("system");
  const [zone, setZoneState] = useState<TimeZoneChoice>("utc");
  const [account, setAccountState] = useState<string | null>(null);

  useEffect(() => {
    setThemeState(stored("td-theme", ["system", "light", "dark"] as const, "system"));
    setZoneState(stored("td-zone", ["utc", "local"] as const, "utc"));
    try {
      setAccountState(window.localStorage.getItem("td-account"));
    } catch {
      /* private window */
    }
  }, []);

  const setTheme = useCallback((t: ThemeChoice) => {
    setThemeState(t);
    applyTheme(t);
    store("td-theme", t);
  }, []);
  const setZone = useCallback((z: TimeZoneChoice) => {
    setZoneState(z);
    store("td-zone", z);
  }, []);

  const setAccount = useCallback((a: string) => {
    setAccountState(a);
    store("td-account", a);
  }, []);

  return <PrefsContext.Provider value={{ theme, setTheme, zone, setZone, account, setAccount }}>{children}</PrefsContext.Provider>;
}

export function usePrefs(): Prefs {
  const p = useContext(PrefsContext);
  if (!p) throw new Error("usePrefs outside PrefsProvider");
  return p;
}
