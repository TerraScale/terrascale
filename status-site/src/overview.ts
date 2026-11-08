/* The status overview shared by the page and the JSON API: the headline,
   bars per component and cluster over the chosen range, response times,
   and the daily platform history, all measured by the probe rounds. */

import type { Certificate, Snapshot, SnapshotWindow } from "./db/reads";
import { coverage, type Coverage } from "./domain/coverage";
import { EXPIRY_WARNING_MS } from "./probe/tls";
import { DAY, HOUR, startOfDay, startOfHour } from "./format";
import { byHour, RANGES, rangeStart, type RangeKey } from "./domain/range";
import { median } from "./domain/consensus";
import {
  aggregate,
  COMING_SOON,
  componentState,
  countsOf,
  endpointStatus,
  incidentWindows,
  isActive,
  isClosed,
  isMonitorFault,
  maintenancePhase,
  meanLatency,
  NOT_CONFIGURED,
  overallStatus,
  PENDING_LAUNCH,
  PROBE_STALE_MS,
  probeState,
  probeUptime,
  spans,
  sumCounts,
  typicalLatency,
  worst,
  worstDuring,
  type Bucket,
  type Component,
  type ComponentGroup,
  type Endpoint,
  type Incident,
  type LatestSample,
  type ProbeCounts,
  type ProbePeriod,
  type Provider,
  type Region,
  type Status,
  type TimelineState,
} from "./domain/status";
import { VANTAGES } from "./probe/vantages";

/** Days in the platform history. */
export const PLATFORM_DAYS = 90;
export const RESOLVED_DAYS = 7;

export interface ComponentOverview extends Component {
  status: TimelineState;
  /** Every measured endpoint serving the component, regional or global. */
  clusters: ClusterOverview[];
  /** Each vantage's latest median latency to the component's clusters. */
  vantagePings: Record<string, number | null>;
  /** Shown in place of the status label, e.g. "Not configured". */
  note: string | null;
  bars: Bucket[];
  /** Median latency per bar. */
  latency: (number | null)[];
  uptime: number | null;
}

export interface GroupOverview extends ComponentGroup {
  components: ComponentOverview[];
}

export interface ClusterOverview {
  id: string;
  componentId: string;
  componentName: string;
  regionCode: string | null;
  cluster: string;
  provider: Provider;
  status: TimelineState;
  /** Shown in place of the status label, e.g. "Not configured". */
  note: string | null;
  /** Median latency of the latest round. */
  pingMs: number | null;
  meanLatency: number | null;
  uptime: number | null;
  bars: Bucket[];
  latency: (number | null)[];
  /** The host's latest logged TLS certificate. */
  certificate: Certificate | null;
}

export interface RegionOverview extends Region {
  status: TimelineState;
  /** Mean latest ping of the region's reachable clusters. */
  pingMs: number | null;
  /** Each vantage's latest median latency to the region's clusters. */
  vantagePings: Record<string, number | null>;
  clusters: ClusterOverview[];
  /** Shown in place of the status label while no cluster is live yet. */
  note: string | null;
}

export interface Monitoring {
  /** When the latest probe round ran, or null before the first. */
  lastRoundAt: number | null;
  /** True when rounds have stopped arriving and states may be out of date. */
  delayed: boolean;
  /** Which vantages answered the latest round, and where they ran. */
  coverage: Coverage;
  /** Unmeasured authenticated probes, independent of HTTP vantage coverage. */
  issues: { endpointId: string; componentId: string; note: string }[];
}

/** A day of platform history, with the incidents open during it. */
export interface PlatformDay extends Bucket {
  incidents: Pick<Incident, "id" | "title" | "impact">[];
}

export interface Overview {
  generatedAt: number;
  range: RangeKey;
  status: Status;
  monitoring: Monitoring;
  groups: GroupOverview[];
  regions: RegionOverview[];
  /** The measured endpoints: those of active components. */
  endpoints: Endpoint[];
  /** Certificates of measured hosts expiring within two weeks, or expired. */
  expiring: Certificate[];
  activeIncidents: Incident[];
  resolvedIncidents: Incident[];
  /** Maintenance windows in progress or scheduled, soonest first. */
  maintenance: Incident[];
  days: PlatformDay[];
  /** Platform uptime over the range, and over the platform history. */
  uptime: number | null;
  uptimeHistory: number | null;
}

/** Where the platform history starts: midnight UTC, 89 days before today. */
export const platformStart = (now: number): number => startOfDay(now) - (PLATFORM_DAYS - 1) * DAY;

