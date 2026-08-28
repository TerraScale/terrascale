import type * as Models from "../../models.js";
import { type Ulid, tryUlid } from "../../ids.js";
import {
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalRequestContext,
} from "../context.js";

/*
 * The local host deliberately uses the same maps as the workspace repository.
 * Keeping this adapter map-oriented is useful here: PersistentMap (the SQLite
 * implementation) and Map (the in-memory test implementation) have the same
 * observable contract, and mutations consequently get persisted by the store.
 */
type AnyMap<T = unknown> = Map<string, T>;
type StoredBillingAccess = Models.BillingAccessMember & { readonly organizationId: Ulid };

const DEFAULT_PROJECT = "01J00000000000000000000000" as Ulid;

export async function handleLocalPlatform(
  context: LocalRequestContext,
): Promise<Response | undefined> {
  // The SQLite store is the source of truth. Never retain a process-level
  // snapshot: reset(), another handler, or another request may have changed it.
  clearLocalPlatformState(context.store);
  await Promise.all(["api-keys", "m2m-applications", "invoices", "billing-access", "billing-accounts", "pricing-plans"].map((name) => hydrate(context, name)));
  const path = context.pathname;
  const project = matchProject(path);
  const organization = matchOrganization(path);

  if (project !== undefined) {
    const apiKeys = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/auth/api-keys");
    if (apiKeys !== undefined) return jsonResponse({ apiKeys: values(projectMap(context, "api-keys", apiKeys.projectId ?? ""), apiKeys.projectId) });

    const createApiKey = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/auth/api-keys");
    if (createApiKey !== undefined) {
      const body = objectBody(context);
      const keyId = (await context.store.nextUlid()) as Ulid;
      const secret = deterministicSecret("key", keyId);
      const key: Models.ApiKey = {
        keyId,
        projectId: asUlid(createApiKey.projectId),
        name: stringValue(body.name, "API key"),
        prefix: secret.slice(0, 12),
        lastFour: secret.slice(-4),
        scopes: stringArray(body.scopes),
        databaseId: asOptionalUlid(body.databaseId) ?? null,
        environmentId: asOptionalUlid(body.environmentId) ?? null,
        createdByEmail: "developer@terrascale.local",
        createdAt: context.now,
        expiresAt: typeof body.expiresAt === "string" ? body.expiresAt : null,
        isActive: true,
      };
      projectMap(context, "api-keys", createApiKey.projectId ?? "").set(keyId, key);
      await persist(context, "api-keys");
      return jsonResponse({ apiKey: key, secret });
    }

    const rotateKey = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/auth/api-keys/{apiKeyId}/rotate");
    if (rotateKey !== undefined) {
      const map = projectMap(context, "api-keys", rotateKey.projectId ?? "");
      const current = map.get(rotateKey.apiKeyId ?? "") as Models.ApiKey | undefined;
      if (current === undefined || current.projectId !== asUlid(rotateKey.projectId)) return problemResponse(404, "not_found", "The API key was not found.");
      const secret = deterministicSecret("key", `${rotateKey.apiKeyId}:${context.now}`);
      const key = { ...current, prefix: secret.slice(0, 12), lastFour: secret.slice(-4), revokedAt: null, isActive: true } satisfies Models.ApiKey;
      map.set(rotateKey.apiKeyId ?? "", key);
      await persist(context, "api-keys");
      return jsonResponse({ apiKey: key, secret, rotated: true });
    }

    const revokeKey = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/auth/api-keys/{apiKeyId}");
    if (revokeKey !== undefined) {
      const map = projectMap(context, "api-keys", revokeKey.projectId ?? "");
      const current = map.get(revokeKey.apiKeyId ?? "") as Models.ApiKey | undefined;
      if (current === undefined || current.projectId !== asUlid(revokeKey.projectId)) return problemResponse(404, "not_found", "The API key was not found.");
      map.set(revokeKey.apiKeyId ?? "", { ...current, revokedAt: context.now, isActive: false });
      await persist(context, "api-keys");
      return jsonResponse({ keyId: asUlid(revokeKey.apiKeyId), revoked: true });
    }

    const m2mList = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/auth/m2m-applications");
    if (m2mList !== undefined) return jsonResponse({ applications: values(projectMap(context, "m2m-applications", m2mList.projectId ?? ""), m2mList.projectId) });
    const m2mCreate = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/auth/m2m-applications");
    if (m2mCreate !== undefined) {
      const body = objectBody(context);
      const clientId = (await context.store.nextUlid()) as Ulid;
      const clientSecret = deterministicSecret("client", clientId);
      const app: Models.M2MApplication = {
        clientId,
        projectId: asUlid(m2mCreate.projectId),
        name: stringValue(body.name, "Local application"),
        prefix: clientSecret.slice(0, 12),
        lastFour: clientSecret.slice(-4),
        scopes: stringArray(body.scopes),
        databaseId: asOptionalUlid(body.databaseId) ?? null,
        environmentId: asOptionalUlid(body.environmentId) ?? null,
        createdByEmail: "developer@terrascale.local",
        createdAt: context.now,
        expiresAt: typeof body.expiresAt === "string" ? body.expiresAt : null,
        isActive: true,
      };
      projectMap(context, "m2m-applications", m2mCreate.projectId ?? "").set(clientId, { ...app, clientSecret });
      await persist(context, "m2m-applications");
      return jsonResponse({ application: app, clientSecret });
    }
    const m2mRotate = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/auth/m2m-applications/{clientId}/rotate");
    if (m2mRotate !== undefined) {
      const map = projectMap(context, "m2m-applications", m2mRotate.projectId ?? "");
      const current = map.get(m2mRotate.clientId ?? "") as (Models.M2MApplication & { clientSecret?: string }) | undefined;
      if (current === undefined || current.projectId !== asUlid(m2mRotate.projectId)) return problemResponse(404, "not_found", "The M2M application was not found.");
      const clientSecret = deterministicSecret("client", `${m2mRotate.clientId}:${context.now}`);
      const app = { ...current, prefix: clientSecret.slice(0, 12), lastFour: clientSecret.slice(-4), revokedAt: null, isActive: true, clientSecret };
      map.set(m2mRotate.clientId ?? "", app);
      await persist(context, "m2m-applications");
      return jsonResponse({ application: withoutSecret(app), clientSecret, rotated: true });
    }
    const m2mRevoke = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/auth/m2m-applications/{clientId}");
    if (m2mRevoke !== undefined) {
      const map = projectMap(context, "m2m-applications", m2mRevoke.projectId ?? "");
      const current = map.get(m2mRevoke.clientId ?? "") as Models.M2MApplication | undefined;
      if (current === undefined || current.projectId !== asUlid(m2mRevoke.projectId)) return problemResponse(404, "not_found", "The M2M application was not found.");
      map.set(m2mRevoke.clientId ?? "", { ...current, revokedAt: context.now, isActive: false });
      await persist(context, "m2m-applications");
      return jsonResponse({ clientId: asUlid(m2mRevoke.clientId), revoked: true });
    }
    const token = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/auth/m2m/token");
    if (token !== undefined) {
      const body = objectBody(context);
      const app = [...projectMap(context, "m2m-applications", token.projectId ?? "").values()].find((v) => {
        const candidate = v as Models.M2MApplication & { clientSecret?: string };
        return candidate.clientId === body.client_id && candidate.clientSecret === body.client_secret && candidate.isActive;
      }) as (Models.M2MApplication & { clientSecret?: string }) | undefined;
      if (app === undefined) return problemResponse(401, "invalid_client", "The client credentials are invalid.");
      return jsonResponse({ access_token: deterministicSecret("token", `${app.clientId}:${context.now}`), token_type: "Bearer", expires_in: 3600, scope: typeof body.scope === "string" ? body.scope : app.scopes.join(" "), project_id: app.projectId, client_id: app.clientId, ...(app.databaseId === undefined ? {} : { database_id: app.databaseId }), ...(app.environmentId === undefined ? {} : { environment_id: app.environmentId }) });
    }

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
    const account = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/billing");
    if (account !== undefined) return jsonResponse(billingAccount(context, asUlid(account.organizationId)));
    const invoices = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/billing/invoices");
    if (invoices !== undefined) return jsonResponse(localPage(values(map(context, "invoices"), invoices.organizationId)));
    const access = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/billing/access");
    if (access !== undefined) return jsonResponse(values(map(context, "billing-access"), access.organizationId).map(stripBillingAccess));
    const setAccess = matchLocalRoute(context, "PUT", "/api/v1/organizations/{organizationId}/billing/access/{userId}");
    if (setAccess !== undefined) {
      const body = objectBody(context);
      const member: StoredBillingAccess = { organizationId: asUlid(setAccess.organizationId), userId: asUlid(setAccess.userId), email: "developer@terrascale.local", displayName: "Developer", role: "billing_manager", enabled: body.enabled !== false, grantedAt: context.now, grantedByUserId: asUlid(setAccess.userId) };
      map(context, "billing-access").set(`${setAccess.organizationId}:${setAccess.userId}`, member);
      await persist(context, "billing-access");
      return jsonResponse(stripBillingAccess(member));
    }
    const checkout = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}/billing/checkout-sessions");
    if (checkout !== undefined) return jsonResponse({ sessionId: `local-checkout-${checkout.organizationId}`, url: `https://terrascale.local/billing/checkout/${checkout.organizationId}` });
    const portal = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}/billing/portal-sessions");
    if (portal !== undefined) return jsonResponse({ url: `https://terrascale.local/billing/portal/${portal.organizationId}` });
    const orgUsage = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/usage");
    if (orgUsage !== undefined) return jsonResponse(organizationUsage(context, asUlid(orgUsage.organizationId)));
  }

  if (matchLocalRoute(context, "GET", "/api/v1/pricing/plans") !== undefined) return jsonResponse(values(map(context, "pricing-plans")));
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
  return { organizationId, planId: "01J00000000000000000000002", planName: "Developer", periodStart: shift(context.now, -30), periodEnd: context.now, updatedAt: context.now, provisional: true, resources: [], projects: [] };
}

