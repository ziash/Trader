"use client";

// Basic controls (HANDOFF §2): real buttons with ARIA roles; every touch target at least 44 px tall where it stands alone.
import { useEffect, useRef, type ReactNode } from "react";

export function Switch({ on, onChange, label, disabled }: { on: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <button type="button" role="switch" aria-checked={on} aria-label={label} disabled={disabled} className={`sw${on ? " on" : ""}`} onClick={() => onChange(!on)}>
      <span className="k" />
    </button>
  );
}

export function Segmented<T extends string>({ value, options, onChange, label }: { value: T; options: { value: T; label: string }[]; onChange: (v: T) => void; label: string }) {
  return (
    <div role="radiogroup" aria-label={label} className="seg">
      {options.map((o) => (
        <button key={o.value} type="button" role="radio" aria-checked={value === o.value} className={value === o.value ? "on" : ""} onClick={() => onChange(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function FilterChips<T extends string>({ value, options, onChange, label }: { value: T; options: T[]; onChange: (v: T) => void; label: string }) {
  return (
    <div role="group" aria-label={label} style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
      {options.map((o) => (
        <button key={o} type="button" className={`fchip${value === o ? " on" : ""}`} aria-pressed={value === o} onClick={() => onChange(o)}>
          {o}
        </button>
      ))}
    </div>
  );
}

/** A modal with a title, the sentence to confirm, Cancel and one action (centred on desktop). */
export function ConfirmSheet({ title, children, action, danger, onCancel, onConfirm, busy }: {
  title: string; children: ReactNode; action: string; danger?: boolean; onCancel: () => void; onConfirm: () => void; busy?: boolean;
}) {
  const cancel = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    cancel.current?.focus();
    const esc = (e: KeyboardEvent) => e.key === "Escape" && onCancel();
    window.addEventListener("keydown", esc);
    return () => window.removeEventListener("keydown", esc);
  }, [onCancel]);
  return (
    <div className="scrim" onClick={onCancel}>
      <div role="dialog" aria-modal="true" aria-labelledby="confirm-h" className="dialog" onClick={(e) => e.stopPropagation()}>
        <h2 id="confirm-h">{title}</h2>
        <div className="dialog-body">{children}</div>
        <div className="dialog-actions">
          <button ref={cancel} type="button" className="btn tall" onClick={onCancel}>Cancel</button>
          <button type="button" className={`btn tall ${danger ? "danger" : "pri"}`} onClick={onConfirm} disabled={busy} aria-busy={busy}>{action}</button>
        </div>
      </div>
    </div>
  );
}
