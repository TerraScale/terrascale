import { describe, expect, it } from "vitest";
import { consensus, isDown, median, settle, type VantageResult } from "./consensus";

const pass = (vantage: string, latencyMs = 80): VantageResult => ({ vantage, ok: true, latencyMs, error: null });
const fail = (vantage: string): VantageResult => ({ vantage, ok: false, latencyMs: 5_000, error: "Timed out" });

describe("consensus", () => {
  it("is operational when every vantage passes", () => {
    expect(consensus([pass("a", 60), pass("b", 100), pass("c", 90)])).toEqual({
      state: "operational",
      ok: 3,
      total: 3,
      latencyMs: 90,
    });
  });

  it("is degraded when a minority fails or responses are slow", () => {
    expect(consensus([pass("a"), pass("b"), fail("c"), fail("d")]).state).toBe("degraded");
    expect(consensus([pass("a", 2_000), pass("b", 1_900)]).state).toBe("degraded");
  });

  it("is down only when most vantages fail", () => {
    expect(consensus([pass("a"), fail("b"), fail("c"), fail("d")]).state).toBe("major_outage");
    expect(isDown(1, 4)).toBe(true);
    expect(isDown(2, 4)).toBe(false);
  });

  it("cannot judge with fewer than two answers", () => {
    expect(consensus([fail("a")]).state).toBe("none");
    expect(consensus([]).state).toBe("none");
    expect(isDown(0, 1)).toBe(false);
  });
});

describe("settle", () => {
  it("confirms an outage on the second down round", () => {
    expect(settle("major_outage", 0)).toEqual({ state: "degraded", streak: 1 });
    expect(settle("major_outage", 1)).toEqual({ state: "major_outage", streak: 2 });
    expect(settle("operational", 5)).toEqual({ state: "operational", streak: 0 });
  });

  it("keeps the down streak through unknown rounds", () => {
    expect(settle("none", 1)).toEqual({ state: "none", streak: 1 });
    expect(settle("major_outage", 1)).toEqual({ state: "major_outage", streak: 2 });
  });
});

describe("median", () => {
  it("takes the middle value or the mean of the middle two", () => {
    expect(median([3, 1, 2])).toBe(2);
    expect(median([1, 2, 3, 4])).toBe(3);
    expect(median([])).toBeNull();
  });
});
