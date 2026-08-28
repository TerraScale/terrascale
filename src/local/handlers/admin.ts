import type * as Models from "../../models.js";
import { type Ulid, tryUlid } from "../../ids.js";
import {
  emptyResponse,
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalRequestContext,
} from "../context.js";

type AnyMap<T = unknown> = Map<string, T>;
const FALLBACK_ID = "01J00000000000000000000000" as Ulid;

/** Implements the public, owner-protected AdminClient surface for local runs. */
export async function handleLocalAdmin(context: LocalRequestContext): Promise<Response | undefined> {
  // A request-local hydration makes reset() and mutations from sibling
  // handlers immediately visible and avoids stale cross-request snapshots.
  clearLocalAdminState(context.store);
  await Promise.all(["users", "organizations", "projects", "databases", "support-notes", "pricing-plans", "audit", "notifications", "billing-attempts"].map((name) => hydrate(context, name)));
  await hydrateDatabases(context);
  const overview = matchLocalRoute(context, "GET", "/api/v1/admin");
  if (overview !== undefined) return jsonResponse(adminUsage(context));

  const users = matchLocalRoute(context, "GET", "/api/v1/admin/users");
  if (users !== undefined) {
    const search = context.url.searchParams.get("search")?.toLowerCase();
    const result = values(context, "users").map((value) => adminUser(value)).filter((value) => search === null || search === undefined || `${value.email} ${value.displayName ?? ""}`.toLowerCase().includes(search));
    return jsonResponse(localPage(result));
  }
  const getUser = matchLocalRoute(context, "GET", "/api/v1/admin/users/{userId}");
  if (getUser !== undefined) {
    const value = map(context, "users").get(getUser.userId ?? "");
    return value === undefined ? problemResponse(404, "not_found", "The user was not found.") : jsonResponse(adminUser(value));
  }
  const enable = matchLocalRoute(context, "POST", "/api/v1/admin/users/{userId}:enable");
  const disable = matchLocalRoute(context, "POST", "/api/v1/admin/users/{userId}:disable");
  if (enable !== undefined || disable !== undefined) {
    const id = (enable ?? disable)?.userId ?? "";
    const current = map(context, "users").get(id);
    if (current === undefined) return problemResponse(404, "not_found", "The user was not found.");
    const updated = { ...asRecord(current), enabled: enable !== undefined };
    map(context, "users").set(id, updated);
    await persist(context, "users");
    return jsonResponse(adminUser(updated));
  }
  const deleteUser = matchLocalRoute(context, "DELETE", "/api/v1/admin/users/{userId}");
  if (deleteUser !== undefined) {
    const userId = deleteUser.userId ?? "";
    const user = map(context, "users").get(userId);
    if (user === undefined) return problemResponse(404, "not_found", "The user was not found.");
    await deleteOwnedUserState(context, userId, user);
    map(context, "users").delete(userId);
    await persist(context, "users");
    return emptyResponse();
  }

  const note = matchLocalRoute(context, "POST", "/api/v1/admin/support-notes");
  if (note !== undefined) {
    const body = objectBody(context);
    const supportNote: Models.AdminSupportNote = {
      supportNoteId: (await context.store.nextUlid()) as Ulid,
      ...(optionalField("userId", body.userId)),
      ...(optionalField("organizationId", body.organizationId)),
      ...(optionalField("projectId", body.projectId)),
      note: typeof body.note === "string" ? body.note : "",
      createdAt: context.now,
    };
    map(context, "support-notes").set(supportNote.supportNoteId, supportNote);
    await persist(context, "support-notes");
    return jsonResponse(supportNote, 201);
  }

  const organizations = matchLocalRoute(context, "GET", "/api/v1/admin/organizations");
  if (organizations !== undefined) return jsonResponse(localPage(values(context, "organizations")));
  const projects = matchLocalRoute(context, "GET", "/api/v1/admin/projects");
  if (projects !== undefined) return jsonResponse(localPage(values(context, "projects")));
  const databases = matchLocalRoute(context, "GET", "/api/v1/admin/databases");
  if (databases !== undefined) return jsonResponse(localPage(values(context, "databases")));
  const usage = matchLocalRoute(context, "GET", "/api/v1/admin/usage");
  if (usage !== undefined) return jsonResponse(adminUsage(context));

  const plans = matchLocalRoute(context, "GET", "/api/v1/admin/plans");
  if (plans !== undefined) return jsonResponse(values(context, "pricing-plans"));
  const createPlan = matchLocalRoute(context, "POST", "/api/v1/admin/plans");
  if (createPlan !== undefined) {
    const body = objectBody(context);
    const plan: Models.PricingPlan = await makePlan(context, body);
    map(context, "pricing-plans").set(plan.planId, plan);
    await persist(context, "pricing-plans");
    return jsonResponse(plan, 201);
  }
  const updatePlan = matchLocalRoute(context, "PATCH", "/api/v1/admin/plans/{planId}");
  if (updatePlan !== undefined) {
    const current = map(context, "pricing-plans").get(updatePlan.planId ?? "") as Models.PricingPlan | undefined;
    if (current === undefined) return problemResponse(404, "not_found", "The pricing plan was not found.");
    const body = objectBody(context);
    const updated = { ...current, ...pickPlan(body) } as Models.PricingPlan;
    map(context, "pricing-plans").set(updatePlan.planId ?? "", updated);
    await persist(context, "pricing-plans");
    return jsonResponse(updated);
  }
  const deletePlan = matchLocalRoute(context, "DELETE", "/api/v1/admin/plans/{planId}");
  if (deletePlan !== undefined) {
    if (!map(context, "pricing-plans").has(deletePlan.planId ?? "")) return jsonResponse(false);
    map(context, "pricing-plans").delete(deletePlan.planId ?? "");
    await persist(context, "pricing-plans");
    return jsonResponse(true);
  }

  const audit = matchLocalRoute(context, "GET", "/api/v1/admin/audit");
  if (audit !== undefined) return jsonResponse(localPage(values(context, "audit")));
  const notifications = matchLocalRoute(context, "GET", "/api/v1/admin/notifications");
  if (notifications !== undefined) return jsonResponse(localPage(values(context, "notifications")));

  const trigger = matchLocalRoute(context, "POST", "/api/v1/admin/billing/reconciliation/{organizationId}:trigger");
  if (trigger !== undefined) {
    const attempt: Models.BillingReconciliationAttempt = {
      attemptId: (await context.store.nextUlid()) as Ulid, organizationId: asUlid(trigger.organizationId), provider: "local", state: "queued", requestedAt: context.now,
      enqueuedAt: context.now, startedAt: null, finishedAt: null, reconciledAt: null, errorCode: null,
    };
    map(context, "billing-attempts").set(attempt.attemptId, attempt);
    await persist(context, "billing-attempts");
    return jsonResponse(attempt, 202);
  }
  const getAttempt = matchLocalRoute(context, "GET", "/api/v1/admin/billing/reconciliation/{organizationId}/{attemptId}");
  if (getAttempt !== undefined) {
    const attemptId = getAttempt.attemptId ?? "";
    const attempt = (map(context, "billing-attempts").get(attemptId)
      ?? await context.store.get<Models.BillingReconciliationAttempt>("billing-attempts", "local", attemptId)) as Models.BillingReconciliationAttempt | undefined;
    return attempt === undefined || attempt.organizationId !== asUlid(getAttempt.organizationId) ? problemResponse(404, "not_found", "The reconciliation attempt was not found.") : jsonResponse(attempt);
  }
  return undefined;
}

