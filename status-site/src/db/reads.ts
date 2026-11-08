/* D1 reads. Each page costs one batched round trip on a session that may be
   served by the nearest read replica. Timestamps are stored as SQLite
   datetime text ("YYYY-MM-DD HH:MM:SS", UTC) and returned as ISO 8601. */

import type { Env } from "../env";
import { logEvent } from "../log";
import { HOUR } from "../format";
import { SAMPLE_RETENTION_MS } from "../probe/retention";
import {
  countsOf,
  probeState,
  typicalLatency,
  type Component,
  type ComponentGroup,
  type Endpoint,
  type Incident,
  type IncidentUpdate,
  type LatestSample,
  type ProbePeriod,
  type Region,
  type TimelineState,
  type VantageSample,
} from "../domain/status";

/** When a vantage last answered a round, and where it proved it ran. */
export interface VantageSeen {
  code: string;
  colo: string | null;
  seenAt: string | null;
}

/** The latest logged TLS certificate of a host. */
export interface Certificate {
  host: string;
  notAfter: string | null;
  issuer: string | null;
  checkedAt: string;
  error: string | null;
}

export interface Snapshot {
  groups: ComponentGroup[];
  components: Component[];
  incidents: Incident[];
  regions: Region[];
  endpoints: Endpoint[];
  latest: LatestSample[];
  vantages: VantageSeen[];
  certificates: Certificate[];
  /** Consensus counts per endpoint and hour, then per endpoint and day. */
  hours: ProbePeriod[];
  days: ProbePeriod[];
}

export const iso = (column: string) => `strftime('%Y-%m-%dT%H:%M:%SZ', ${column})`;

export const INCIDENT_COLUMNS = `id, kind, title, status, impact, source,
  (SELECT body FROM incident_updates u WHERE u.incident_id = incidents.id
    ORDER BY u.created_at DESC, u.rowid DESC LIMIT 1) AS message,
  (SELECT json_group_array(component_id) FROM incident_components c WHERE c.incident_id = incidents.id) AS components,
  ${iso("scheduled_start")} AS scheduledStart, ${iso("scheduled_end")} AS scheduledEnd,
  ${iso("created_at")} AS createdAt, ${iso("updated_at")} AS updatedAt, ${iso("resolved_at")} AS resolvedAt`;

type IncidentRow = Omit<Incident, "components"> & { components: string };

function toIncident(row: IncidentRow): Incident {
  let components: string[] = [];
  try {
    const value: unknown = JSON.parse(row.components);
    if (Array.isArray(value)) components = value.filter((item): item is string => typeof item === "string");
  } catch {
    // json_group_array always yields valid JSON, so this is corruption: say
    // so rather than quietly treating the incident as platform-wide.
    logEvent("error", "malformed incident components", { component: "reads", id: row.id, components: row.components });
  }
  return { ...row, message: row.message ?? "", components };
}

export const incidents = (result: D1Result | undefined): Incident[] =>
  ((result?.results ?? []) as IncidentRow[]).map(toIncident);

const GROUPS = `SELECT id, name FROM component_groups ORDER BY position, id`;

const COMPONENTS = `SELECT id, group_id AS groupId, name, description, lifecycle
  FROM components ORDER BY position, id`;

// Open incidents, those resolved since the cutoff, and maintenance windows
// ending after it, upcoming ones included. Open ones rank first so the limit
// can only ever drop closed history.
const CURRENT_INCIDENTS = `SELECT ${INCIDENT_COLUMNS} FROM incidents
  WHERE (kind != 'maintenance' AND (status != 'resolved' OR resolved_at >= ?1))
    OR (kind = 'maintenance' AND coalesce(resolved_at, scheduled_end) >= ?1)
  ORDER BY status NOT IN ('resolved', 'completed') DESC, created_at DESC LIMIT 50`;

const REGIONS = `SELECT code, name, country, map_x AS mapX, map_y AS mapY, ping_url AS pingUrl, ping_label AS pingLabel
  FROM regions ORDER BY position, code`;

