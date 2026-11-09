import { describe as group, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ControlStateChip } from "@/lib/controls";
import { SettingsBody } from "@/screens/SettingsScreen";
import { PrefsProvider } from "@/lib/prefs";
import { StatusProvider } from "@/lib/status";

group("Controls", () => {
  it("says how far a change has got, in words", () => {
    const { rerender } = render(<ControlStateChip state={{ state: "saved", utc: "2026-11-06 18:00:00" }} zone="utc" />);
    expect(screen.getByText("Saved · waiting for Scout")).toBeTruthy();
    rerender(<ControlStateChip state={{ state: "applied", utc: "2026-11-06 18:01:00", cancelled: [1036848429] }} zone="utc" />);
    expect(screen.getByText("Applied by Scout 18:01 · 1 order cancelled")).toBeTruthy();
    rerender(<ControlStateChip state={{ state: "refused", reason: "needs a newer Scout" }} zone="utc" />);
    expect(screen.getByText("Refused: needs a newer Scout")).toBeTruthy();
  });
});

group("Settings", () => {
  const view = {
    account: "brokerB-49124666", available: true, can_change: true, catalogue_version: 2, version: 3, knows: ["scout.dailyDirection"], symbols: [], setups: {},
    options: [{ key: "dailyDirection", scope: "scout", type: "switch", default: true, label: "Trade with the daily direction", help: "Help text", confirm: "Are you sure?" }],
    scout: { set: {}, state: {} },
  };
  const mockData = (data: unknown) => {
    (globalThis as { fetch: unknown }).fetch = vi.fn(async (url: string) => new Response(JSON.stringify(String(url).includes("/api/controls") ? data : {
      available: true, accounts: [{ id: "brokerB-49124666", broker: "brokerB", ctid: 49124666, name: "Pepperstone", scout: true, tm: true }],
    }), { status: 200, headers: { "content-type": "application/json" } }));
  };

  it("shows one tab per project, and Scout Lens and TM say they have no options yet", async () => {
    mockData(view);
    render(<PrefsProvider><StatusProvider><SettingsBody /></StatusProvider></PrefsProvider>);
    for (const name of ["Scout", "Scout Lens", "TM"]) expect(await screen.findByRole("radio", { name })).toBeTruthy();
    fireEvent.click(screen.getByRole("radio", { name: "Scout Lens" }));
    expect(screen.getByText("Scout Lens has no settings yet")).toBeTruthy();
    fireEvent.click(screen.getByRole("radio", { name: "TM" }));
    expect(screen.getByText("TM has no settings on this site yet")).toBeTruthy();
  });

  it("shows the daily switch on by default, and follows what was set", async () => {
    mockData(view);
    const { unmount } = render(<PrefsProvider><StatusProvider><SettingsBody /></StatusProvider></PrefsProvider>);
    const on = await screen.findByRole("switch", { name: "Trade with the daily direction" });
    expect(on.getAttribute("aria-checked")).toBe("true");
    unmount();
    mockData({ ...view, scout: { set: { dailyDirection: false }, state: { dailyDirection: { state: "saved", utc: "2026-11-09 12:00:00" } } } });
    render(<PrefsProvider><StatusProvider><SettingsBody /></StatusProvider></PrefsProvider>);
    await waitFor(() => expect(screen.getByRole("switch", { name: "Trade with the daily direction" }).getAttribute("aria-checked")).toBe("false"));
    expect(screen.getByText("Saved · waiting for Scout")).toBeTruthy();
  });
});