/** The data window behind an overview of `range` at `now`. */
export function snapshotWindow(now: number, range: RangeKey): SnapshotWindow {
  const from = platformStart(now);
  return {
    incidentsFrom: from,
    hoursFrom: byHour(range) ? rangeStart(range, now) : startOfHour(now),
    daysFrom: Math.min(from, rangeStart(range, now)),
  };
}

const hostOf = (url: string): string => {
  try {
    return new URL(url).hostname;
  } catch {
    return "";
  }
};

/** The note of something served by several clusters when none of them is
    measured yet: not configured when no secret names any, else pending, as
    is something with no clusters at all yet. */
function sharedNote(notes: readonly (string | null)[]): string | null {
  const fault = notes.find(isMonitorFault);
  if (fault) return fault;
  if (notes.some((note) => note === null)) return null;
  if (notes.length === 0) return PENDING_LAUNCH;
  return notes.every((note) => note === NOT_CONFIGURED) ? NOT_CONFIGURED : PENDING_LAUNCH;
}

/** Consensus counts per endpoint, keyed by the period's start. */
type Periods = Map<string, Map<number, ProbeCounts>>;

function byEndpoint(list: readonly ProbePeriod[]): Periods {
  const periods: Periods = new Map();
  for (const period of list) {
    const entries = periods.get(period.endpointId) ?? new Map<number, ProbeCounts>();
    entries.set(Date.parse(period.start), countsOf(period));
    periods.set(period.endpointId, entries);
  }
  return periods;
}

/** Reads summed counts of endpoints over whole hours or days. */
class Counter {
  private readonly hours: Periods;
  private readonly days: Periods;

  constructor(snapshot: Snapshot) {
    this.hours = byEndpoint(snapshot.hours);
    this.days = byEndpoint(snapshot.days);
  }

  /** Counts of one endpoint over [from, to), from hours or days. */
  one(endpoint: Endpoint, from: number, to: number, hourly: boolean): ProbeCounts {
    const periods = (hourly ? this.hours : this.days).get(endpoint.id);
    const size = hourly ? HOUR : DAY;
    const list: ProbeCounts[] = [];
    for (let at = from; at < to; at += size) {
      const counts = periods?.get(at);
      if (counts) list.push(counts);
    }
    return sumCounts(list);
  }

  all(endpoints: readonly Endpoint[], from: number, to: number, hourly: boolean): ProbeCounts {
    return sumCounts(endpoints.map((endpoint) => this.one(endpoint, from, to, hourly)));
  }
}

/** Bars for a set of endpoints over a range: each bar shows their combined
    state and carries their summed counts. */
function bars(counter: Counter, endpoints: readonly Endpoint[], range: RangeKey, now: number): Bucket[] {
  const { count, size } = RANGES[range];
  const start = rangeStart(range, now);
  const hourly = byHour(range);
  return spans(start, start + count * size, count).map((span) => {
    const counts = endpoints.map((endpoint) => counter.one(endpoint, span.start, span.end, hourly));
    return { ...span, state: aggregate(counts.map(probeState)), counts: sumCounts(counts) };
  });
}

/** Every component's endpoints, in catalog order. */
function endpointsByComponent(endpoints: readonly Endpoint[]): Map<string, Endpoint[]> {
  const byComponent = new Map<string, Endpoint[]>();
  for (const endpoint of endpoints) {
    byComponent.set(endpoint.componentId, [...(byComponent.get(endpoint.componentId) ?? []), endpoint]);
  }
  return byComponent;
}

const latencyOf = (list: readonly Bucket[]): (number | null)[] =>
  list.map((bucket) => (bucket.counts ? typicalLatency(bucket.counts) : null));

const uptimeOf = (list: readonly Bucket[]): number | null =>
  probeUptime(sumCounts(list.flatMap((bucket) => (bucket.counts ? [bucket.counts] : []))));

/** Each vantage's latest median latency over a set of samples. */
function vantagePings(samples: readonly LatestSample[]): Record<string, number | null> {
  return Object.fromEntries(
    VANTAGES.map((vantage) => {
      const passed = samples.flatMap((sample) =>
        sample.results.flatMap((result) =>
          result.v === vantage.code && result.ok === 1 && result.ms !== null ? [result.ms] : [],
        ),
      );
      return [vantage.code, median(passed)];
    }),
  );
}