const ENDPOINTS = `SELECT id, component_id AS componentId, region_code AS regionCode, cluster, provider, url,
    ${iso("live_since")} AS liveSince
  FROM endpoints ORDER BY position, id`;

const CERTIFICATES = `SELECT host, ${iso("not_after")} AS notAfter, issuer, ${iso("checked_at")} AS checkedAt, error FROM tls_certs`;

// Joined from the endpoints so every lookup is a primary key seek.
const LATEST = `SELECT s.endpoint_id AS endpointId, ${iso("s.at")} AS at, s.state, s.ok, s.total,
    s.latency_ms AS latencyMs, s.results
  FROM endpoints e JOIN probe_samples s ON s.endpoint_id = e.id
    AND s.at = (SELECT max(at) FROM probe_samples WHERE endpoint_id = e.id)`;

const periods = (table: "probe_hours" | "probe_days", column: "hour" | "day") =>
  `SELECT p.endpoint_id AS endpointId, ${iso(`p.${column}`)} AS start, p.checks, p.failures, p.degraded,
    p.latency_sum AS latencySum, p.hist
  FROM endpoints e JOIN ${table} p ON p.endpoint_id = e.id AND p.${column} >= ?1 AND p.vantage = 'all'`;

const VANTAGES = `SELECT code, colo, ${iso("seen_at")} AS seenAt FROM vantages`;

// Incidents and warnings matching the filters (null matches anything),
// created in [?5, ?6), newest first.
const HISTORY = `SELECT ${INCIDENT_COLUMNS} FROM incidents
  WHERE kind != 'maintenance'
    AND (?1 IS NULL OR kind = ?1)
    AND (?2 IS NULL OR impact = ?2)
    AND (?3 IS NULL OR EXISTS (SELECT 1 FROM incident_components c WHERE c.incident_id = incidents.id AND c.component_id = ?3))
    AND (?4 IS NULL OR title LIKE '%' || ?4 || '%' ESCAPE '\\')
    AND created_at >= ?5 AND created_at < ?6
  ORDER BY created_at DESC LIMIT 500`;

const INCIDENT = `SELECT ${INCIDENT_COLUMNS} FROM incidents WHERE id = ?1`;

const UPDATES = `SELECT id, incident_id AS incidentId, status, body, ${iso("created_at")} AS createdAt
  FROM incident_updates WHERE incident_id = ?1 ORDER BY created_at DESC, rowid DESC`;

const MAINTENANCE = `SELECT ${INCIDENT_COLUMNS} FROM incidents
  WHERE kind = 'maintenance' AND coalesce(resolved_at, scheduled_end) >= ?1
  ORDER BY scheduled_start DESC LIMIT 100`;

// One series point per round or hour of an endpoint, for incident charts.
const SAMPLE_SERIES = `SELECT s.endpoint_id AS endpointId, ${iso("s.at")} AS start, s.latency_ms AS latencyMs, s.state
  FROM endpoints e JOIN probe_samples s ON s.endpoint_id = e.id AND s.at >= ?1 AND s.at < ?2
  WHERE e.id IN (SELECT value FROM json_each(?3))`;

const HOUR_SERIES = `SELECT p.endpoint_id AS endpointId, ${iso("p.hour")} AS start, p.checks, p.failures, p.degraded,
    p.latency_sum AS latencySum, p.hist
  FROM endpoints e JOIN probe_hours p ON p.endpoint_id = e.id AND p.hour >= ?1 AND p.hour < ?2 AND p.vantage = 'all'
  WHERE e.id IN (SELECT value FROM json_each(?3))`;

function parseResults(text: string): VantageSample[] {
  try {
    const value: unknown = JSON.parse(text);
    return Array.isArray(value) ? (value as VantageSample[]) : [];
  } catch {
    return [];
  }
}