const caches = new WeakMap<object, Map<string, AnyMap>>();
const loaded = new WeakMap<object, Set<string>>();
const persistedIds = new WeakMap<object, Map<string, Set<string>>>();

export function clearLocalAdminState(store: object): void {
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
  const records = await context.store.list<unknown>(storageName(name), name === "users" ? "global" : "local");
  for (const [index, value] of records.entries()) {
    const id = recordId(value) ?? String(index);
    map(context, name).set(id, value);
  }
  let byName = persistedIds.get(context.store);
  if (byName === undefined) { byName = new Map(); persistedIds.set(context.store, byName); }
  byName.set(name, new Set(map(context, name).keys()));
  set.add(name);
}
async function persist(context: LocalRequestContext, name: string): Promise<void> {
  const byName = persistedIds.get(context.store);
  const before = byName?.get(name) ?? new Set<string>();
  const namespace = storageName(name);
  const scope = name === "users" ? "global" : "local";
  for (const id of before) if (!map(context, name).has(id)) await context.store.delete(namespace, scope, id);
  for (const [id, value] of map(context, name)) await context.store.put(namespace, scope, id, value);
  byName?.set(name, new Set(map(context, name).keys()));
}
function recordId(value: unknown): string | undefined { if (value === null || typeof value !== "object") return undefined; const item = value as Record<string, unknown>; for (const key of ["userId", "organizationId", "projectId", "databaseId", "supportNoteId", "planId", "auditEventId", "notificationId", "attemptId"]) if (typeof item[key] === "string") return item[key]; return undefined; }
function storageName(name: string): string { return name === "users" ? "auth:users" : name; }
async function hydrateDatabases(context: LocalRequestContext): Promise<void> {
  const projects = values(context, "projects");
  for (const project of projects) {
    const projectId = asRecord(project).projectId;
    if (typeof projectId !== "string") continue;
    const records = await context.store.list<unknown>("sdk-local-database", projectId);
    for (const value of records) { const id = recordId(value); if (id !== undefined) map(context, "databases").set(id, value); }
  }
}
function values(context: LocalRequestContext, name: string): readonly unknown[] { return [...map(context, name).values()]; }
function localPage(items: readonly unknown[]): Models.Page<unknown> { return { items, totalCount: items.length, nextCursor: null }; }
function asRecord(value: unknown): Record<string, unknown> { return value !== null && typeof value === "object" ? { ...(value as Record<string, unknown>) } : {}; }
function asUlid(value: string | undefined): Ulid { return tryUlid(value ?? "") ?? FALLBACK_ID; }
function asOptionalUlid(value: unknown): Ulid | undefined { return typeof value === "string" ? tryUlid(value) : undefined; }
function optionalField(name: string, value: unknown): Record<string, Ulid> { const id = asOptionalUlid(value); return id === undefined ? {} : { [name]: id }; }
function adminUser(value: unknown): Models.AdminUser {
  const input = asRecord(value);
  const id = asUlid(typeof input.userId === "string" ? input.userId : "");
  const orgValues = Array.isArray(input.organizationIds) ? input.organizationIds : [input.personalOrgId, input.activeOrgId];
  const orgs = orgValues.filter((v): v is string => typeof v === "string").map(asUlid).filter((v, index, all) => all.indexOf(v) === index);
  const displayName = typeof input.displayName === "string" ? input.displayName : typeof input.name === "string" ? input.name : undefined;
  return { userId: id, email: typeof input.email === "string" ? input.email : "developer@terrascale.local", ...(displayName === undefined ? {} : { displayName }), enabled: input.enabled !== false, owner: input.owner === true || input.roles === "owner", organizationIds: orgs, createdAt: typeof input.createdAt === "string" ? input.createdAt : new Date(0).toISOString() };
}
function adminUsage(context: LocalRequestContext): Models.AdminPlatformUsage {
  return { organizations: map(context, "organizations").size, projects: map(context, "projects").size, databases: map(context, "databases").size, regions: [], usage: [{ name: "operations", quantity: 0, unit: "operations" }] };
}
async function makePlan(context: LocalRequestContext, body: Record<string, unknown>): Promise<Models.PricingPlan> { return { planId: (await context.store.nextUlid()) as Ulid, name: typeof body.name === "string" ? body.name : "Local plan", key: typeof body.key === "string" ? body.key : "local", currency: typeof body.currency === "string" ? body.currency : "USD", monthlyBaseCents: numberValue(body.monthlyBaseCents), annualBaseCents: numberValue(body.annualBaseCents), includedUsage: Array.isArray(body.includedUsage) ? body.includedUsage as Models.UsageMetric[] : [], provider: "local", monthlyPriceId: null, annualPriceId: null, monthlyPolarProductId: null, annualPolarProductId: null, monthlyStripePriceId: null, annualStripePriceId: null, monthlyTrialDays: numberValue(body.monthlyTrialDays), annualTrialDays: numberValue(body.annualTrialDays), visible: body.visible !== false, maxRegions: numberValue(body.maxRegions, 1), operationOverageCentsPerMillion: numberValue(body.operationOverageCentsPerMillion), storageOverageCentsPerGbRegion: numberValue(body.storageOverageCentsPerGbRegion) }; }
function pickPlan(body: Record<string, unknown>): Partial<Models.PricingPlan> { const fields = ["key", "name", "currency", "monthlyBaseCents", "annualBaseCents", "monthlyPolarProductId", "annualPolarProductId", "monthlyStripePriceId", "annualStripePriceId", "monthlyTrialDays", "annualTrialDays", "visible", "maxRegions", "operationOverageCentsPerMillion", "storageOverageCentsPerGbRegion", "includedUsage"] as const; return Object.fromEntries(fields.filter((field) => body[field] !== undefined).map((field) => [field, body[field]])) as Partial<Models.PricingPlan>; }
function numberValue(value: unknown, fallback = 0): number { return typeof value === "number" && Number.isFinite(value) ? value : fallback; }

