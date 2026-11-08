import { describe, expect, it, vi } from "vitest";
import type { Env } from "../env";
import { coverage } from "../domain/coverage";
import { VANTAGES, vantageStub } from "./vantages";

describe("vantage placement", () => {
  it("uses placed fetch Workers for the misplaced DOs and retains the working DOs", () => {
    const get = vi.fn((id: string) => id);
    const env = {
      VANTAGE_SFO: {},
      VANTAGE_GRU: {},
      VANTAGE: { idFromName: (name: string) => name, get },
    } as unknown as Env;
    expect(vantageStub(env, VANTAGES[0]!)).toBe(env.VANTAGE_SFO);
    expect(vantageStub(env, VANTAGES[1]!)).toBe(env.VANTAGE_GRU);
    expect(get).not.toHaveBeenCalled();
    VANTAGES.slice(2).forEach((vantage) => vantageStub(env, vantage));
    expect(get.mock.calls).toEqual([
      ["fra", { locationHint: "weur" }],
      ["nrt", { locationHint: "apac" }],
    ]);
  });

  it("still rejects DFW/EWR even after switching to explicit placement regions", () => {
    const now = Date.now();
    const seen = ["DFW", "EWR", "LHR", "SIN"].map((colo, index) => ({
      code: VANTAGES[index]!.code,
      colo,
      seenAt: new Date(now).toISOString(),
    }));
    expect(coverage({ vantages: VANTAGES, seen, now, lastRoundAt: now })).toMatchObject({
      level: "degraded",
      answered: 4,
      verified: 2,
    });
    seen[0]!.colo = "SJC";
    seen[1]!.colo = "GRU";
    expect(coverage({ vantages: VANTAGES, seen, now, lastRoundAt: now })).toMatchObject({
      level: "full",
      answered: 4,
      verified: 4,
    });
  });
});