export function overview(snapshot: Snapshot, now: number, range: RangeKey): Overview {
  const counter = new Counter(snapshot);
  const latest = new Map(snapshot.latest.map((sample): [string, LatestSample] => [sample.endpointId, sample]));
  const fresh = (endpoint: Endpoint) => {
    const sample = latest.get(endpoint.id);
    return sample && endpointStatus(sample, now) !== "none" ? sample : undefined;
  };
  const monitoringNote = (endpoint: Endpoint): string | null => {
    const sample = latest.get(endpoint.id);
    if (!sample || now - Date.parse(sample.at) > PROBE_STALE_MS) return null;
    return sample.results.find((result) => isMonitorFault(result.e) || result.e === NOT_CONFIGURED)?.e ?? null;
  };
  const components = new Map(snapshot.components.map((component) => [component.id, component]));
  // Not live yet: its failures are expected, so it shows no state at all.
  const pending = (endpoint: Endpoint): boolean => endpoint.liveSince === null;
  const status = (endpoint: Endpoint) => (pending(endpoint) ? "none" : endpointStatus(latest.get(endpoint.id), now));
  const endpointsOf = endpointsByComponent(snapshot.endpoints);
  const windows = incidentWindows(snapshot.incidents, now);
  // Every incident paired once with the window it was open.
  const opened = snapshot.incidents.flatMap((incident, index) => {
    const window = windows[index];
    return window ? [{ incident, window }] : [];
  });
  // A component's bars show incidents naming it, and maintenance windows
  // covering it in place of what was measured during them.
  const withIncidents = (list: Bucket[], componentId: string): Bucket[] => {
    const own = opened.filter(({ incident }) => incident.components.includes(componentId)).map(({ window }) => window);
    return list.map((bucket) => {
      const during = worstDuring(own, bucket.start, bucket.end);
      if (during === "none") return bucket;
      const maintenance = own.some(
        (window) =>
          window.state === "maintenance" &&
          window.to > window.from &&
          window.from < bucket.end &&
          window.to > bucket.start,
      );
      const outage = worstDuring(
        own.filter((window) => window.state !== "maintenance"),
        bucket.start,
        bucket.end,
      );
      return { ...bucket, state: maintenance ? worst("maintenance", outage) : worst(bucket.state, during) };
    });
  };

  const certificates = new Map(snapshot.certificates.map((certificate) => [certificate.host, certificate]));
  const clusterOf = (endpoint: Endpoint): ClusterOverview => {
    const list = bars(counter, [endpoint], range, now);
    const total = sumCounts(list.flatMap((bucket) => (bucket.counts ? [bucket.counts] : [])));
    return {
      id: endpoint.id,
      componentId: endpoint.componentId,
      componentName: components.get(endpoint.componentId)?.name ?? endpoint.componentId,
      regionCode: endpoint.regionCode,
      cluster: endpoint.cluster,
      provider: endpoint.provider,
      status: status(endpoint),
      note: monitoringNote(endpoint) ?? (pending(endpoint) ? PENDING_LAUNCH : null),
      pingMs: pending(endpoint) ? null : (fresh(endpoint)?.latencyMs ?? null),
      meanLatency: meanLatency(total),
      uptime: uptimeOf(list),
      bars: list,
      latency: latencyOf(list),
      certificate: certificates.get(hostOf(endpoint.url)) ?? null,
    };
  };
  const clusters = new Map(
    snapshot.endpoints
      .filter((endpoint) => components.get(endpoint.componentId)?.lifecycle === "active")
      .map((endpoint) => [endpoint.id, clusterOf(endpoint)]),
  );

  const componentList = snapshot.components.map((component): ComponentOverview => {
    const endpoints = component.lifecycle === "planned" ? [] : (endpointsOf.get(component.id) ?? []);
    const list = withIncidents(bars(counter, endpoints, range, now), component.id);
    const own = endpoints.flatMap((endpoint) => clusters.get(endpoint.id) ?? []);
    const measuredState = aggregate(endpoints.map(status));
    const monitorFault = own.some((cluster) => isMonitorFault(cluster.note));
    const state =
      component.lifecycle === "planned"
        ? "none"
        : componentState(
            measuredState === "operational" && monitorFault ? "none" : measuredState,
            component.id,
            snapshot.incidents,
            now,
          );
    return {
      ...component,
      status: state,
      // An incident or maintenance window outranks the note.
      note:
        component.lifecycle === "planned"
          ? COMING_SOON
          : state === "none"
            ? sharedNote(own.map((cluster) => cluster.note))
            : null,
      bars: list,
      latency: latencyOf(list),
      uptime: uptimeOf(list),
      clusters: own,
      vantagePings: vantagePings(endpoints.flatMap((endpoint) => (pending(endpoint) ? [] : (fresh(endpoint) ?? [])))),
    };
  });
  const active = componentList.filter((component) => component.lifecycle === "active");
  const measured = active.flatMap((component) => endpointsOf.get(component.id) ?? []);

  const regions = snapshot.regions.map((region): RegionOverview => {
    const endpoints = measured.filter((endpoint) => endpoint.regionCode === region.code);
    const own = endpoints.flatMap((endpoint) => clusters.get(endpoint.id) ?? []);
    const pings = own.flatMap((entry) => (entry.pingMs === null ? [] : [entry.pingMs]));
    const state = aggregate(own.map((entry) => entry.status));
    return {
      ...region,
      status: state,
      pingMs: pings.length === 0 ? null : Math.round(pings.reduce((sum, ping) => sum + ping, 0) / pings.length),
      vantagePings: vantagePings(endpoints.flatMap((endpoint) => (pending(endpoint) ? [] : (fresh(endpoint) ?? [])))),
      clusters: own,
      note: state === "none" ? sharedNote(own.map((entry) => entry.note)) : null,
    };
  });

  const historyStart = platformStart(now);
  const days = spans(historyStart, historyStart + PLATFORM_DAYS * DAY, PLATFORM_DAYS).map((span): PlatformDay => {
    const perComponent = active.map((component) =>
      (endpointsOf.get(component.id) ?? []).map((endpoint) => counter.one(endpoint, span.start, span.end, false)),
    );
    const states = perComponent.map((counts) => aggregate(counts.map(probeState)));
    return {
      ...span,
      state: worst(aggregate(states), worstDuring(windows, span.start, span.end)),
      counts: sumCounts(perComponent.flat()),
      incidents: opened
        .filter(({ window }) => window.from < span.end && window.to > span.start)
        .map(({ incident: { id, title, impact } }) => ({ id, title, impact })),
    };
  });

  const rounds = snapshot.latest.map((sample) => Date.parse(sample.at));
  const lastRoundAt = rounds.length === 0 ? null : Math.max(...rounds);
  const resolvedFrom = now - RESOLVED_DAYS * DAY;
  const rangeFrom = rangeStart(range, now);
  return {
    generatedAt: now,
    range,
    status: overallStatus(
      active.map((component) => component.status),
      snapshot.incidents,
      now,
    ),
    monitoring: {
      lastRoundAt,
      delayed: measured.length > 0 && (lastRoundAt === null || now - lastRoundAt > PROBE_STALE_MS),
      coverage: coverage({ vantages: VANTAGES, seen: snapshot.vantages, lastRoundAt, now }),
      issues: measured.flatMap((endpoint) => {
        const note = monitoringNote(endpoint);
        return isMonitorFault(note) ? [{ endpointId: endpoint.id, componentId: endpoint.componentId, note }] : [];
      }),
    },
    groups: snapshot.groups
      .map((group) => ({ ...group, components: componentList.filter((component) => component.groupId === group.id) }))
      .filter((group) => group.components.length > 0),
    regions,
    endpoints: measured,
    expiring: [
      ...new Set(measured.filter((endpoint) => !pending(endpoint)).map((endpoint) => hostOf(endpoint.url))),
    ].flatMap((host) => {
      const certificate = certificates.get(host);
      return certificate?.notAfter && Date.parse(certificate.notAfter) - now < EXPIRY_WARNING_MS ? [certificate] : [];
    }),
    activeIncidents: snapshot.incidents.filter(
      (incident) => incident.kind !== "maintenance" && isActive(incident, now),
    ),
    resolvedIncidents: snapshot.incidents.filter(
      (incident) =>
        incident.kind !== "maintenance" &&
        isClosed(incident, now) &&
        Date.parse(incident.resolvedAt ?? incident.updatedAt) >= resolvedFrom,
    ),
    maintenance: snapshot.incidents
      .filter((incident) => incident.kind === "maintenance" && maintenancePhase(incident, now) !== "completed")
      .sort((a, b) => Date.parse(a.scheduledStart ?? a.createdAt) - Date.parse(b.scheduledStart ?? b.createdAt)),
    days,
    // One past now, so the half-filled period holding now is counted.
    uptime: probeUptime(counter.all(measured, rangeFrom, now + 1, byHour(range))),
    uptimeHistory: uptimeOf(days),
  };
}
