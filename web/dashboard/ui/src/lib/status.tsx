"use client";

// /api/status, fetched once for the whole page (top bar's data age and the Status page), every 30 minutes.
import { createContext, useContext, type ReactNode } from "react";
import { useData, type Data } from "./useData";
import { usePrefs } from "./prefs";
import type { Account, Status } from "@/api/types";

const StatusContext = createContext<Data<Status> | null>(null);

export function StatusProvider({ children }: { children: ReactNode }) {
  const data = useData<Status>("/api/status", { isEmpty: (s) => !s.available });
  return <StatusContext.Provider value={data}>{children}</StatusContext.Provider>;
}

export function useStatus(): Data<Status> {
  const s = useContext(StatusContext);
  if (!s) throw new Error("useStatus outside StatusProvider");
  return s;
}

/** The account the pages show (top bar selector): the last one chosen, else the first Scout trades on, else the first. */
export function useAccount(): { accounts: Account[]; account: Account | null; setAccount: (id: string) => void } {
  const status = useStatus();
  const { account: chosen, setAccount } = usePrefs();
  const accounts = (status.data?.available && status.data.accounts) || [];
  const account = accounts.find((a) => a.id === chosen) ?? accounts.find((a) => a.scout) ?? accounts[0] ?? null;
  return { accounts, account, setAccount };
}
