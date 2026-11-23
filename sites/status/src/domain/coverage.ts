/* Monitoring coverage: which vantage points answered the latest probe round
   and whether each ran where it was asked to. This is about the monitor, not
   the services: a service can be healthy while coverage is degraded, and HTTP
   checks decide nothing while fewer than MIN_VANTAGES vantages answer. */

import { MIN_VANTAGES } from "./consensus";
import { PROBE_STALE_MS } from "./status";

/** answered and ran in an expected data center; answered with no
    execution proof; answered from somewhere else; did not answer. */
export type VantageState = "verified" | "unverified" | "mismatched" | "missing";

/** full: every vantage verified; degraded: HTTP checks still decide, but
    not every vantage is verified; insufficient: HTTP checks cannot decide. */
export type CoverageLevel = "full" | "degraded" | "insufficient";

export interface VantageCoverage {
  code: string;
  name: string;
  /** Where the vantage was asked to run. */
  requested: string;
  /** Where it last proved it ran, or null without proof. */
  observed: string | null;
  /** When it last answered a round. */
  lastAnswerAt: number | null;
  state: VantageState;
}

export interface Coverage {
  level: CoverageLevel;
  /** Vantages that answered the latest round. */
  answered: number;
  verified: number;
  total: number;
  /** Answers HTTP consensus needs to decide. */
  minimum: number;
  vantages: VantageCoverage[];
}

export interface CoverageInput {
  vantages: readonly { code: string; name: string; placement: string; colos: readonly string[] }[];
  seen: readonly { code: string; colo: string | null; seenAt: string | null }[];
  lastRoundAt: number | null;
  now: number;
}

export function coverage({ vantages, seen, lastRoundAt, now }: CoverageInput): Coverage {
  const fresh = lastRoundAt !== null && now - lastRoundAt <= PROBE_STALE_MS;
  const list = vantages.map((vantage): VantageCoverage => {
    const row = seen.find((entry) => entry.code === vantage.code);
    const lastAnswerAt = row?.seenAt ? Date.parse(row.seenAt) : null;
    const observed = row?.colo ?? null;
    const answered = fresh && lastAnswerAt !== null && lastAnswerAt >= lastRoundAt;
    const state: VantageState = !answered
      ? "missing"
      : observed === null
        ? "unverified"
        : vantage.colos.includes(observed)
          ? "verified"
          : "mismatched";
    return { code: vantage.code, name: vantage.name, requested: vantage.placement, observed, lastAnswerAt, state };
  });
  const answered = list.filter((entry) => entry.state !== "missing").length;
  const verified = list.filter((entry) => entry.state === "verified").length;
  return {
    level: answered < MIN_VANTAGES ? "insufficient" : verified === list.length ? "full" : "degraded",
    answered,
    verified,
    total: list.length,
    minimum: MIN_VANTAGES,
    vantages: list,
  };
}

/** A vantage's name, plus where it really runs unless that is proven to be
    its area: EWR data is never shown as plain "South America". */
export function vantageLabel(coverage: Coverage, vantage: { code: string; name: string }): string {
  const entry = coverage.vantages.find((item) => item.code === vantage.code);
  if (entry?.state === "mismatched") return `${vantage.name} (runs in ${entry.observed})`;
  if (entry?.state === "unverified") return `${vantage.name} (location unproven)`;
  return vantage.name;
}
