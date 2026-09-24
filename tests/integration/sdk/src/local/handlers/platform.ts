import type * as Models from "../../models.js";
import { type Ulid, tryUlid } from "../../ids.js";
import {
  jsonResponse,
  matchLocalRoute,
  type LocalRequestContext,
} from "../context.js";

const DEFAULT_PROJECT = "01J00000000000000000000000" as Ulid;

export async function handleLocalPlatform(
  context: LocalRequestContext,
): Promise<Response | undefined> {
  const path = context.pathname;
  const project = matchProject(path);
  const organization = matchOrganization(path);

  if (project !== undefined) {
    const usage = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/usage");
    if (usage !== undefined) return jsonResponse(projectUsage(context, asUlid(usage.projectId)));
    const workload = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/usage/workload");
    if (workload !== undefined) return jsonResponse(projectWorkload(context, asUlid(workload.projectId), rangeValue(context.url.searchParams.get("range"), "24h")));
    const latency = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/latency");
    if (latency !== undefined) return jsonResponse(projectLatency(context, asUlid(latency.projectId)));
    const history = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/usage/history");
    if (history !== undefined) return jsonResponse(projectHistory(context, asUlid(history.projectId), rangeValue(context.url.searchParams.get("range"), "24h") as Models.UsageHistoryRange));
  }

  if (organization !== undefined) {
    const orgUsage = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/usage");
    if (orgUsage !== undefined) return jsonResponse(organizationUsage(context, asUlid(orgUsage.organizationId)));
  }

  return undefined;
}

function projectUsage(context: LocalRequestContext, projectId: Ulid): Models.UsageSummary {
  const now = context.now;
  return { projectId, periodStart: shift(now, -30), periodEnd: now, metrics: [{ name: "operations", quantity: 0, unit: "operations" }], kpis: [], resourceDistribution: [], operationBreakdown: [], databaseStorage: [], storage: { usedBytes: 0, limitBytes: 10737418240, percentUsed: 0, status: "normal" }, operationUnits: 0, includedOperationUnits: 1000000, overageOperationUnits: 0, egressBytes: 0, updatedAt: now, regionalTotals: {}, categoryTotals: {}, provisional: true, sources: [{ source: "local", available: true, provisional: true }] };
}

function projectWorkload(context: LocalRequestContext, projectId: Ulid, range: Models.UsageWorkloadRange): Models.UsageWorkload {
  return { projectId, range, periodStart: shift(context.now, -duration(range)), periodEnd: context.now, bucketSize: range === "24h" ? "1h" : "1d", points: [] };
}

function projectLatency(context: LocalRequestContext, projectId: Ulid): Models.ProjectLatency {
  return { projectId, capturedAt: context.now, referencePeriodStart: shift(context.now, -1), referencePeriodEnd: context.now, source: "geographic_estimate", interRegion: [], nearestRegion: null };
}

function projectHistory(context: LocalRequestContext, projectId: Ulid, range: Models.UsageHistoryRange): Models.ProjectUsageHistory {
  const start = shift(context.now, -duration(range));
  return { projectId, range, requestedStart: start, accessibleStart: start, end: context.now, limitedByRetention: false, hasData: false, points: [] };
}

function organizationUsage(context: LocalRequestContext, organizationId: Ulid): Models.OrganizationUsage {
  return { organizationId, periodStart: shift(context.now, -30), periodEnd: context.now, updatedAt: context.now, provisional: true, resources: [], projects: [] };
}

function matchProject(path: string): Readonly<Record<string, string>> | undefined {
  const match = /^\/api\/v1\/projects\/([^/]+)(?:\/|$)/u.exec(path);
  return match === null ? undefined : { projectId: decodeURIComponent(match[1] ?? "") };
}
function matchOrganization(path: string): Readonly<Record<string, string>> | undefined {
  const match = /^\/api\/v1\/organizations\/([^/]+)(?:\/|$)/u.exec(path);
  return match === null ? undefined : { organizationId: decodeURIComponent(match[1] ?? "") };
}
export function clearLocalPlatformState(_store: object): void {
  void _store;
}

function asUlid(value: string | undefined): Ulid { return tryUlid(value ?? "") ?? DEFAULT_PROJECT; }
function rangeValue(value: string | null, fallback: Models.UsageWorkloadRange): Models.UsageWorkloadRange { return value === "7d" || value === "30d" ? value : fallback; }
function duration(range: string): number { return range === "24h" ? 86400000 : range === "7d" ? 604800000 : range === "30d" ? 2592000000 : range === "90d" ? 7776000000 : 31536000000; }
function shift(now: string, milliseconds: number): string { return new Date(new Date(now).getTime() + milliseconds).toISOString(); }
