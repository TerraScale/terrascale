/* Status domain: states, the catalog, incidents, and the probe maths behind
   every status bar. Everything here is pure so it is unit tested. */

import { merge, parseHistogram, percentile, type Histogram } from "./histogram";

/** States, least to most severe. */
export const STATES = ["operational", "maintenance", "degraded", "partial_outage", "major_outage"] as const;

export type Status = (typeof STATES)[number];

/** A timeline state: a status, or "none" where nothing was measured. */
export type TimelineState = Status | "none";

export type Lifecycle = "active" | "planned";

export type IncidentKind = "incident" | "warning" | "maintenance";

export type IncidentStatus =
  | "investigating"
  | "identified"
  | "monitoring"
  | "resolved"
  | "scheduled"
  | "in_progress"
  | "completed";

export type IncidentImpact = "none" | "minor" | "major" | "critical";

export type Provider = "cloudflare" | "other";

export interface ComponentGroup {
  id: string;
  name: string;
}

/** Something customers use, checked by the endpoints that serve it. */
export interface Component {
  id: string;
  groupId: string;
  name: string;
  description: string;
  lifecycle: Lifecycle;
}

export interface Incident {
  id: string;
  kind: IncidentKind;
  title: string;
  status: IncidentStatus;
  impact: IncidentImpact;
  source: "manual" | "auto";
  /** The latest update's text. */
  message: string;
  /** Affected components; none means the whole platform. */
  components: string[];
  scheduledStart: string | null;
  scheduledEnd: string | null;
  createdAt: string;
  updatedAt: string;
  resolvedAt: string | null;
}

export interface IncidentUpdate {
  id: string;
  incidentId: string;
  status: IncidentStatus;
  body: string;
  createdAt: string;
}

/** Updates grouped by their incident, keeping their order. */
export function updatesByIncident(updates: readonly IncidentUpdate[]): Map<string, IncidentUpdate[]> {
  const grouped = new Map<string, IncidentUpdate[]>();
  for (const update of updates) grouped.set(update.incidentId, [...(grouped.get(update.incidentId) ?? []), update]);
  return grouped;
}

/** A span of time in one state. */
export interface Segment {
  from: number;
  to: number;
  state: TimelineState;
}

/** A status bar: the worst state seen during [start, end), with the probe
    counts behind it where it was measured. */
export interface Bucket {
  start: number;
  end: number;
  state: TimelineState;
  counts?: ProbeCounts;
}

export const STATE_LABELS: Record<TimelineState, string> = {
  operational: "Operational",
  maintenance: "Maintenance",
  degraded: "Degraded performance",
  partial_outage: "Partial outage",
  major_outage: "Major outage",
  none: "No data",
};

/** The data-state value of a state in the shared status contract, which
    names the outages "partial" and "outage". */
export const stateAttr = (state: TimelineState): string =>
  state === "partial_outage" ? "partial" : state === "major_outage" ? "outage" : state;

/** The note of an endpoint the latest round found not configured, and of a
    component whose every endpoint it found so (see src/probe/terrabase.ts). */
export const NOT_CONFIGURED = "Not configured";

/** The monitor cannot authenticate; this is no evidence of service health. */
export const CANARY_CREDENTIAL_INVALID = "Canary credential invalid";

/** The monitor's own Free-plan operation allowance is spent, so TerraBase
    refuses its writes: no evidence of service health either. */
export const CANARY_QUOTA_EXHAUSTED = "Canary quota exhausted";

/** The email probe's disposable inbox API failed: no evidence about our email. */
export const EMAIL_INBOX_UNAVAILABLE = "Probe inbox unavailable";

/** Notes of checks the monitor itself could not run. */
const MONITOR_FAULTS: ReadonlySet<string> = new Set([
  CANARY_CREDENTIAL_INVALID,
  CANARY_QUOTA_EXHAUSTED,
  EMAIL_INBOX_UNAVAILABLE,
]);

export const isMonitorFault = (note: string | null | undefined): note is string =>
  typeof note === "string" && MONITOR_FAULTS.has(note);

/** The note of an endpoint that has never passed a round, and of a component
    whose every endpoint has not: not live yet, so not an outage. */
export const PENDING_LAUNCH = "Pending launch";

/** The label of a planned component, which is not measured yet. */
export const COMING_SOON = "Coming soon";

/** The headline for each overall state. */
export const HEADLINES: Record<Status, string> = {
  operational: "All systems operational",
  maintenance: "Scheduled maintenance in progress",
  degraded: "Some systems are degraded",
  partial_outage: "Partial system outage",
  major_outage: "Major system outage",
};