function billingAccount(context: LocalRequestContext, organizationId: Ulid): Models.BillingAccount {
  const existing = [...map(context, "billing-accounts").values()].find((value) => (value as Models.BillingAccount).organizationId === organizationId) as Models.BillingAccount | undefined;
  return existing ?? { billingAccountId: "01J00000000000000000000003" as Ulid, organizationId, planId: "01J00000000000000000000002" as Ulid, provider: "stripe", status: "trialing", billingInterval: "monthly" };
}

function matchProject(path: string): Readonly<Record<string, string>> | undefined {
  const match = /^\/api\/v1\/projects\/([^/]+)(?:\/|$)/u.exec(path);
  return match === null ? undefined : { projectId: decodeURIComponent(match[1] ?? "") };
}
function matchOrganization(path: string): Readonly<Record<string, string>> | undefined {
  const match = /^\/api\/v1\/organizations\/([^/]+)(?:\/|$)/u.exec(path);
  return match === null ? undefined : { organizationId: decodeURIComponent(match[1] ?? "") };
}
const caches = new WeakMap<object, Map<string, AnyMap>>();
const loaded = new WeakMap<object, Set<string>>();
const persistedIds = new WeakMap<object, Map<string, Set<string>>>();

export function clearLocalPlatformState(store: object): void {
  caches.delete(store);
  loaded.delete(store);
  persistedIds.delete(store);
}

