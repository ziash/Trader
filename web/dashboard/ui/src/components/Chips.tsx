// Chips, tags and pills (HANDOFF §2): every coloured state also carries a word or an icon.
import type { Role } from "@/api/types";
import { Clock } from "./Icons";

export type Tone = "green" | "red" | "amber" | "blue" | "grey" | "accent" | "plain";

export function Chip({ tone = "plain", children, label }: { tone?: Tone; children: React.ReactNode; label?: string }) {
  return <span className={`chip t-${tone}`} aria-label={label}>{children}</span>;
}

const ROLE: Record<Role, { text: string; tone: Tone }> = {
  owner: { text: "Owner", tone: "accent" },
  admin: { text: "Administrator", tone: "blue" },
  viewer: { text: "Viewer", tone: "grey" },
};

export function RoleBadge({ role }: { role: Role }) {
  return <Chip tone={ROLE[role].tone}>{ROLE[role].text}</Chip>;
}

export type UnitState = "running" | "stopped" | "failed" | "waiting";

/** Bot or timer state: Running ● / Stopped ○ / Failed ◆ / Waiting (clock). */
export function StatePill({ state }: { state: UnitState }) {
  const map = {
    running: { tone: "green", text: "Running", mark: <span className="dot" aria-hidden="true" /> },
    stopped: { tone: "grey", text: "Stopped", mark: <span className="dot hollow" aria-hidden="true" /> },
    failed: { tone: "red", text: "Failed", mark: <span className="diamond" aria-hidden="true" /> },
    waiting: { tone: "blue", text: "Waiting", mark: <Clock size={12} /> },
  } as const;
  const m = map[state];
  return <span className={`pill t-${m.tone}`}>{m.mark}{m.text}</span>;
}
