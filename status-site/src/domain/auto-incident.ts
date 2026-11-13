import { STATE_LABELS, worst, type IncidentImpact, type IncidentStatus, type TimelineState } from "./status";

type Unhealthy = "degraded" | "partial_outage" | "major_outage";

export interface Tracker {
  /** The worst state seen since the component became unhealthy. */
  state: Unhealthy;
  since: number;
  incidentId: string | null;
  impact: IncidentImpact | null;
  /** When checks started passing again, while the incident is monitoring. */
  healthySince: number | null;
  /** Last failure detail posted; absent in trackers saved before detail updates. */
  detail?: string;
}

export interface Observation {
  componentId: string;
  name: string;
  state: TimelineState;
  /** What the checks see, e.g. "3 of 4 vantage points failing: HTTP 503". */
  detail: string;
  /** A maintenance window covers the component. */
  suppressed: boolean;
}

export type Action =
  | { type: "open"; incidentId: string; componentId: string; impact: IncidentImpact; title: string; body: string }
  | { type: "update"; incidentId: string; status: IncidentStatus; impact: IncidentImpact | null; body: string }
  | { type: "resolve"; incidentId: string; body: string };

/** How long a state lasts before it opens an incident. */
export const OPEN_AFTER: Record<Unhealthy, number> = {
  major_outage: 60_000,
  partial_outage: 60_000,
  degraded: 10 * 60_000,
};

/** How long checks must pass before an incident resolves. */
export const RESOLVE_AFTER = 5 * 60_000;

export const IMPACT_OF: Record<Unhealthy, IncidentImpact> = {
  degraded: "minor",
  partial_outage: "major",
  major_outage: "critical",
};

const IMPACTS: IncidentImpact[] = ["none", "minor", "major", "critical"];

const TITLES: Record<Unhealthy, (name: string) => string> = {
  degraded: (name) => `${name} is degraded`,
  partial_outage: (name) => `${name} is partially down`,
  major_outage: (name) => `${name} is down`,
};

const isUnhealthy = (state: TimelineState): state is Unhealthy =>
  state === "degraded" || state === "partial_outage" || state === "major_outage";

/** Reconciles the trackers with the automatic incidents open in D1: it lets
    go of incidents closed or deleted outside the machine, such as by an
    operator, and tracks open ones it has no memory of, such as after its
    storage was reset. */
export function adopt(
  trackers: Readonly<Record<string, Tracker>>,
  open: readonly { id: string; componentId: string; impact: IncidentImpact; createdAt: number }[],
): Record<string, Tracker> {
  const openIds = new Set(open.map((incident) => incident.id));
  const next = Object.fromEntries(
    Object.entries(trackers).filter(([, tracker]) => !tracker.incidentId || openIds.has(tracker.incidentId)),
  );
  for (const incident of open) {
    if (next[incident.componentId]?.incidentId) continue;
    const state =
      (Object.keys(IMPACT_OF) as Unhealthy[]).find((key) => IMPACT_OF[key] === incident.impact) ?? "degraded";
    next[incident.componentId] = {
      state,
      since: incident.createdAt,
      incidentId: incident.id,
      impact: incident.impact,
      healthySince: null,
    };
  }
  return next;
}

export function step(
  trackers: Readonly<Record<string, Tracker>>,
  observations: readonly Observation[],
  now: number,
  options: { newId: () => string; touched: ReadonlySet<string> },
): { trackers: Record<string, Tracker>; actions: Action[] } {
  const next: Record<string, Tracker> = { ...trackers };
  const actions: Action[] = [];

  for (const observation of observations) {
    const id = observation.componentId;
    let tracker = next[id];
    if (tracker?.incidentId && options.touched.has(tracker.incidentId)) {
      delete next[id];
      continue;
    }
    if (observation.state === "none") {
      // An unmeasured round is not a healthy one: a recovery being
      // monitored counts its five minutes from the last of them.
      if (tracker !== undefined && tracker.healthySince !== null) next[id] = { ...tracker, healthySince: now };
      continue;
    }
    if (observation.suppressed) {
      if (!tracker?.incidentId) delete next[id];
      continue;
    }

    if (isUnhealthy(observation.state)) {
      const current: Tracker = tracker
        ? { ...tracker, state: worst(tracker.state, observation.state) as Unhealthy }
        : { state: observation.state, since: now, incidentId: null, impact: null, healthySince: null };
      const label = STATE_LABELS[observation.state].toLowerCase();
      if (!current.incidentId) {
        if (now - current.since >= OPEN_AFTER[observation.state]) {
          current.incidentId = options.newId();
          current.impact = IMPACT_OF[observation.state];
          current.state = observation.state;
          current.detail = observation.detail;
          actions.push({
            type: "open",
            incidentId: current.incidentId,
            componentId: id,
            impact: current.impact,
            title: TITLES[observation.state](observation.name),
            body: `Automated health checks detected ${label}: ${observation.detail}. We are investigating.`,
          });
        }
      } else {
        const impact = IMPACT_OF[observation.state];
        const raised = IMPACTS.indexOf(impact) > IMPACTS.indexOf(current.impact ?? "none");
        if (raised || current.healthySince !== null || current.detail !== observation.detail) {
          actions.push({
            type: "update",
            incidentId: current.incidentId,
            status: "investigating",
            impact: raised ? impact : null,
            body:
              current.healthySince !== null
                ? `Health checks are failing again: ${observation.detail}. We are investigating.`
                : raised
                  ? `The impact has grown to ${label}: ${observation.detail}.`
                  : `Health checks are still failing: ${observation.detail}. We are investigating.`,
          });
          current.detail = observation.detail;
          if (raised) current.impact = impact;
        }
        current.healthySince = null;
      }
      next[id] = current;
      continue;
    }

    // Operational or in maintenance.
    if (!tracker) continue;
    if (!tracker.incidentId) {
      delete next[id];
      continue;
    }
    if (tracker.healthySince === null) {
      next[id] = { ...tracker, healthySince: now };
      actions.push({
        type: "update",
        incidentId: tracker.incidentId,
        status: "monitoring",
        impact: null,
        body: "Health checks are passing again. We are monitoring to confirm the recovery.",
      });
    } else if (now - tracker.healthySince >= RESOLVE_AFTER) {
      delete next[id];
      actions.push({
        type: "resolve",
        incidentId: tracker.incidentId,
        body: "Health checks have passed for five minutes. This incident is resolved.",
      });
    }
  }
  return { trackers: next, actions };
}
