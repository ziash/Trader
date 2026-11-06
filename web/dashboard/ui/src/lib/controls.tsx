"use client";

// Scout's controls (dashboard.md §3.4): read them, change one, and say how far a change has got — saved, applied by Scout, refused.
import { useState } from "react";
import { api, ApiError } from "./api";
import { useData } from "./useData";
import { hhmm, parseUtc, type TimeZoneChoice } from "./time";
import { Chip } from "@/components/Chips";
import type { ControlOption, ControlState, ControlsView } from "@/api/types";

export type ControlChange = { scope: "symbol" | "setup"; target: string; key: string; value: boolean | string | null };

/** Every 30 s while on the page, so "saved" turns into "applied" without a reload (the laptop syncs every minute). */
export function useControls(account: string | null) {
  const data = useData<ControlsView>(account ? `/api/controls?account=${encodeURIComponent(account)}` : null, { refreshMs: 30_000 });
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const change = async (c: ControlChange) => {
    if (!account) return;
    setBusy(`${c.scope}|${c.target}|${c.key}`);
    setError(null);
    try {
      await api.post<ControlsView>("/api/controls", { account, ...c });
      data.refresh();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  };
  const option = (scope: string, key: string): ControlOption | undefined => data.data?.options.find((o) => o.scope === scope && o.key === key);
  const knows = (scope: string, key: string) => !data.data?.knows || data.data.knows.includes(`${scope}.${key}`);
  return { data, change, busy, error, option, knows };
}

/** The state of one control, in words (never colour alone). */
export function ControlStateChip({ state, zone }: { state?: ControlState | null; zone: TimeZoneChoice }) {
  if (!state) return null;
  const at = state.utc ? hhmm(parseUtc(state.utc), zone) : "";
  if (state.state === "saved") return <Chip tone="blue">Saved · waiting for Scout</Chip>;
  if (state.state === "refused") return <Chip tone="red">Refused: {state.reason ?? "no reason given"}</Chip>;
  const n = state.cancelled?.length ?? 0;
  return <Chip tone="green">Applied by Scout {at}{n > 0 ? ` · ${n} order${n === 1 ? "" : "s"} cancelled` : ""}</Chip>;
}
