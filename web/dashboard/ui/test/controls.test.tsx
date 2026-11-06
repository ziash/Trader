import { describe as group, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { ControlStateChip } from "@/lib/controls";

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