/** The state an active incident implies. */
export const IMPACT_STATUS: Record<IncidentImpact, Status> = {
  none: "operational",
  minor: "degraded",
  major: "partial_outage",
  critical: "major_outage",
};

const severity = (state: TimelineState): number => (state === "none" ? -1 : STATES.indexOf(state));

/** The more severe of two states. */
export function worst(a: TimelineState, b: TimelineState): TimelineState {
  return severity(b) > severity(a) ? b : a;
}

/** The state of something served by several parts, such as a region's
    clusters or a component's endpoints: down when every part is down,
    partially down when some are, and otherwise the worst part. */
export function aggregate(states: readonly TimelineState[]): TimelineState {
  const known = states.filter((state) => state !== "none");
  if (known.length === 0) return "none";
  const down = known.filter((state) => state === "major_outage").length;
  if (down === known.length) return "major_outage";
  if (down > 0) return "partial_outage";
  return known.reduce<TimelineState>(worst, "none");
}

export type MaintenancePhase = "scheduled" | "in_progress" | "completed";

/** Where a maintenance window stands at `now`: operators may complete it
    early; otherwise its schedule decides. */
export function maintenancePhase(
  incident: Pick<Incident, "status" | "scheduledStart" | "scheduledEnd" | "resolvedAt">,
  now: number,
): MaintenancePhase {
  const end = incident.resolvedAt ?? incident.scheduledEnd;
  if (incident.status === "completed" || (end !== null && now >= Date.parse(end))) return "completed";
  if (incident.status === "in_progress" || (incident.scheduledStart && now >= Date.parse(incident.scheduledStart))) {
    return "in_progress";
  }
  return "scheduled";
}

/** Whether an incident affects the present: open incidents and warnings, and
    maintenance windows in progress. */
export function isActive(incident: Incident, now: number): boolean {
  if (incident.kind === "maintenance") return maintenancePhase(incident, now) === "in_progress";
  return incident.status !== "resolved";
}

/** Whether an incident is over: resolved, or maintenance completed. */
export function isClosed(incident: Incident, now: number): boolean {
  return incident.kind === "maintenance"
    ? maintenancePhase(incident, now) === "completed"
    : incident.status === "resolved";
}

/** The headline: the components taken together, worsened by the impact of
    every active platform-wide incident, or maintenance while a platform-wide
    window is in progress. Incidents naming components count through those
    components' states, so one component down is a partial outage of the
    platform; only all of them is a major one. */
export function overallStatus(
  components: readonly TimelineState[],
  incidents: readonly Incident[],
  now: number,
): Status {
  let state = aggregate(components);
  for (const incident of incidents) {
    if (!isActive(incident, now) || incident.components.length > 0) continue;
    state = worst(state, incident.kind === "maintenance" ? "maintenance" : IMPACT_STATUS[incident.impact]);
  }
  return state === "none" ? "operational" : state;
}

/** A component's state: maintenance while a window covering it is in
    progress (measured failures are expected then), otherwise the measured
    state worsened by the impact of active incidents naming it. */
export function componentState(
  measured: TimelineState,
  componentId: string,
  incidents: readonly Incident[],
  now: number,
): TimelineState {
  const active = incidents.filter((incident) => incident.components.includes(componentId) && isActive(incident, now));
  const impact = active
    .filter((incident) => incident.kind !== "maintenance")
    .reduce<TimelineState>((acc, incident) => worst(acc, IMPACT_STATUS[incident.impact]), "none");
  if (active.some((incident) => incident.kind === "maintenance")) return worst("maintenance", impact);
  return worst(measured, impact);
}

/** The time every incident was open, in the state its impact implies, and
    every maintenance window as maintenance. */
export function incidentWindows(incidents: readonly Incident[], now: number): Segment[] {
  return incidents.map((incident) => {
    if (incident.kind === "maintenance") {
      const from = Date.parse(incident.scheduledStart ?? incident.createdAt);
      const end = incident.resolvedAt ?? incident.scheduledEnd;
      return { from, to: end ? Math.min(Date.parse(end), Math.max(now, from)) : now, state: "maintenance" as const };
    }
    return {
      from: Date.parse(incident.createdAt),
      to: incident.resolvedAt ? Date.parse(incident.resolvedAt) : now,
      state: IMPACT_STATUS[incident.impact],
    };
  });
}

/** The worst state of any window overlapping [from, to). */
export function worstDuring(windows: readonly Segment[], from: number, to: number): TimelineState {
  return windows
    .filter((window) => window.to > window.from && window.from < to && window.to > from)
    .reduce<TimelineState>((acc, window) => worst(acc, window.state), "none");
}

