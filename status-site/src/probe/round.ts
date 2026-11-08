/* One probe round: every vantage probes every endpoint of an active
   component, and the results become one sample per endpoint. Rounds start
   on the half minute and are keyed by that moment, so a retried round never
   counts twice. An endpoint is pending launch until its first passing
   round: its samples are kept but count toward no uptime or incident. */

import { newId } from "../ids";
import { judgeReplication } from "../domain/replication";

import { sqlTime } from "../db/reads";
import { judge, needsBody, parseSpec, type CheckSpec } from "../domain/assertions";
import { adopt, step, type Action, type Observation, type Tracker } from "../domain/auto-incident";
import { consensus, settle, type VantageResult } from "../domain/consensus";
import { aggregate, isMonitorFault, type IncidentImpact, type TimelineState } from "../domain/status";
import type { Env } from "../env";
import { logEvent } from "../log";
import { emailSample, INBOX_API, runEmailProbe, type EmailProbeState } from "./email";
import { canarySample, isTokenRoute, parseCanaries, runCanary, runTokenProbe, type CanaryResult } from "./canary";
import type { ProbeResponse, Target } from "./protocol";
import {
  isConfigured,
  isTerraBaseConfig,
  notConfiguredSample,
  parseTerraBase,
  runTerraBase,
  type TerraBaseProbe,
} from "./terrabase";
import { VANTAGES, vantageStub, type Vantage } from "./vantages";

export const ROUND_MS = 30_000;

/** A vantage that has not answered by now is left out of the round. */
export const VANTAGE_TIMEOUT_MS = 8_000;

/** The round a moment belongs to. */
export const roundOf = (at: number): number => at - (at % ROUND_MS);

/** An endpoint as a round reads it. */
export interface RoundEndpoint {
  id: string;
  url: string;
  headers: string;
  spec: CheckSpec;
  componentId?: string;
  regionCode?: string | null;
}

export interface Sample {
  endpointId: string;
  state: string;
  ok: number;
  total: number;
  latencyMs: number | null;
  /** Each vantage's result; `ok` is null where the result was unknown, and
      `ms` is null where the time measured is not the endpoint's own. */
  results: { v: string; ok: 0 | 1 | null; ms: number | null; e: string | null }[];
  /** Values extracted by the first passing vantage. */
  values: Record<string, unknown> | null;
}

const keyOf = (endpoint: Pick<RoundEndpoint, "url" | "headers">): string => `${endpoint.url}\n${endpoint.headers}`;

const REPLICATION_URL = "https://api.terrascale.tech/health/replication";

/** The first-party replication report describes peers, not regional origins. */
export function judgeEndpoint(endpoint: RoundEndpoint, observation: Parameters<typeof judge>[1]) {
  const result = judge(endpoint.spec, observation);
  if (endpoint.url !== REPLICATION_URL || result.outcome !== "ok") return result;
  const replication = judgeReplication(observation, endpoint.regionCode);
  return { ...replication, values: { ...result.values, ...replication.values } };
}

/** Targets to probe: endpoints sharing a URL and headers are probed once,
    with the body when any of them needs it. */
export function targets(endpoints: readonly RoundEndpoint[]): Target[] {
  const unique = new Map<string, Target>();
  for (const endpoint of endpoints) {
    const key = keyOf(endpoint);
    const body = (unique.get(key)?.body ?? false) || needsBody(endpoint.spec) || endpoint.url === REPLICATION_URL;
    unique.set(key, { key, url: endpoint.url, headers: endpoint.headers, body });
  }
  return [...unique.values()];
}

/** Judges every vantage's observation of every endpoint and turns them into
    samples, updating each endpoint's down streak. A vantage that did not
    answer, or whose result is unknown, is left out, never counts as down. */
