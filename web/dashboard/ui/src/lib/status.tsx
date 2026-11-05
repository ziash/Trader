"use client";

// /api/status, fetched once for the whole page (top bar's data age and the Status page), every 30 minutes.
import { createContext, useContext, type ReactNode } from "react";
import { useData, type Data } from "./useData";
import type { Status } from "@/api/types";

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
