"use client";

// The data's age (HANDOFF §2): green under 45 min, amber 45 min–2 h, red over 2 h — each with an icon, never colour alone.
import { ago, hhmm, parseUtc, zoneLabel, type TimeZoneChoice } from "@/lib/time";
import { Clock, Warn } from "./Icons";

export const AMBER_AFTER_MIN = 45;
export const RED_AFTER_MIN = 120;

export function freshness(generatedUtc: string | null | undefined, now: Date = new Date()): "none" | "green" | "amber" | "red" {
  const d = parseUtc(generatedUtc);
  if (!d) return "none";
  const min = (now.getTime() - d.getTime()) / 60000;
  return min < AMBER_AFTER_MIN ? "green" : min < RED_AFTER_MIN ? "amber" : "red";
}

export function FreshnessPill({ generatedUtc, zone, now = new Date() }: { generatedUtc: string | null | undefined; zone: TimeZoneChoice; now?: Date }) {
  const level = freshness(generatedUtc, now);
  const d = parseUtc(generatedUtc);
  if (level === "none") {
    return <span role="status" className="pill t-grey"><Clock size={14} />No data uploaded yet</span>;
  }
  if (level === "red") {
    return <span role="status" className="pill t-red" style={{ fontWeight: 600 }}><Warn size={14} />Laptop offline? Last data {ago(d, now)}</span>;
  }
  return (
    <span role="status" className={`pill t-${level}`}>
      {level === "green" ? <span className="dot" aria-hidden="true" /> : <Clock size={14} />}
      Data <span className="num">{hhmm(d, zone)}</span> {zoneLabel(zone)} · {ago(d, now)}
    </span>
  );
}