export function evaluate(
  endpoints: readonly RoundEndpoint[],
  responses: readonly (ProbeResponse | null)[],
  streaks: Readonly<Record<string, number>>,
): { samples: Sample[]; streaks: Record<string, number> } {
  const answered = responses.filter((response): response is ProbeResponse => response !== null);
  const next: Record<string, number> = {};
  const samples = endpoints.map((endpoint): Sample => {
    const key = keyOf(endpoint);
    // An indirect source's response time is the source's, not the endpoint's.
    const latencyOf = (observation: { latencyMs: number }) => (endpoint.spec.indirect ? null : observation.latencyMs);
    const judged = answered.flatMap((response) => {
      const observation = response.results.find((entry) => entry.key === key);
      return observation
        ? [{ vantage: response.vantage, observation, judgement: judgeEndpoint(endpoint, observation) }]
        : [];
    });
    const known: VantageResult[] = judged
      .filter((entry) => entry.judgement.outcome !== "unknown")
      .map((entry) => ({
        vantage: entry.vantage,
        ok: entry.judgement.outcome === "ok",
        latencyMs: latencyOf(entry.observation),
        error: entry.judgement.error,
      }));
    const round = consensus(known);
    const settled = settle(round.state, streaks[endpoint.id] ?? 0);
    if (settled.streak > 0) next[endpoint.id] = settled.streak;
    const values = judged.find((entry) => entry.judgement.outcome === "ok")?.judgement.values;
    return {
      endpointId: endpoint.id,
      state: settled.state,
      ok: round.ok,
      total: round.total,
      latencyMs: round.latencyMs,
      results: judged.map((entry) => ({
        v: entry.vantage,
        ok: entry.judgement.outcome === "unknown" ? null : entry.judgement.outcome === "ok" ? 1 : 0,
        ms: latencyOf(entry.observation),
        e: entry.judgement.error,
      })),
      values: values && Object.keys(values).length > 0 ? values : null,
    };
  });
  return { samples, streaks: next };
}

