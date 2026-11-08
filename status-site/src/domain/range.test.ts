import { describe, expect, it } from "vitest";
import { byHour, isRange, rangeStart } from "./range";

describe("ranges", () => {
  const now = Date.parse("2026-09-26T10:15:00Z");

  it("start on UTC boundaries and end with the bucket holding now", () => {
    expect(new Date(rangeStart("24h", now)).toISOString()).toBe("2026-09-25T11:00:00.000Z");
    expect(new Date(rangeStart("7d", now)).toISOString()).toBe("2026-09-19T12:00:00.000Z");
    expect(new Date(rangeStart("90d", now)).toISOString()).toBe("2026-06-29T00:00:00.000Z");
  });

  it("read hours for sub-day buckets", () => {
    expect(byHour("7d")).toBe(true);
    expect(byHour("30d")).toBe(false);
    expect(isRange("7d")).toBe(true);
    expect(isRange("1y")).toBe(false);
  });
});