/** Cuts [start, end) into `count` equal buckets. */
export function spans(start: number, end: number, count: number): { start: number; end: number }[] {
  const size = (end - start) / count;
  return Array.from({ length: count }, (_, index) => {
    const from = start + index * size;
    return { start: from, end: index === count - 1 ? end : from + size };
  });
}

/* Regions and health probes. Every cluster is an endpoint probed from
   every vantage each round; rounds roll up into hourly and daily counts. */

export interface Region {
  code: string;
  name: string;
  country: string;
  mapX: number;
  mapY: number;
  /** A URL visitors' browsers can time requests to, and what serves it. */
  pingUrl: string | null;
  pingLabel: string | null;
}

export interface Endpoint {
  id: string;
  componentId: string;
  regionCode: string | null;
  cluster: string;
  provider: Provider;
  url: string;
  /** When it first passed a round; null while it is pending launch. */
  liveSince: string | null;
}

/** One vantage's result in a sample; `ok` is null where it was unknown. */
export interface VantageSample {
  v: string;
  ok: 0 | 1 | null;
  ms: number;
  e: string | null;
}

/** An endpoint's latest probe round. */
export interface LatestSample {
  endpointId: string;
  at: string;
  state: TimelineState;
  /** Vantages that passed, out of those that answered. */
  ok: number;
  total: number;
  latencyMs: number | null;
  results: VantageSample[];
}

/** Probe counts over any stretch of time. `checks` counts judged rounds,
    `failures` the down ones; latency and its histogram cover the rest. */
export interface ProbeCounts {
  checks: number;
  failures: number;
  degraded: number;
  latencySum: number;
  hist: Histogram;
}

/** One endpoint's consensus counts for one UTC hour or day, as stored. */
export interface ProbePeriod extends Omit<ProbeCounts, "hist"> {
  endpointId: string;
  start: string;
  hist: string;
}

/** Probes older than this no longer describe the present. */
export const PROBE_STALE_MS = 2 * 60_000;

/** A healthy response slower than this counts as degraded. */
export const SLOW_MS = 1_500;

/** The current state of one endpoint from its latest round. */
export function endpointStatus(latest: LatestSample | undefined, now: number): TimelineState {
  if (!latest || now - Date.parse(latest.at) > PROBE_STALE_MS) return "none";
  return latest.state;
}

/** The sum of several probe counts. */
export function sumCounts(list: readonly ProbeCounts[]): ProbeCounts {
  return {
    checks: list.reduce((sum, entry) => sum + entry.checks, 0),
    failures: list.reduce((sum, entry) => sum + entry.failures, 0),
    degraded: list.reduce((sum, entry) => sum + entry.degraded, 0),
    latencySum: list.reduce((sum, entry) => sum + entry.latencySum, 0),
    hist: merge(list.map((entry) => entry.hist)),
  };
}

/** Stored counts with their histogram parsed. */
export const countsOf = (period: ProbePeriod): ProbeCounts => ({ ...period, hist: parseHistogram(period.hist) });

/** The median latency: from the histogram, or the mean where rollups
    predate histograms. */
export function typicalLatency(counts: ProbeCounts): number | null {
  return percentile(counts.hist, 0.5) ?? meanLatency(counts);
}

/** Fewer rounds than this cannot make a stretch of time an outage. */
export const MIN_OUTAGE_ROUNDS = 3;

/** The state of a stretch of time: down or partially down by its share of
    failed rounds, degraded by any failure or a tenth of degraded rounds. */
export function probeState(counts: Pick<ProbeCounts, "checks" | "failures" | "degraded"> | undefined): TimelineState {
  if (!counts || counts.checks === 0) return "none";
  // Too few rounds to call an outage, like a single failed round early in an hour.
  if (counts.failures > 0 && counts.checks < MIN_OUTAGE_ROUNDS) return "degraded";
  if (counts.failures === counts.checks) return "major_outage";
  if (counts.failures / counts.checks >= 0.1) return "partial_outage";
  if (counts.failures > 0 || counts.degraded * 10 >= counts.checks) return "degraded";
  return "operational";
}

/** Share of rounds that were not down, or null without rounds. */
export function probeUptime(counts: Pick<ProbeCounts, "checks" | "failures">): number | null {
  return counts.checks === 0 ? null : 1 - counts.failures / counts.checks;
}

/** Mean latency of the rounds that were not down, or null without any. */
export function meanLatency(counts: Pick<ProbeCounts, "checks" | "failures" | "latencySum">): number | null {
  const ok = counts.checks - counts.failures;
  return ok === 0 ? null : Math.round(counts.latencySum / ok);
}
