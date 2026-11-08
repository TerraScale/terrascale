/* D1 reads for the detail pages: per-vantage rollups, deploys and the
   incidents of a set of endpoints, and a year of daily platform rollups.
   Each is one batched round trip, made after the page's overview. */

import type { Incident, ProbePeriod } from "../domain/status";
import type { Env } from "../env";
import { INCIDENT_COLUMNS, incidents, iso, session, sqlTime } from "./reads";

/** Probe counts of one endpoint, from one vantage or the consensus ('all'). */
export interface VantagePeriod extends ProbePeriod {
  vantage: string;
}

export interface Deploy {
  endpointId: string;
  version: string;
  at: string;
}

export interface Details {
  periods: VantagePeriod[];
  deploys: Deploy[];
  incidents: Incident[];
}

const periods = (table: "probe_hours" | "probe_days", column: "hour" | "day") =>
  `SELECT p.endpoint_id AS endpointId, p.vantage, ${iso(`p.${column}`)} AS start, p.checks, p.failures, p.degraded,
    p.latency_sum AS latencySum, p.hist
  FROM endpoints e JOIN ${table} p ON p.endpoint_id = e.id AND p.${column} >= ?1
  WHERE e.id IN (SELECT value FROM json_each(?2))`;

const DEPLOYS = `SELECT endpoint_id AS endpointId, version, ${iso("detected_at")} AS at FROM deploys
  WHERE endpoint_id IN (SELECT value FROM json_each(?2)) AND detected_at >= ?1 ORDER BY detected_at DESC LIMIT 50`;

// Incidents naming any of the components, or none (the whole platform).
const INCIDENTS = `SELECT ${INCIDENT_COLUMNS} FROM incidents
  WHERE created_at >= ?1 AND kind != 'maintenance' AND (
    EXISTS (SELECT 1 FROM incident_components c WHERE c.incident_id = incidents.id
      AND c.component_id IN (SELECT value FROM json_each(?2)))
    OR NOT EXISTS (SELECT 1 FROM incident_components c WHERE c.incident_id = incidents.id))
  ORDER BY created_at DESC LIMIT 30`;

export interface DetailQuery {
  endpointIds: readonly string[];
  componentIds: readonly string[];
  /** Rollups from here on, hourly or daily. */
  from: number;
  hourly: boolean;
  /** Incidents and deploys from here on. */
  since: number;
}

export async function loadDetails(env: Env, query: DetailQuery): Promise<Details> {
  const db = session(env);
  const endpoints = JSON.stringify(query.endpointIds);
  const [found, deploys, list] = await db.batch([
    db
      .prepare(query.hourly ? periods("probe_hours", "hour") : periods("probe_days", "day"))
      .bind(sqlTime(query.from), endpoints),
    db.prepare(DEPLOYS).bind(sqlTime(query.since), endpoints),
    db.prepare(INCIDENTS).bind(sqlTime(query.since), JSON.stringify(query.componentIds)),
  ]);
  return {
    periods: (found?.results ?? []) as VantagePeriod[],
    deploys: (deploys?.results ?? []) as Deploy[],
    incidents: incidents(list),
  };
}

/** Hourly or daily rollups of the endpoints from `from` on, per vantage and
    for the consensus. */
export async function loadPeriods(
  env: Env,
  endpointIds: readonly string[],
  from: number,
  hourly: boolean,
): Promise<VantagePeriod[]> {
  const { results } = await session(env)
    .prepare(hourly ? periods("probe_hours", "hour") : periods("probe_days", "day"))
    .bind(sqlTime(from), JSON.stringify(endpointIds))
    .all<VantagePeriod>();
  return results ?? [];
}

/** Daily consensus rollups of the endpoints from `from` on. */
export async function loadDays(env: Env, endpointIds: readonly string[], from: number): Promise<VantagePeriod[]> {
  const { results } = await session(env)
    .prepare(`${periods("probe_days", "day")} AND p.vantage = 'all'`)
    .bind(sqlTime(from), JSON.stringify(endpointIds))
    .all<VantagePeriod>();
  return results ?? [];
}