function map(context: LocalRequestContext, name: string): AnyMap {
  let byName = caches.get(context.store);
  if (byName === undefined) { byName = new Map(); caches.set(context.store, byName); }
  let result = byName.get(name);
  if (result === undefined) { result = new Map(); byName.set(name, result); }
  return result;
}
async function hydrate(context: LocalRequestContext, name: string): Promise<void> {
  let set = loaded.get(context.store);
  if (set === undefined) { set = new Set(); loaded.set(context.store, set); }
  if (set.has(name)) return;
  const records = await context.store.list<unknown>(name, "local");
  for (const value of records) {
    const id = recordId(value, name);
    if (id !== undefined) map(context, name).set(id, value);
  }
  let byName = persistedIds.get(context.store);
  if (byName === undefined) { byName = new Map(); persistedIds.set(context.store, byName); }
  byName.set(name, new Set(map(context, name).keys()));
  set.add(name);
}
async function persist(context: LocalRequestContext, name: string): Promise<void> {
  const byName = persistedIds.get(context.store);
  const before = byName?.get(name) ?? new Set<string>();
  for (const id of before) if (!map(context, name).has(id)) await context.store.delete(name, "local", id);
  for (const [id, value] of map(context, name)) await context.store.put(name, "local", id, value);
  byName?.set(name, new Set(map(context, name).keys()));
}
function recordId(value: unknown, namespace?: string): string | undefined {
  if (value === null || typeof value !== "object") return undefined;
  const record = value as Record<string, unknown>;
  if (namespace === "billing-access" && typeof record.organizationId === "string" && typeof record.userId === "string") return `${record.organizationId}:${record.userId}`;
  for (const key of ["keyId", "clientId", "invoiceId", "billingAccountId", "planId", "userId", "notificationId", "auditEventId"]) if (typeof record[key] === "string") return record[key];
  return undefined;
}
function projectMap(context: LocalRequestContext, name: string, projectId: string): AnyMap {
  // Records are globally keyed by their ULID; callers filter by project when
  // listing, while lookups and writes use the global map so SQLite sees them.
  void projectId;
  return map(context, name);
}
function values(source: AnyMap, projectId?: string): readonly unknown[] {
  return [...source.values()].filter((value) => projectId === undefined || (value as { projectId?: string; organizationId?: string }).projectId === projectId || (value as { organizationId?: string }).organizationId === projectId).map(withoutSecret);
}
function withoutSecret<T>(value: T): T {
  if (value !== null && typeof value === "object" && "clientSecret" in value) {
    const { clientSecret: _secret, ...safe } = value as T & { clientSecret?: string };
    return safe as T;
  }
  return value;
}
function stripBillingAccess(value: unknown): Models.BillingAccessMember {
  const { organizationId: _organizationId, ...member } = value as StoredBillingAccess;
  return member;
}
function asUlid(value: string | undefined): Ulid { return tryUlid(value ?? "") ?? DEFAULT_PROJECT; }
function asOptionalUlid(value: unknown): Ulid | undefined { return typeof value === "string" ? tryUlid(value) : undefined; }
function stringValue(value: unknown, fallback: string): string { return typeof value === "string" && value.trim() !== "" ? value.trim() : fallback; }
function stringArray(value: unknown): readonly string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []; }
function rangeValue(value: string | null, fallback: Models.UsageWorkloadRange): Models.UsageWorkloadRange { return value === "7d" || value === "30d" ? value : fallback; }
function duration(range: string): number { return range === "24h" ? 86400000 : range === "7d" ? 604800000 : range === "30d" ? 2592000000 : range === "90d" ? 7776000000 : 31536000000; }
function shift(now: string, milliseconds: number): string { return new Date(new Date(now).getTime() + milliseconds).toISOString(); }
function deterministicSecret(kind: string, seed: string): string { return `ts_local_${kind}_${seed.replace(/[^A-Za-z0-9]/gu, "").slice(-20)}_development_secret`; }
function localPage(items: readonly unknown[]): Models.Page<unknown> { return { items, totalCount: items.length, nextCursor: null }; }
