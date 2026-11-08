/* Figures over a set of incidents: how many, how long they took to resolve
   (mean time to recovery), the longest, and how often each impact came up. */

import type { Incident, IncidentImpact } from "./status";

export interface IncidentStats {
  count: number;
  resolved: number;
  /** Mean time from start to resolution of resolved incidents. */
  mttrMs: number | null;
  longest: { id: string; title: string; durationMs: number } | null;
  byImpact: Record<Exclude<IncidentImpact, "none">, number>;
}

export function incidentStats(incidents: readonly Incident[]): IncidentStats {
  const durations = incidents.flatMap((incident) =>
    incident.resolvedAt
      ? [{ incident, durationMs: Math.max(0, Date.parse(incident.resolvedAt) - Date.parse(incident.createdAt)) }]
      : [],
  );
  const longest = durations.reduce<(typeof durations)[number] | null>(
    (best, entry) => (best === null || entry.durationMs > best.durationMs ? entry : best),
    null,
  );
  const byImpact = { minor: 0, major: 0, critical: 0 };
  for (const incident of incidents) if (incident.impact !== "none") byImpact[incident.impact] += 1;
  return {
    count: incidents.length,
    resolved: durations.length,
    mttrMs:
      durations.length === 0
        ? null
        : Math.round(durations.reduce((sum, entry) => sum + entry.durationMs, 0) / durations.length),
    longest: longest && { id: longest.incident.id, title: longest.incident.title, durationMs: longest.durationMs },
    byImpact,
  };
}
