import { describe, expect, it } from "vitest";
import { formatDateTime, formatDuration, formatUptime } from "./format";

describe("format", () => {
  it("shows times in UTC", () => {
    expect(formatDateTime("2026-09-20T08:45:00Z")).toBe("Sep 20, 2026, 08:45 UTC");
  });

  it("never rounds uptime up to 100%", () => {
    expect(formatUptime(0.99999)).toBe("99.99%");
    expect(formatUptime(1)).toBe("100%");
    expect(formatUptime(null)).toBe("No data");
  });

  it("formats durations compactly", () => {
    expect(formatDuration(30_000)).toBe("under a minute");
    expect(formatDuration(72 * 60_000)).toBe("1h 12m");
    expect(formatDuration(52 * 3_600_000)).toBe("2d 4h");
  });
});
