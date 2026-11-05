// Times in the bots' files are UTC, "yyyy-MM-dd HH:mm:ss". The page shows UTC by default, the browser's local time on request.

export type TimeZoneChoice = "utc" | "local";

export function parseUtc(value: string | null | undefined): Date | null {
  if (!value) return null;
  const m = /^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2})(?::(\d{2}))?/.exec(value);
  if (!m) return null;
  return new Date(Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +(m[6] ?? 0)));
}

function parts(d: Date, zone: TimeZoneChoice) {
  const utc = zone === "utc";
  return {
    y: utc ? d.getUTCFullYear() : d.getFullYear(),
    mo: utc ? d.getUTCMonth() : d.getMonth(),
    day: utc ? d.getUTCDate() : d.getDate(),
    wd: utc ? d.getUTCDay() : d.getDay(),
    h: utc ? d.getUTCHours() : d.getHours(),
    mi: utc ? d.getUTCMinutes() : d.getMinutes(),
    s: utc ? d.getUTCSeconds() : d.getSeconds(),
  };
}

const pad = (n: number) => String(n).padStart(2, "0");
const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** "10:02" */
export function hhmm(d: Date | null, zone: TimeZoneChoice): string {
  if (!d) return "—";
  const p = parts(d, zone);
  return `${pad(p.h)}:${pad(p.mi)}`;
}

/** "10:02:51" */
export function hhmmss(d: Date | null, zone: TimeZoneChoice): string {
  if (!d) return "—";
  const p = parts(d, zone);
  return `${pad(p.h)}:${pad(p.mi)}:${pad(p.s)}`;
}

/** "Wed 04 Nov" */
export function dayLabel(d: Date | null, zone: TimeZoneChoice): string {
  if (!d) return "—";
  const p = parts(d, zone);
  return `${DAYS[p.wd]} ${pad(p.day)} ${MONTHS[p.mo]}`;
}

/** "10:02" today, else "04 Nov 10:02" */
export function shortStamp(d: Date | null, zone: TimeZoneChoice, now: Date = new Date()): string {
  if (!d) return "—";
  const p = parts(d, zone);
  const n = parts(now, zone);
  const same = p.y === n.y && p.mo === n.mo && p.day === n.day;
  return same ? hhmm(d, zone) : `${pad(p.day)} ${MONTHS[p.mo]} ${hhmm(d, zone)}`;
}

/** "6 min ago", "3 h ago", "2 days ago" */
export function ago(d: Date | null, now: Date = new Date()): string {
  if (!d) return "never";
  const min = Math.max(0, Math.round((now.getTime() - d.getTime()) / 60000));
  if (min < 1) return "just now";
  if (min < 90) return `${min} min ago`;
  const h = Math.round(min / 60);
  return h < 48 ? `${h} h ago` : `${Math.round(h / 24)} days ago`;
}

export const zoneLabel = (zone: TimeZoneChoice) => (zone === "utc" ? "UTC" : "local");
