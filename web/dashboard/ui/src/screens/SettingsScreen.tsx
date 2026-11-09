"use client";

// Settings (dashboard.md §2.2): one tab per project. Scout's tab draws the catalogue's options that are not per symbol or per setup, as working
// controls with their state. Scout Lens and TM have no options on the site yet.
import { useState } from "react";
import Link from "next/link";
import { AppShell, PageHead } from "@/components/AppShell";
import { Chip } from "@/components/Chips";
import { ConfirmSheet, Segmented, Switch } from "@/components/Controls";
import { DataBlock } from "@/components/DataBlock";
import { useAccount } from "@/lib/status";
import { usePrefs } from "@/lib/prefs";
import { ControlStateChip, useControls, type ControlChange } from "@/lib/controls";
import type { ControlOption, ControlState, ControlsView } from "@/api/types";

type Tab = "scout" | "lens" | "tm";
const TABS: { value: Tab; label: string }[] = [
  { value: "scout", label: "Scout" },
  { value: "lens", label: "Scout Lens" },
  { value: "tm", label: "TM" },
];

export function SettingsScreen() {
  return (
    <AppShell>
      <SettingsBody />
    </AppShell>
  );
}

export function SettingsBody() {
  const [tab, setTab] = useState<Tab>("scout");
  return (
    <>
      <PageHead title="Settings" note="Each project’s options, from the laptop’s catalogue" />
      <div style={{ marginBottom: 12 }}>
        <Segmented<Tab> value={tab} options={TABS} onChange={setTab} label="Project" />
      </div>
      {tab === "scout" && <ScoutTab />}
      {tab === "lens" && (
        <Nothing title="Scout Lens has no settings yet">
          Scout Lens is still being built and does not touch any order. Its options will appear here when it reaches shadow mode.
        </Nothing>
      )}
      {tab === "tm" && (
        <Nothing title="TM has no settings on this site yet">
          TM’s settings are in <code>tm.json</code> on the laptop. Options appear here once TM has its own catalogue and reads a control file, as Scout does.
        </Nothing>
      )}
    </>
  );
}

function Nothing({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="blk">
      <div className="blkb">
        <p style={{ margin: 0 }}><strong>{title}</strong></p>
        <p style={{ margin: "6px 0 0" }}>{children}</p>
      </div>
    </section>
  );
}

function ScoutTab() {
  const { account } = useAccount();
  const { zone } = usePrefs();
  const c = useControls(account?.id ?? null);
  const [pending, setPending] = useState<{ change: ControlChange; title: string; text: string } | null>(null);
  const ask = (change: ControlChange, title: string, confirm?: string | null) =>
    confirm ? setPending({ change, title, text: confirm }) : void c.change(change);
  return (
    <>
      <DataBlock<ControlsView> title="Scout’s options" data={c.data} empty={<strong>No options uploaded yet</strong>}>
        {(x) => {
          const general = x.options.filter((o) => o.scope === "scout");
          const elsewhere = x.options.filter((o) => o.scope === "symbol" || o.scope === "setup");
          const set = (x.scout?.set ?? {}) as Record<string, unknown>;
          const states = (x.scout?.state ?? {}) as Record<string, ControlState>;
          return (
            <div className="blkb">
              {c.error && <p role="alert" style={{ margin: "0 0 8px" }}><Chip tone="red">{c.error}</Chip></p>}
              {general.length === 0 && <p style={{ margin: 0 }}>The catalogue has no options for all of Scout yet.</p>}
              {general.map((o) => {
                const key = o.key ?? "";
                return (
                  <ScoutOption key={key} option={o} value={set[key]} state={states[key]} zone={zone} account={account?.name ?? x.account}
                    canChange={x.can_change} busy={c.busy === `scout|all|${key}`} knows={c.knows("scout", key)} ask={ask} />
                );
              })}
              <p className="upd" style={{ margin: "14px 0 6px" }}>Set elsewhere:</p>
              <ul style={{ margin: 0 }}>
                {elsewhere.map((o) => (
                  <li key={`${o.scope}.${o.key}`}>
                    <strong>{o.label}</strong> ({o.scope === "symbol" ? <Link href="/symbols">Symbols page</Link> : <Link href="/scout">each setup on the Scout page</Link>})
                  </li>
                ))}
              </ul>
            </div>
          );
        }}
      </DataBlock>
      {pending && (
        <ConfirmSheet title={pending.title} action="Save" onCancel={() => setPending(null)} busy={c.busy !== null}
          onConfirm={async () => { await c.change(pending.change); setPending(null); }}>
          {pending.text}
        </ConfirmSheet>
      )}
    </>
  );
}

function ScoutOption({ option, value, state, zone, account, canChange, busy, knows, ask }: {
  option: ControlOption; value: unknown; state?: ControlState; zone: "utc" | "local"; account: string; canChange: boolean; busy: boolean; knows: boolean;
  ask: (c: ControlChange, title: string, confirm?: string | null) => void;
}) {
  const key = option.key ?? "";
  const on = typeof value === "boolean" ? value : option.default === true;
  return (
    <div style={{ display: "flex", gap: 12, alignItems: "flex-start", justifyContent: "space-between" }}>
      <div>
        <strong>{option.label}</strong>
        <p style={{ margin: "4px 0 6px" }}>{option.help}</p>
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {!knows && <Chip tone="grey">Needs a newer Scout</Chip>}
          <ControlStateChip state={state} zone={zone} />
        </div>
      </div>
      {option.type === "switch" && (
        <Switch on={on} label={option.label ?? key} disabled={!canChange || busy || !knows}
          onChange={(v) => ask({ scope: "scout", target: "all", key, value: v }, `${v ? "Switch on" : "Switch off"} for ${account}?`, option.confirm)} />
      )}
    </div>
  );
}
