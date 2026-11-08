/* The JSON API, version 1: the status overview, recent incidents with their
   updates, regions, and the latency of one component over a range. */

import type { Certificate, Recent } from "../db/reads";
import { RANGES, type RangeKey } from "../domain/range";
import { updatesByIncident } from "../domain/status";
import type { Measurements } from "../measure";
import type { ComponentOverview, Overview } from "../overview";
import { VANTAGES } from "../probe/vantages";
import { byHour, rangeStart } from "../domain/range";
import { DAY, HOUR } from "../format";

/** A cluster's certificate as published: its expiry and issuer, never the
    lookup's internals. */
const publicCertificate = (certificate: Certificate | null) =>
  certificate && { notAfter: certificate.notAfter, issuer: certificate.issuer };

const iso = (at: number | null) => (at === null ? null : new Date(at).toISOString());
const states = (list: readonly { state: string }[]) => list.map((bucket) => bucket.state);

export function statusDocument(view: Overview) {
  return {
    generatedAt: iso(view.generatedAt),
    range: view.range,
    status: view.status,
    uptime: view.uptime,
    lastRoundAt: iso(view.monitoring.lastRoundAt),
    monitoringDelayed: view.monitoring.delayed,
    monitoringIssues: view.monitoring.issues,
    monitoringCoverage: (({ vantages: _vantages, ...summary }) => summary)(view.monitoring.coverage),
    groups: view.groups.map(({ components, ...group }) => ({
      ...group,
      components: components.map(({ bars, clusters: _clusters, groupId: _group, ...component }) => ({
        ...component,
        bars: states(bars),
      })),
    })),
    regions: view.regions.map(({ mapX: _x, mapY: _y, clusters, ...region }) => ({
      ...region,
      clusters: clusters.map(({ bars, certificate, ...entry }) => ({
        ...entry,
        certificate: publicCertificate(certificate),
        bars: states(bars),
      })),
    })),
    days: states(view.days),
    maintenance: view.maintenance,
    incidents: [...view.activeIncidents, ...view.resolvedIncidents],
  };
}

export function incidentsDocument(recent: Recent) {
  const updates = updatesByIncident(recent.updates);
  return {
    incidents: recent.incidents.map((incident) => ({ ...incident, updates: updates.get(incident.id) ?? [] })),
  };
}

export function regionsDocument(view: Overview) {
  return {
    generatedAt: iso(view.generatedAt),
    regions: view.regions.map(({ mapX: _x, mapY: _y, clusters, ...region }) => ({
      ...region,
      live: clusters.length > 0,
      clusters: clusters.map(({ bars: _bars, latency: _latency, certificate, ...entry }) => ({
        ...entry,
        certificate: publicCertificate(certificate),
      })),
    })),
    vantages: view.monitoring.coverage.vantages.map((entry) => ({
      code: entry.code,
      name: entry.name,
      placement: entry.requested,
      colo: entry.observed,
      lastAnswerAt: iso(entry.lastAnswerAt),
      coverage: entry.state,
    })),
  };
}

export function latencyDocument(
  component: ComponentOverview,
  measurements: Measurements,
  range: RangeKey,
  now: number,
) {
  const { count, size } = RANGES[range];
  const start = rangeStart(range, now);
  const period = byHour(range) ? HOUR : DAY;
  const starts = Array.from({ length: count }, (_, index) => start + index * size);
  const ids = component.clusters.map((cluster) => cluster.id);
  const p50 = measurements.series(ids, "all", starts, size, period, 0.5);
  const p95 = measurements.series(ids, "all", starts, size, period, 0.95);
  const vantages = Object.fromEntries(
    VANTAGES.map((vantage) => [vantage.code, measurements.series(ids, vantage.code, starts, size, period, 0.5)]),
  );
  return {
    component: component.id,
    range,
    bucketSeconds: size / 1000,
    buckets: starts.map((at, index) => ({
      start: iso(at),
      p50: p50[index] ?? null,
      p95: p95[index] ?? null,
      vantages: Object.fromEntries(VANTAGES.map((vantage) => [vantage.code, vantages[vantage.code]?.[index] ?? null])),
    })),
  };
}