/** SQLite datetime text for a moment, matching the stored format. */
export function sqlTime(at: number): string {
  return new Date(at).toISOString().slice(0, 19).replace("T", " ");
}

export function session(env: Env): D1DatabaseSession {
  return env.STATUS_DB.withSession("first-unconstrained");
}

export interface SnapshotWindow {
  /** Incidents open at any time from here on. */
  incidentsFrom: number;
  /** Hourly probe rollups from here on. */
  hoursFrom: number;
  /** Daily probe rollups from here on. */
  daysFrom: number;
}

/** Everything the status page and API show, in one batched round trip. */
export async function loadSnapshot(env: Env, window: SnapshotWindow): Promise<Snapshot> {
  const db = session(env);
  const [groups, components, current, regions, endpoints, latest, hours, days, vantages, certificates] = await db.batch(
    [
      db.prepare(GROUPS),
      db.prepare(COMPONENTS),
      db.prepare(CURRENT_INCIDENTS).bind(sqlTime(window.incidentsFrom)),
      db.prepare(REGIONS),
      db.prepare(ENDPOINTS),
      db.prepare(LATEST),
      db.prepare(periods("probe_hours", "hour")).bind(sqlTime(window.hoursFrom)),
      db.prepare(periods("probe_days", "day")).bind(sqlTime(window.daysFrom)),
      db.prepare(VANTAGES),
      db.prepare(CERTIFICATES),
    ],
  );
  return {
    groups: (groups?.results ?? []) as ComponentGroup[],
    components: (components?.results ?? []) as Component[],
    incidents: incidents(current),
    regions: (regions?.results ?? []) as Region[],
    endpoints: (endpoints?.results ?? []) as Endpoint[],
    latest: ((latest?.results ?? []) as (Omit<LatestSample, "results"> & { results: string })[]).map((sample) => ({
      ...sample,
      results: parseResults(sample.results),
    })),
    vantages: (vantages?.results ?? []) as VantageSeen[],
    certificates: (certificates?.results ?? []) as Certificate[],
    hours: (hours?.results ?? []) as ProbePeriod[],
    days: (days?.results ?? []) as ProbePeriod[],
  };
}

export interface HistoryFilters {
  kind?: string;
  impact?: string;
  component?: string;
  q?: string;
  from: number;
  to: number;
}

export interface History {
  incidents: Incident[];
  components: Component[];
}

/** Incidents and warnings matching the filters, and the catalog to filter by. */
export async function loadHistory(env: Env, filters: HistoryFilters): Promise<History> {
  const db = session(env);
  const like = filters.q?.replace(/[\\%_]/g, (char) => `\\${char}`) ?? null;
  const [found, components] = await db.batch([
    db
      .prepare(HISTORY)
      .bind(
        filters.kind ?? null,
        filters.impact ?? null,
        filters.component ?? null,
        like,
        sqlTime(filters.from),
        sqlTime(filters.to),
      ),
    db.prepare(COMPONENTS),
  ]);
  return { incidents: incidents(found), components: (components?.results ?? []) as Component[] };
}

/** A latency point of one endpoint: one round, or one hour. */
export interface SeriesPoint {
  endpointId: string;
  start: string;
  latencyMs: number | null;
  state: TimelineState;
}

export interface IncidentDetail {
  incident: Incident;
  updates: IncidentUpdate[];
  components: Component[];
  endpoints: Endpoint[];
  /** Latency of the affected endpoints around the incident, and whether it
      comes from rounds (recent incidents) or hourly rollups. */
  series: SeriesPoint[];
  resolution: "round" | "hour";
  window: { from: number; to: number };
}

/** Raw rounds are read this far back, an hour short of their retention so
    pruning never cuts into a window; older incidents chart hourly rollups. */
const ROUNDS_KEPT_MS = SAMPLE_RETENTION_MS - HOUR;

/** An incident with its timeline, affected components, and the latency of
    their endpoints from half an hour before to half an hour after it. */