/** Remove the user's complete local identity footprint, including records
 * whose durable key is not the user id (email-index, sessions, and factors).
 * Pending tokens are deterministic in the local auth adapter; deleting the
 * known token forms also covers pending signup/reset/magic-link records. */
async function deleteOwnedUserState(context: LocalRequestContext, userId: string, rawUser: unknown): Promise<void> {
  const user = asRecord(rawUser);
  const email = typeof user.email === "string" ? user.email : "";
  await deleteMatching(context, "auth:usersByEmail", (value) => asRecord(value).userId === userId, (value) => asRecord(value).email ?? email);
  await deleteMatching(context, "auth:sessions", (value) => asRecord(value).userId === userId, (value) => asRecord(value).sessionId);
  await deleteMatching(context, "auth:factors", (value) => asRecord(value).userId === userId, (value) => asRecord(value).id);
  const pending = await context.store.list<unknown>("auth:pending", "global");
  if (pending.length > 0 && pending.every((value) => asRecord(value).userId === userId)) context.store.clearNamespace("auth:pending");
  await context.store.delete("auth:recovery", "global", userId);
  await deleteMatching(context, "onboarding", (value) => asRecord(value).userId === userId, () => userId);
  // auth:users is deleted by the caller after the cache has been updated.
  // The pending adapter keys are deterministic hashes of e-mail and kind.
  for (const kind of ["signup", "reset", "magic", "email-change"]) await context.store.delete("auth:pending", "global", localToken(kind, email));
}
async function deleteMatching(context: LocalRequestContext, namespace: string, matches: (value: unknown) => boolean, id: (value: unknown) => unknown): Promise<void> {
  const records = await context.store.list<unknown>(namespace, "global");
  for (const value of records) { if (!matches(value)) continue; const key = id(value); if (typeof key === "string" && key.length > 0) await context.store.delete(namespace, "global", key); }
}
function localToken(kind: string, value: string): string {
  let hash = 2166136261;
  for (const character of value) hash = Math.imul(hash ^ character.charCodeAt(0), 16777619);
  return `local-${kind}-${(hash >>> 0).toString(36)}`;
}
