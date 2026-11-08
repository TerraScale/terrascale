/* Statuspage-compatible JSON served under /api/v1,
   so existing tools and integrations that read status pages can read this
   one. Planned components and those pending launch are left out:
   Statuspage has no such state. */

import type { Component, Incident, IncidentUpdate, Status, TimelineState } from "../domain/status";
import {
  aggregate,
  HEADLINES,
  isMonitorFault,
  maintenancePhase,
  PENDING_LAUNCH,
  updatesByIncident,
} from "../domain/status";
import type { Overview } from "../overview";

const PAGE_ID = "01M4E7GK59RY4WPKPBBKFEDF48";

// Statuspage has no state for "nothing measured", so an unmeasured component
// reads as operational; the status description says when monitoring lags.
const COMPONENT_STATUS: Record<TimelineState, string> = {
  operational: "operational",
  maintenance: "under_maintenance",
  degraded: "degraded_performance",
  partial_outage: "partial_outage",
  major_outage: "major_outage",
  none: "operational",
};

const INDICATOR: Record<Status, string> = {
  operational: "none",
  maintenance: "maintenance",
  degraded: "minor",
  partial_outage: "major",
  major_outage: "critical",
};

export interface Feed {
  view: Overview;
  incidents: Incident[];
  updates: IncidentUpdate[];
  components: Component[];
  origin: string;
}

function page(feed: Feed) {
  return {
    id: PAGE_ID,
    name: "TerraScale",
    url: feed.origin,
    time_zone: "Etc/UTC",
    updated_at: new Date(feed.view.generatedAt).toISOString(),
  };
}

function status(feed: Feed) {
  const headline = HEADLINES[feed.view.status];
  const notes = [
    ...(feed.view.monitoring.delayed ? ["monitoring delayed, states may be stale"] : []),
    ...new Set(feed.view.monitoring.issues.map((issue) => `${issue.note}; affected checks are unmeasured`)),
  ];
  return {
    indicator: INDICATOR[feed.view.status],
    description: [headline, ...notes].join("; "),
  };
}

function components(feed: Feed) {
  const list: Record<string, unknown>[] = [];
  let position = 1;
  for (const group of feed.view.groups) {
    const members = group.components.filter(
      (component) =>
        component.lifecycle === "active" && component.note !== PENDING_LAUNCH && !isMonitorFault(component.note),
    );
    if (members.length === 0) continue;
    list.push({
      id: group.id,
      name: group.name,
      status: COMPONENT_STATUS[aggregate(members.map((member) => member.status))],
      position: position++,
      description: null,
      group: true,
      group_id: null,
      showcase: false,
      only_show_if_degraded: false,
      components: members.map((member) => member.id),
    });
    for (const member of members) {
      list.push({
        id: member.id,
        name: member.name,
        status: COMPONENT_STATUS[member.status],
        position: position++,
        description: member.description,
        group: false,
        group_id: group.id,
        showcase: true,
        only_show_if_degraded: false,
      });
    }
  }
  return list;
}

/** What every incident in a document is rendered from, built once. */
interface Context {
  feed: Feed;
  names: Map<string, string>;
  updates: Map<string, IncidentUpdate[]>;
}

function context(feed: Feed): Context {
  return {
    feed,
    names: new Map(feed.components.map((component) => [component.id, component.name])),
    updates: updatesByIncident(feed.updates),
  };
}

function incident({ feed, names, updates: byIncident }: Context, entry: Incident) {
  const updates = (byIncident.get(entry.id) ?? []).map((update) => ({
    id: update.id,
    incident_id: entry.id,
    status: update.status,
    body: update.body,
    created_at: update.createdAt,
    updated_at: update.createdAt,
    display_at: update.createdAt,
  }));
  const maintenance = entry.kind === "maintenance";
  return {
    id: entry.id,
    name: entry.title,
    status: maintenance ? maintenancePhase(entry, feed.view.generatedAt) : entry.status,
    impact: maintenance ? "maintenance" : entry.impact,
    created_at: entry.createdAt,
    updated_at: entry.updatedAt,
    started_at: entry.scheduledStart ?? entry.createdAt,
    resolved_at: entry.resolvedAt,
    monitoring_at: updates.find((update) => update.status === "monitoring")?.created_at ?? null,
    scheduled_for: entry.scheduledStart,
    scheduled_until: entry.scheduledEnd,
    shortlink: `${feed.origin}/incidents/${entry.id}`,
    page_id: PAGE_ID,
    incident_updates: updates,
    components: entry.components.map((id) => ({ id, name: names.get(id) ?? id })),
  };
}

const isMaintenance = (entry: Incident) => entry.kind === "maintenance";

function incidents(feed: Feed, keep: (entry: Incident) => boolean) {
  const shared = context(feed);
  return { page: page(feed), incidents: feed.incidents.filter(keep).map((entry) => incident(shared, entry)) };
}

function maintenances(feed: Feed, keep: (entry: Incident) => boolean) {
  const shared = context(feed);
  return {
    page: page(feed),
    scheduled_maintenances: feed.incidents.filter(keep).map((entry) => incident(shared, entry)),
  };
}

/** The payload of every Statuspage-compatible /api/v1 document, by file name. */
export const statuspage = {
  "status.json": (feed: Feed) => ({ page: page(feed), status: status(feed) }),
  "components.json": (feed: Feed) => ({ page: page(feed), components: components(feed) }),
  "summary.json": (feed: Feed) => {
    const shared = context(feed);
    return {
      page: page(feed),
      status: status(feed),
      components: components(feed),
      incidents: feed.incidents
        .filter((entry) => !isMaintenance(entry) && entry.status !== "resolved")
        .map((entry) => incident(shared, entry)),
      scheduled_maintenances: feed.incidents
        .filter((entry) => isMaintenance(entry) && maintenancePhase(entry, feed.view.generatedAt) !== "completed")
        .map((entry) => incident(shared, entry)),
    };
  },
  "incidents.json": (feed: Feed) => incidents(feed, (entry) => !isMaintenance(entry)),
  "incidents/unresolved.json": (feed: Feed) =>
    incidents(feed, (entry) => !isMaintenance(entry) && entry.status !== "resolved"),
  "scheduled-maintenances.json": (feed: Feed) => maintenances(feed, isMaintenance),
  "scheduled-maintenances/upcoming.json": (feed: Feed) =>
    maintenances(
      feed,
      (entry) => isMaintenance(entry) && maintenancePhase(entry, feed.view.generatedAt) === "scheduled",
    ),
  "scheduled-maintenances/active.json": (feed: Feed) =>
    maintenances(
      feed,
      (entry) => isMaintenance(entry) && maintenancePhase(entry, feed.view.generatedAt) === "in_progress",
    ),
} as const;

export type StatuspageDocument = keyof typeof statuspage;
