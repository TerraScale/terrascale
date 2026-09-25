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
  await Promise.all(["users", "organizations", "projects", "databases", "support-notes", "audit", "notifications"].map((name) => hydrate(context, name)));
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
    if (!map(context, "users").has(userId)) return problemResponse(404, "not_found", "The user was not found.");
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

  const audit = matchLocalRoute(context, "GET", "/api/v1/admin/audit");
  if (audit !== undefined) return jsonResponse(localPage(values(context, "audit")));
  const notifications = matchLocalRoute(context, "GET", "/api/v1/admin/notifications");
  if (notifications !== undefined) return jsonResponse(localPage(values(context, "notifications")));

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
function recordId(value: unknown): string | undefined { if (value === null || typeof value !== "object") return undefined; const item = value as Record<string, unknown>; for (const key of ["userId", "organizationId", "projectId", "databaseId", "supportNoteId", "auditEventId", "notificationId"]) if (typeof item[key] === "string") return item[key]; return undefined; }
function storageName(name: string): string { return name; }
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
