"use client";

// Settings (dashboard.md §2.2): the catalogue's options that are not per symbol or per setup — for all of Scout or one account.
// Drawn from the laptop's catalogue; today it has none of these, so the page says where today's options are.
import Link from "next/link";
import { AppShell, PageHead } from "@/components/AppShell";
import { DataBlock } from "@/components/DataBlock";
import { useAccount } from "@/lib/status";
import { useControls } from "@/lib/controls";
import type { ControlsView } from "@/api/types";

export function SettingsScreen() {
  return (
    <AppShell>
      <SettingsBody />
    </AppShell>
  );
}

function SettingsBody() {
  const { account } = useAccount();
  const c = useControls(account?.id ?? null);
  return (
    <>
      <PageHead title="Settings" note="Scout’s options for all symbols, from the laptop’s catalogue" />
      <DataBlock<ControlsView> title="Scout’s options" data={c.data} empty={<strong>No options uploaded yet</strong>}>
        {(x) => {
          const general = x.options.filter((o) => o.scope === "scout" || o.scope === "account");
          const elsewhere = x.options.filter((o) => o.scope === "symbol" || o.scope === "setup");
          return (
            <div className="blkb">
              {general.length === 0 ? (
                <p style={{ margin: 0 }}>
                  The catalogue (catalogue v{x.catalogue_version ?? "?"}) has no options for all of Scout yet. Today’s options are per symbol and per setup:
                </p>
              ) : (
                <ul>{general.map((o) => <li key={o.key}><strong>{o.label}</strong> — {o.help}</li>)}</ul>
              )}
              <ul style={{ margin: 0 }}>
                {elsewhere.map((o) => (
                  <li key={`${o.scope}.${o.key}`}>
                    <strong>{o.label}</strong> ({o.scope === "symbol" ? <Link href="/symbols">Symbols page</Link> : <Link href="/scout">each setup on the Scout page</Link>}) — {o.help}
                  </li>
                ))}
              </ul>
              <p className="upd" style={{ margin: 0 }}>A new option is added on the laptop (its catalogue and Scout’s support); it then appears here or on its page by itself.</p>
            </div>
          );
        }}
      </DataBlock>
    </>
  );
}