export async function loadIncident(env: Env, id: string, now: number): Promise<IncidentDetail | null> {
  const db = session(env);
  const [found, updates, components, endpoints] = await db.batch([
    db.prepare(INCIDENT).bind(id),
    db.prepare(UPDATES).bind(id),
    db.prepare(COMPONENTS),
    db.prepare(ENDPOINTS),
  ]);
  const incident = incidents(found)[0];
  if (!incident) return null;

  const allComponents = (components?.results ?? []) as Component[];
  const allEndpoints = (endpoints?.results ?? []) as Endpoint[];
  const affected = allEndpoints.filter((endpoint) =>
    incident.components.length === 0
      ? allComponents.some((component) => component.id === endpoint.componentId && component.lifecycle === "active")
      : incident.components.includes(endpoint.componentId),
  );
  const start = Date.parse(incident.scheduledStart ?? incident.createdAt);
  const end = Date.parse(incident.resolvedAt ?? incident.scheduledEnd ?? new Date(now).toISOString());
  const window = { from: start - 30 * 60_000, to: Math.min(now, Math.max(end, start) + 30 * 60_000) };
  const resolution = now - window.from <= ROUNDS_KEPT_MS && window.to - window.from <= 12 * HOUR ? "round" : "hour";
  const ids = JSON.stringify(affected.map((endpoint) => endpoint.id));
  const hourFrom = window.from - (window.from % HOUR);
  const { results } = await db
    .prepare(resolution === "round" ? SAMPLE_SERIES : HOUR_SERIES)
    .bind(sqlTime(resolution === "round" ? window.from : hourFrom), sqlTime(window.to), ids)
    .all<SeriesPoint | ProbePeriod>();
  const series = (results ?? []).map((row): SeriesPoint => {
    if ("latencyMs" in row) return row;
    const counts = countsOf(row);
    return {
      endpointId: row.endpointId,
      start: row.start,
      latencyMs: typicalLatency(counts),
      state: probeState(counts),
    };
  });
  return {
    incident,
    updates: (updates?.results ?? []) as IncidentUpdate[],
    components: allComponents.filter((component) => incident.components.includes(component.id)),
    endpoints: affected,
    series,
    resolution,
    window,
  };
}

/** Maintenance windows ending after `from`, upcoming ones included. */
export async function loadMaintenance(
  env: Env,
  from: number,
): Promise<{ windows: Incident[]; components: Component[] }> {
  const db = session(env);
  const [found, components] = await db.batch([db.prepare(MAINTENANCE).bind(sqlTime(from)), db.prepare(COMPONENTS)]);
  return { windows: incidents(found), components: (components?.results ?? []) as Component[] };
}

// The latest incidents of every kind, by last change.
// Both select the same incidents, so their order must be total.
const RECENT_ORDER = "ORDER BY updated_at DESC, id DESC LIMIT ?1";

const RECENT = `SELECT ${INCIDENT_COLUMNS} FROM incidents ${RECENT_ORDER}`;

const RECENT_UPDATES = `SELECT id, incident_id AS incidentId, status, body, ${iso("created_at")} AS createdAt
  FROM incident_updates WHERE incident_id IN (SELECT id FROM incidents ${RECENT_ORDER})
  ORDER BY created_at DESC, rowid DESC`;

export interface Recent {
  incidents: Incident[];
  /** Updates of those incidents, newest first. */
  updates: IncidentUpdate[];
  components: Component[];
}

/** The latest `limit` incidents, warnings and maintenance windows with
    their updates, for feeds and APIs. */
export async function loadRecent(env: Env, limit: number): Promise<Recent> {
  const db = session(env);
  const [found, updates, components] = await db.batch([
    db.prepare(RECENT).bind(limit),
    db.prepare(RECENT_UPDATES).bind(limit),
    db.prepare(COMPONENTS),
  ]);
  return {
    incidents: incidents(found),
    updates: (updates?.results ?? []) as IncidentUpdate[],
    components: (components?.results ?? []) as Component[],
  };
}
