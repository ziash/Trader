"use client";

// Screens whose data arrives in a later build step (dashboard.md §8): the page exists, with an honest empty block.
import { AppShell, PageHead } from "@/components/AppShell";
import { NotYet } from "@/components/DataBlock";

export function Later({ title, step, what }: { title: string; step: number; what: string }) {
  return (
    <AppShell>
      <PageHead title={title} />
      <NotYet title={title}>
        <strong>Not built yet</strong>
        <span className="upd">{what} — arrives in build step {step}.</span>
      </NotYet>
    </AppShell>
  );
}
