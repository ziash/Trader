"use client";

// One fetched block of data with the states every DataBlock shows (HANDOFF §2):
// loading → ready | empty | error; a refresh keeps the old content ("refreshing"); data older than 45 min is "stale".
import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "./api";

export type BlockStatus = "loading" | "ready" | "refreshing" | "empty" | "error";

export type Data<T> = {
  status: BlockStatus;
  data: T | null;
  error: ApiError | null;
  updatedAt: Date | null;
  refresh: () => void;
};

export const REFRESH_MS = 30 * 60 * 1000;

export function useData<T>(path: string | null, opts: { isEmpty?: (d: T) => boolean; refreshMs?: number } = {}): Data<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(true);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const isEmpty = useRef(opts.isEmpty);
  isEmpty.current = opts.isEmpty;

  const load = useCallback(() => {
    if (!path) return;
    setBusy(true);
    api.get<T>(path).then(
      (d) => {
        setData(d);
        setError(null);
        setUpdatedAt(new Date());
        setBusy(false);
      },
      (e: unknown) => {
        setError(e instanceof ApiError ? e : new ApiError(0, "error", String(e)));
        setBusy(false);
      },
    );
  }, [path]);

  useEffect(() => {
    load();
    const every = opts.refreshMs ?? REFRESH_MS;
    const t = window.setInterval(load, every);
    return () => window.clearInterval(t);
  }, [load, opts.refreshMs]);

  let status: BlockStatus;
  if (busy && data === null) status = "loading";
  else if (busy) status = "refreshing";
  else if (error && data === null) status = "error";
  else if (data !== null && isEmpty.current?.(data)) status = "empty";
  else status = data === null ? "error" : "ready";

  return { status, data, error, updatedAt, refresh: load };
}
