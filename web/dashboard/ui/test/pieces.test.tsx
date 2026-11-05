import { describe as group, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { render, screen } from "@testing-library/react";
import { freshness, FreshnessPill } from "@/components/FreshnessPill";
import { DataBlock } from "@/components/DataBlock";
import { PrefsProvider } from "@/lib/prefs";
import { ApiError } from "@/lib/api";
import { ago, dayLabel, hhmm, parseUtc, shortStamp } from "@/lib/time";
import { readAccess } from "@/screens/SignIn";
import { describe as describeLog } from "@/screens/UsersScreen";
import type { Data } from "@/lib/useData";

vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: vi.fn() }), usePathname: () => "/" }));

group("design tokens", () => {
  it("are the exact copy of docs/design/tokens.css", () => {
    expect(readFileSync("src/styles/tokens.css", "utf8")).toBe(readFileSync("../../../docs/design/tokens.css", "utf8"));
  });
});

group("time", () => {
  const d = parseUtc("2026-11-04 10:02:51")!;
  it("reads the bots' UTC format", () => {
    expect(d.toISOString()).toBe("2026-11-04T10:02:51.000Z");
    expect(parseUtc("nonsense")).toBeNull();
    expect(parseUtc(null)).toBeNull();
  });
  it("formats in UTC", () => {
    expect(hhmm(d, "utc")).toBe("10:02");
    expect(dayLabel(d, "utc")).toBe("Wed 04 Nov");
    expect(shortStamp(d, "utc", new Date("2026-11-04T23:00:00Z"))).toBe("10:02");
    expect(shortStamp(d, "utc", new Date("2026-11-05T01:00:00Z"))).toBe("04 Nov 10:02");
  });
  it("says how old", () => {
    const now = new Date("2026-11-04T10:08:51Z");
    expect(ago(d, now)).toBe("6 min ago");
    expect(ago(d, new Date("2026-11-04T13:02:51Z"))).toBe("3 h ago");
  });
});

group("freshness pill", () => {
  const now = new Date("2026-11-04T12:00:00Z");
  it("is green under 45 min, amber to 2 h, red after", () => {
    expect(freshness("2026-11-04 11:16:00", now)).toBe("green");
    expect(freshness("2026-11-04 11:15:00", now)).toBe("amber");
    expect(freshness("2026-11-04 10:01:00", now)).toBe("amber");
    expect(freshness("2026-11-04 10:00:00", now)).toBe("red");
    expect(freshness(null, now)).toBe("none");
  });
  it("never shows colour alone", () => {
    render(<FreshnessPill generatedUtc="2026-11-04 09:00:00" zone="utc" now={now} />);
    expect(screen.getByRole("status")).toHaveTextContent("Laptop offline? Last data 3 h ago");
    render(<FreshnessPill generatedUtc={null} zone="utc" now={now} />);
    expect(screen.getByText("No data uploaded yet")).toBeInTheDocument();
  });
});

group("sign-in page", () => {
  it("reads only the known outcomes from the address", () => {
    expect(readAccess("?access=requested")).toBe("requested");
    expect(readAccess("?access=refused")).toBe("refused");
    expect(readAccess("?access=<script>")).toBe("none");
    expect(readAccess("")).toBe("none");
  });
});

group("data block", () => {
  const base: Data<{ n: number }> = { status: "ready", data: { n: 7 }, error: null, updatedAt: new Date("2026-11-04T10:02:00Z"), refresh: vi.fn() };
  const show = (d: Partial<Data<{ n: number }>>) =>
    render(<PrefsProvider><DataBlock title="Balance" data={{ ...base, ...d }} empty={<strong>Nothing</strong>}>{(x) => <p>Value {x.n}</p>}</DataBlock></PrefsProvider>);

  it("shows the content and when it was updated", () => {
    show({});
    expect(screen.getByText("Value 7")).toBeInTheDocument();
    expect(screen.getByText(/Updated/)).toHaveTextContent("Updated 10:02");
  });
  it("keeps the old content while refreshing", () => {
    show({ status: "refreshing" });
    expect(screen.getByText("Value 7")).toBeInTheDocument();
    expect(screen.getByText(/Updating/)).toBeInTheDocument();
  });
  it("shows skeletons while loading, the empty message when empty", () => {
    const { container } = show({ status: "loading", data: null });
    expect(container.querySelectorAll(".skeleton").length).toBeGreaterThan(0);
    show({ status: "empty" });
    expect(screen.getByText("Nothing")).toBeInTheDocument();
  });
  it("offers Try again on an error and Sign in again when signed out", () => {
    show({ status: "error", data: null, error: new ApiError(503, "x", "down") });
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
    show({ status: "error", data: null, error: new ApiError(401, "not_signed_in", "Please sign in") });
    expect(screen.getByRole("link", { name: "Sign in again" })).toBeInTheDocument();
  });
});

group("sign-in log", () => {
  it("names who and how for each event", () => {
    const row = (event: string, who: string, detail = "") => ({ time_utc: "2026-11-04 10:00:00", event, who, role: "", ip: "192.0.2.1", user_agent: "", detail });
    expect(describeLog(row("signin", "owner@example.com", "google"))).toMatchObject({ text: "Signed in", who: "owner@example.com", how: "Google" });
    expect(describeLog(row("approve", "owner@example.com", "someone@example.com"))).toMatchObject({ text: "Approved as viewer", who: "someone@example.com", how: "by owner@example.com" });
    expect(describeLog(row("m2m_refused", "m2m_x_00000000"))).toMatchObject({ text: "Wrong secret", how: "M2M", tone: "red" });
    expect(describeLog(row("something_new", "x"))).toMatchObject({ text: "something_new" });
  });
});
