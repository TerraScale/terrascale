/* Regional latency from the latest completed UTC hour of D1 rollups. */

import { vantageLabel, type Coverage } from "../domain/coverage";
import { percentile } from "../domain/histogram";
import type { TimelineState } from "../domain/status";
import { HOUR, startOfHour } from "../format";
import type { Measurements } from "../measure";
import type { RegionOverview } from "../overview";
import { VANTAGES } from "../probe/vantages";

const statuses: Record<TimelineState, "operational" | "degraded" | "down" | "planned"> = {
  operational: "operational",
  degraded: "degraded",
  partial_outage: "down",
  major_outage: "down",
  maintenance: "planned",
  none: "planned",
};

const iso = (at: number | null) => (at === null ? null : new Date(at).toISOString());

export function regionLatencyDocument(
  regions: readonly RegionOverview[],
  measurements: Measurements,
  now: number,
  coverage: Coverage,
) {
  const to = startOfHour(now);
  // Where each vantage runs now: `name` stays the area, `label` says when it is elsewhere.
  const where = (code: string, name: string) => {
    const entry = coverage.vantages.find((item) => item.code === code);
    return {
      label: vantageLabel(coverage, { code, name }),
      requested_location: entry?.requested ?? null,
      observed_colo: entry?.observed ?? null,
      last_answer_at: iso(entry?.lastAnswerAt ?? null),
      coverage: entry?.state ?? "missing",
    };
  };
  return {
    generated_at: new Date(now).toISOString(),
    window: "1h",
    monitoring_coverage: (({ vantages: _vantages, ...summary }) => summary)(coverage),
    regions: regions.map((region) => {
      const ids = region.clusters.map((cluster) => cluster.id);
      const stats = (vantage: string) => {
        const { hist } = measurements.counts(ids, vantage, to - HOUR, to, HOUR);
        return {
          median_ms: percentile(hist, 0.5),
          p95_ms: percentile(hist, 0.95),
          samples: hist.reduce((sum, count) => sum + count, 0),
        };
      };
      return {
        code: region.code.toLowerCase(),
        name: region.name,
        ping_url: region.pingUrl,
        status: statuses[region.status],
        consensus: stats("all"),
        vantages: VANTAGES.map(({ code, name }) => ({ code, name, ...stats(code), ...where(code, name) })),
      };
    }),
  };
}