/** Asks one vantage to probe the targets; null when it fails to answer. */
async function ask(env: Env, vantage: Vantage, list: readonly Target[]): Promise<ProbeResponse | null> {
  // A cleared timer, unlike AbortSignal.timeout, never keeps the alarm
  // running once every vantage has answered.
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), VANTAGE_TIMEOUT_MS);
  try {
    const response = await vantageStub(env, vantage).fetch(`https://vantage/${vantage.code}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ targets: list }),
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return (await response.json()) as ProbeResponse;
  } catch (error) {
    logEvent("error", "vantage did not answer", { component: "round", vantage: vantage.code, error });
    return null;
  } finally {
    clearTimeout(timer);
  }
}

/** Checks HTTP endpoints from every vantage right away and stores nothing:
    the admin page's "check now". */
export async function checkNow(env: Env, endpoints: readonly RoundEndpoint[]): Promise<Sample[]> {
  if (endpoints.length === 0) return [];
  const list = targets(endpoints);
  const responses = await Promise.all(VANTAGES.map((vantage) => ask(env, vantage, list)));
  return evaluate(endpoints, responses, {}).samples;
}

const ENDPOINTS = `SELECT e.id, e.url, e.headers, e.expect_status AS expectStatus, e.blocked_status AS blockedStatus,
    e.keyword, e.assertions, e.extract, e.indirect, e.component_id AS componentId, e.kind, e.probe,
    e.region_code AS regionCode, e.live_since AS liveSince
  FROM endpoints e JOIN components c ON c.id = e.component_id WHERE c.lifecycle = 'active'`;

const COMPONENTS = "SELECT id, name FROM components WHERE lifecycle = 'active'";

// Maintenance in progress at ?1, with the components it covers (null: all).
const MAINTENANCE = `SELECT ic.component_id AS componentId FROM incidents i
  LEFT JOIN incident_components ic ON ic.incident_id = i.id
  WHERE i.kind = 'maintenance' AND i.status != 'completed' AND i.scheduled_start <= ?1
    AND coalesce(i.resolved_at, i.scheduled_end) > ?1`;

const OPEN_AUTO = `SELECT id, auto_key AS componentId, impact, operator_touched AS touched,
    strftime('%Y-%m-%dT%H:%M:%SZ', created_at) AS createdAt
  FROM incidents WHERE source = 'auto' AND status NOT IN ('resolved', 'completed')`;

const OPEN_INCIDENT = `INSERT INTO incidents (id, kind, title, status, impact, source, auto_key, created_at, updated_at)
  VALUES (?1, 'incident', ?2, 'investigating', ?3, 'auto', ?4, ?5, ?5)`;
const AFFECTS = "INSERT OR IGNORE INTO incident_components (incident_id, component_id) VALUES (?1, ?2)";
const UPDATE_INCIDENT = `UPDATE incidents SET status = ?2, impact = coalesce(?3, impact), updated_at = ?4
  WHERE id = ?1 AND operator_touched = 0`;
const RESOLVE_INCIDENT = `UPDATE incidents SET status = 'resolved', resolved_at = ?2, updated_at = ?2, auto_key = NULL
  WHERE id = ?1 AND operator_touched = 0`;
const POST_UPDATE = `INSERT INTO incident_updates (id, incident_id, status, body, created_at)
  SELECT ?1, ?2, ?3, ?4, ?5 WHERE EXISTS (SELECT 1 FROM incidents WHERE id = ?2 AND operator_touched = 0)`;

const INSERT_SAMPLE = `INSERT OR IGNORE INTO probe_samples (endpoint_id, at, state, ok, total, latency_ms, results, "values")
  VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`;

const LAUNCHED = "UPDATE endpoints SET live_since = ?2 WHERE id = ?1 AND live_since IS NULL";

const DEPLOYED = "INSERT OR IGNORE INTO deploys (endpoint_id, version, detected_at) VALUES (?1, ?2, ?3)";

const SEEN = `INSERT INTO vantages (code, colo, seen_at) VALUES (?1, ?2, ?3)
  ON CONFLICT (code) DO UPDATE SET colo = excluded.colo, seen_at = excluded.seen_at`;

/** Each active component's state this round, for automatic incidents. */
export function observe(
  endpoints: readonly RoundEndpoint[],
  samples: readonly Sample[],
  components: readonly { id: string; name: string }[],
  maintenance: readonly (string | null)[],
): Observation[] {
  const byId = new Map(samples.map((sample) => [sample.endpointId, sample]));
  return components.flatMap((component) => {
    const own = endpoints.flatMap((endpoint) =>
      endpoint.componentId === component.id ? (byId.get(endpoint.id) ?? []) : [],
    );
    if (own.length === 0) return [];
    const failing = own.reduce((sum, sample) => sum + sample.total - sample.ok, 0);
    const total = own.reduce((sum, sample) => sum + sample.total, 0);
    const errors = [
      ...new Set(
        own.flatMap((sample) => sample.results.flatMap((result) => (result.ok === 0 && result.e ? [result.e] : []))),
      ),
    ];
    const state = aggregate(own.map((sample) => sample.state as TimelineState));
    const monitorFault =
      own.flatMap((sample) => sample.results.flatMap((result) => (isMonitorFault(result.e) ? [result.e] : [])))[0] ??
      null;
    return [
      {
        componentId: component.id,
        name: component.name,
        // Healthy HTTP siblings cannot prove recovery while the canary is unmeasured.
        state: state === "operational" && monitorFault ? "none" : state,
        monitorFault,
        detail: `${failing} of ${total} checks failing${errors.length > 0 ? ` (${errors.slice(0, 3).join(", ")})` : ""}`,
        suppressed: maintenance.includes(null) || maintenance.includes(component.id),
      },
    ];
  });
}

/** The statements carrying out automatic incident actions at `time`. */
export function actionStatements(db: D1Database, actions: readonly Action[], time: string): D1PreparedStatement[] {
  return actions.flatMap((action) => {
    const update = (status: string, body: string) =>
      db.prepare(POST_UPDATE).bind(newId(), action.incidentId, status, body, time);
    switch (action.type) {
      case "open":
        return [
          db.prepare(OPEN_INCIDENT).bind(action.incidentId, action.title, action.impact, action.componentId, time),
          db.prepare(AFFECTS).bind(action.incidentId, action.componentId),
          update("investigating", action.body),
        ];
      case "update":
        return [
          db.prepare(UPDATE_INCIDENT).bind(action.incidentId, action.status, action.impact, time),
          update(action.status, action.body),
        ];
      case "resolve":
        return [db.prepare(RESOLVE_INCIDENT).bind(action.incidentId, time), update("resolved", action.body)];
    }
  });
}

/** What a round remembers for the next one. */
export interface RoundState {
  /** Consecutive down rounds per endpoint. */
  streaks: Record<string, number>;
  /** Where every answering vantage last ran. */
  colos: Record<string, string | null>;
  /** The last version seen per endpoint. */
  versions: Record<string, string>;
  /** The email delivery probe's inbox and message on its way. */
  email?: EmailProbeState;
  /** Automatic incident trackers per component. */
  trackers: Record<string, Tracker>;
}

type EndpointRow = Omit<RoundEndpoint, "spec"> &
  Parameters<typeof parseSpec>[0] & { kind: "http" | "canary"; probe: TerraBaseProbe | null; liveSince: string | null };

/** Whether most of a sample's checks passed: the round that launches an endpoint. */
export const passed = (sample: Pick<Sample, "ok" | "total">): boolean => sample.ok * 2 > sample.total;

interface OpenAuto {
  id: string;
  componentId: string;
  impact: IncidentImpact;
  touched: number;
  createdAt: string;
}

/** Runs the round starting at `at` and stores its samples. Each answering
    vantage's location is written every round, and a version that differs
    from the last one seen is recorded as a deploy. */
export async function runRound(env: Env, at: number, previous: RoundState): Promise<RoundState> {
  const time = sqlTime(at);
  const [rowsResult, componentsResult, maintenanceResult, openResult] = await env.STATUS_DB.batch([
    env.STATUS_DB.prepare(ENDPOINTS),
    env.STATUS_DB.prepare(COMPONENTS),
    env.STATUS_DB.prepare(MAINTENANCE).bind(time),
    env.STATUS_DB.prepare(OPEN_AUTO),
  ]);
  const rows = (rowsResult?.results ?? []) as EndpointRow[];
  if (rows.length === 0) return { ...previous, streaks: {} };
  const toEndpoint = (row: EndpointRow): RoundEndpoint => ({
    id: row.id,
    url: row.url,
    headers: row.headers,
    spec: parseSpec(row),
    componentId: row.componentId,
    regionCode: row.regionCode,
  });
  const probed = rows.filter((row) => row.kind !== "canary").map(toEndpoint);
  // A canary or TerraBase probe the secret does not name is not configured;
  // a malformed TerraBase entry fails.
  const canaries = parseCanaries(env.DATA_API_CANARY);
  const terrabase = parseTerraBase(env.TERRABASE_PROBES);
  const emailRow = rows.find((row) => row.kind === "canary" && row.url === INBOX_API);
  const canaryRows = rows.filter((row) => row.kind === "canary" && row !== emailRow);
  const endpoints = [...probed, ...canaryRows.map(toEndpoint), ...(emailRow ? [toEndpoint(emailRow)] : [])];

  const list = targets(probed);
  // Fresh per attempt: a replayed round must not pass on an earlier write.
  const marker = `ts-status canary ${new Date(at).toISOString()} ${newId()}`;
  const [responses, canaryResults, emailResult] = await Promise.all([
    probed.length === 0 ? [] : Promise.all(VANTAGES.map((vantage) => ask(env, vantage, list))),
    Promise.all(
      canaryRows.map(async (row): Promise<CanaryResult | null> => {
        const failed = (error: string): CanaryResult => ({ ok: false, latencyMs: null, error });
        try {
          if (row.probe) {
            if (terrabase === null) return failed("Invalid probe config");
            const config = terrabase[row.id];
            if (!isConfigured(row.probe, config)) return null;
            if (!isTerraBaseConfig(row.probe, config)) return failed("Invalid probe config");
            return await runTerraBase(row.probe, config, marker);
          }
          // The token probe exchanges the Data API canary's key: no credential of its own.
          if (isTokenRoute(row.url)) {
            const config = Object.values(canaries)[0];
            return config ? await runTokenProbe(config) : null;
          }
          const config = canaries[row.id];
          return config ? await runCanary(config, marker) : null;
        } catch (error) {
          logEvent("error", "canary failed", { component: "round", id: row.id, error });
          return failed("Canary error");
        }
      }),
    ),
    emailRow
      ? runEmailProbe(previous.email ?? {}, env.EMAIL, at).catch((error: unknown) => {
          logEvent("error", "email probe failed", { component: "round", error });
          return null;
        })
      : null,
  ]);
  const evaluated = evaluate(probed, responses, previous.streaks);
  canaryRows.forEach((row, index) => {
    const result = canaryResults[index];
    if (result === undefined) return;
    if (result === null) {
      evaluated.samples.push(notConfiguredSample(row.id));
      return;
    }
    const { sample, streak } = canarySample(row.id, result, previous.streaks[row.id] ?? 0);
    evaluated.samples.push(sample);
    if (streak > 0) evaluated.streaks[row.id] = streak;
  });
  if (emailRow && emailResult) {
    const { sample, streak } = emailSample(
      emailRow.id,
      emailResult.state,
      emailResult.fault,
      at,
      previous.streaks[emailRow.id] ?? 0,
    );
    evaluated.samples.push(sample);
    if (streak > 0) evaluated.streaks[emailRow.id] = streak;
  }

  const statements = evaluated.samples.map((sample) =>
    env.STATUS_DB.prepare(INSERT_SAMPLE).bind(
      sample.endpointId,
      time,
      sample.state,
      sample.ok,
      sample.total,
      sample.latencyMs,
      JSON.stringify(sample.results),
      sample.values && JSON.stringify(sample.values),
    ),
  );
  const pending = new Set(rows.flatMap((row) => (row.liveSince === null ? [row.id] : [])));
  for (const sample of evaluated.samples) {
    if (!pending.has(sample.endpointId) || !passed(sample)) continue;
    pending.delete(sample.endpointId);
    statements.push(env.STATUS_DB.prepare(LAUNCHED).bind(sample.endpointId, time));
  }
  const versions = { ...previous.versions };
  for (const sample of evaluated.samples) {
    const version = sample.values?.version;
    if (typeof version !== "string" && typeof version !== "number") continue;
    const text = String(version).slice(0, 100);
    if (sample.endpointId in versions && versions[sample.endpointId] !== text) {
      statements.push(env.STATUS_DB.prepare(DEPLOYED).bind(sample.endpointId, text, time));
    }
    versions[sample.endpointId] = text;
  }
  // Every answer is recorded, so the page can tell which vantages answered
  // the latest round and where they proved they ran.
  const seen = { ...previous.colos };
  for (const response of responses) {
    if (!response) continue;
    seen[response.vantage] = response.colo;
    statements.push(env.STATUS_DB.prepare(SEEN).bind(response.vantage, response.colo, time));
  }
  await env.STATUS_DB.batch(statements);

  // Incidents go in their own batch, so a failure there never loses samples.
  const open = (openResult?.results ?? []) as OpenAuto[];
  const stepped = step(
    adopt(
      previous.trackers,
      open.map((incident) => ({ ...incident, createdAt: Date.parse(incident.createdAt) })),
    ),
    observe(
      endpoints.filter(
        (endpoint) =>
          !pending.has(endpoint.id) ||
          evaluated.samples.some(
            (sample) => sample.endpointId === endpoint.id && sample.results.some((result) => isMonitorFault(result.e)),
          ),
      ),
      evaluated.samples,
      (componentsResult?.results ?? []) as { id: string; name: string }[],
      ((maintenanceResult?.results ?? []) as { componentId: string | null }[]).map((row) => row.componentId),
    ),
    at,
    {
      newId,
      touched: new Set(open.filter((row) => row.touched === 1).map((row) => row.id)),
    },
  );
  if (stepped.actions.length > 0) await env.STATUS_DB.batch(actionStatements(env.STATUS_DB, stepped.actions, time));
  return {
    streaks: evaluated.streaks,
    colos: seen,
    versions,
    trackers: stepped.trackers,
    ...(emailResult ? { email: emailResult.state } : previous.email ? { email: previous.email } : {}),
  };
}
