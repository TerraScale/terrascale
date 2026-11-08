import { describe, expect, it } from "vitest";
import { coverage, vantageLabel } from "./coverage";

const VANTAGES = [
  { code: "sfo", name: "US West", placement: "aws:us-west-1", colos: ["SFO", "SJC"] },
  { code: "gru", name: "South America", placement: "aws:sa-east-1", colos: ["GRU"] },
  { code: "fra", name: "Europe", placement: "aws:eu-central-1", colos: ["FRA"] },
  { code: "nrt", name: "Asia Pacific", placement: "aws:ap-northeast-1", colos: ["NRT"] },
];
const ROUND = "2026-10-12T14:47:00Z";
const now = Date.parse(ROUND) + 10_000;
const at = (code: string, colo: string | null, seenAt = ROUND) => ({ code, colo, seenAt });
const verifiedAll = [at("sfo", "SJC"), at("gru", "GRU"), at("fra", "FRA"), at("nrt", "NRT")];
const run = (seen: Parameters<typeof coverage>[0]["seen"], lastRoundAt: number | null = Date.parse(ROUND)) =>
  coverage({ vantages: VANTAGES, seen, lastRoundAt, now });

describe("monitoring coverage", () => {
  it("is full only when all four answered the latest round from expected data centers", () => {
    const result = run(verifiedAll);
    expect(result).toMatchObject({ level: "full", answered: 4, verified: 4, total: 4, minimum: 2 });
    expect(result.vantages[1]).toEqual({
      code: "gru",
      name: "South America",
      requested: "aws:sa-east-1",
      observed: "GRU",
      lastAnswerAt: Date.parse(ROUND),
      state: "verified",
    });
  });

  it("is insufficient with zero or one answering vantage: HTTP consensus cannot decide", () => {
    expect(run([])).toMatchObject({ level: "insufficient", answered: 0, verified: 0 });
    const one = run([at("fra", "FRA"), at("sfo", "SJC", "2026-10-12T14:00:00Z")]);
    expect(one).toMatchObject({ level: "insufficient", answered: 1, verified: 1 });
    expect(one.vantages.map((entry) => entry.state)).toEqual(["missing", "missing", "verified", "missing"]);
    // The last answer is kept even when it is old.
    expect(one.vantages[0]?.lastAnswerAt).toBe(Date.parse("2026-10-12T14:00:00Z"));
  });

  it("is degraded with two answering vantages: consensus decides but coverage is not full", () => {
    expect(run([at("fra", "FRA"), at("nrt", "NRT")])).toMatchObject({ level: "degraded", answered: 2, verified: 2 });
  });

  it("never verifies an answer without location proof or from another data center", () => {
    const result = run([at("sfo", "SJC"), at("gru", "IAD"), at("fra", null), at("nrt", "NRT")]);
    expect(result).toMatchObject({ level: "degraded", answered: 4, verified: 2 });
    expect(result.vantages.map((entry) => [entry.code, entry.state, entry.observed])).toEqual([
      ["sfo", "verified", "SJC"],
      ["gru", "mismatched", "IAD"],
      ["fra", "unverified", null],
      ["nrt", "verified", "NRT"],
    ]);
  });

  it("counts nothing as answered when the latest round is stale or missing", () => {
    const stale = coverage({
      vantages: VANTAGES,
      seen: verifiedAll,
      lastRoundAt: Date.parse(ROUND),
      now: now + 3 * 60_000,
    });
    expect(stale).toMatchObject({ level: "insufficient", answered: 0, verified: 0 });
    expect(stale.vantages.every((entry) => entry.state === "missing" && entry.observed !== null)).toBe(true);
    expect(run(verifiedAll, null)).toMatchObject({ level: "insufficient", answered: 0 });
  });

  it("labels a vantage with where it runs unless its area is proven", () => {
    const result = run([at("sfo", "SJC"), at("gru", "EWR"), at("fra", null)]);
    expect(VANTAGES.map((vantage) => vantageLabel(result, vantage))).toEqual([
      "US West",
      "South America (runs in EWR)",
      "Europe (location unproven)",
      "Asia Pacific",
    ]);
  });
});
