import type * as Models from "../../models.js";
import type { ProjectId, Ulid } from "../../ids.js";
import { regionalApiHostname, terraScaleRegions } from "../../regions.js";
import {
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalHandler,
  type LocalRequestContext,
} from "../context.js";
import type { TerraScaleSqliteStore } from "../store.js";

type NotificationRecord = Models.Notification & { readonly userId?: string };

const DEFAULT_LOCAL_USER_ID = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const LOCAL_SCOPE = "local";

export const handleLocalMetadata: LocalHandler = async (context) => {
  const store = context.store;
  let match = matchLocalRoute(context, "GET", "/api/v1/regions");
  if (match !== undefined) {
    const regions = terraScaleRegions.map((region): Models.RegionSummary => ({
      code: region.code,
      name: region.displayName,
      country: countryFor(region.code),
      latitude: region.latitude,
      longitude: region.longitude,
      available: region.available,
    }));
    return jsonResponse({
      regions,
      hosts: Object.fromEntries(regions.map((region) => [region.code, regionalApiHostname(region.code)])),
    } satisfies Models.RegionCatalogResponse);
  }
  const projectId = projectIdFromContext(context) ?? projectIdFromRoute(context);
  if (projectId !== undefined) {
    match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/saved-queries");
    if (match !== undefined && match.projectId! === projectId) return savedQueries(store, projectId, context.url.searchParams);
    match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/saved-queries");
    if (match !== undefined && match.projectId! === projectId) return createSavedQuery(store, context, projectId);
    match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}");
    if (match !== undefined && match.projectId! === projectId) return getSavedQuery(store, projectId, match.savedQueryId!);
    match = matchLocalRoute(context, "PUT", "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}");
    if (match !== undefined && match.projectId! === projectId) return updateSavedQuery(store, context, projectId, match.savedQueryId!);
    match = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}");
    if (match !== undefined && match.projectId! === projectId) return deleteSavedQuery(store, projectId, match.savedQueryId!);

    match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/access/team-members");
    if (match !== undefined && match.projectId! === projectId) return jsonResponse(await pageItems<Models.TeamMember>(store, "team-members", `${projectId}:`));
    match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/access/team-members");
    if (match !== undefined && match.projectId! === projectId) return addTeamMember(store, context, projectId);
    match = matchLocalRoute(context, "PATCH", "/api/v1/projects/{projectId}/access/team-members/{userId}");
    if (match !== undefined && match.projectId! === projectId) return updateTeamMember(store, context, projectId, match.userId!);
    match = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/access/team-members/{userId}");
    if (match !== undefined && match.projectId! === projectId) return removeTeamMember(store, projectId, match.userId!);

    match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/audit");
    if (match !== undefined && match.projectId! === projectId) return jsonResponse(await auditPage(store, projectId));
    match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/audit/{auditEventId}");
    if (match !== undefined && match.projectId! === projectId) return getAuditEvent(store, projectId, match.auditEventId!);

  }

  match = matchLocalRoute(context, "GET", "/api/v1/users/me/notifications");
  if (match !== undefined) return listNotifications(store, context);
  match = matchLocalRoute(context, "POST", "/api/v1/users/me/notifications/{notificationId}:mark-read");
  if (match !== undefined) return markNotificationRead(store, context, match.notificationId!);
  match = matchLocalRoute(context, "POST", "/api/v1/users/me/notifications:mark-all-read");
  if (match !== undefined) return markAllNotificationsRead(store, context);

  return undefined;
};

async function createSavedQuery(store: TerraScaleSqliteStore, context: LocalRequestContext, projectId: string): Promise<Response> {
  const body = objectBody(context);
  const name = text(body.name);
  const query = text(body.query);
  if (name === undefined || query === undefined) return problemResponse(400, "invalid_request", "name and query are required.");
  const savedQueryId = (await nextId(store)) as Ulid;
  const item: Models.ProjectSavedQueryResponse = {
    savedQueryId, projectId: projectId as ProjectId, name, query,
    createdAt: context.now, updatedAt: context.now, revision: "1",
  };
  await put(store, "saved-queries", queryKey(projectId, savedQueryId), item);
  return jsonResponse(item, 201);
}

async function updateSavedQuery(store: TerraScaleSqliteStore, context: LocalRequestContext, projectId: string, id: string): Promise<Response> {
  const old = await get<Models.ProjectSavedQueryResponse>(store, "saved-queries", queryKey(projectId, id));
  if (old === undefined) return notFound("saved query");
  const body = objectBody(context);
  const currentRevision = revisionOf(store, projectId, id);
  const revision = await currentRevision;
  if (body.revision !== revision) return problemResponse(409, "conflict", "The saved query revision is stale.");
  const name = text(body.name) ?? old.name;
  const query = text(body.query) ?? old.query;
  const nextRevision = String(Number(revision) + 1);
  const updated = { ...old, name, query, updatedAt: context.now, revision: nextRevision };
  await put(store, "saved-queries", queryKey(projectId, id), updated);
  await store.setMeta(`saved-query-revisions:${queryKey(projectId, id)}`, nextRevision);
  return jsonResponse(updated);
}

async function deleteSavedQuery(store: TerraScaleSqliteStore, projectId: string, id: string): Promise<Response> {
  const old = await get<Models.ProjectSavedQueryResponse>(store, "saved-queries", queryKey(projectId, id));
  if (old === undefined) return notFound("saved query");
  await remove(store, "saved-queries", queryKey(projectId, id));
  return jsonResponse({ savedQueryId: old.savedQueryId, projectId: old.projectId, deleted: true } satisfies Models.DeletedProjectSavedQueryResponse);
}

async function getSavedQuery(store: TerraScaleSqliteStore, projectId: string, id: string): Promise<Response> {
  const item = await get<Models.ProjectSavedQueryResponse>(store, "saved-queries", queryKey(projectId, id));
  if (item === undefined) return notFound("saved query");
  return jsonResponse(item);
}

async function savedQueries(store: TerraScaleSqliteStore, projectId: string, params: URLSearchParams): Promise<Response> {
  const items = await records<Models.ProjectSavedQueryResponse>(store, "saved-queries", `${projectId}:`);
  const requestedTake = Number(params.get("take") ?? "100");
  const take = Number.isFinite(requestedTake) ? Math.max(1, Math.min(1000, Math.trunc(requestedTake))) : 100;
  const requestedSkip = Number(params.get("skip") ?? "0");
  const skip = Number.isFinite(requestedSkip) ? Math.max(0, Math.trunc(requestedSkip)) : 0;
  const offset = params.has("cursor") ? decodeCursor(params.get("cursor")) : skip;
  if (offset === undefined) {
    return problemResponse(400, "invalid_request", "cursor must be a canonical non-negative integer.");
  }
  const selected = items.slice(offset, offset + take);
  const nextCursor = offset + take < items.length ? encodeCursor(offset + take) : null;
  return jsonResponse({ items: selected, totalCount: items.length, nextCursor } satisfies Models.ProjectSavedQueryPageResponse);
}

async function addTeamMember(store: TerraScaleSqliteStore, context: LocalRequestContext, projectId: string): Promise<Response> {
  const body = objectBody(context);
  const userId = text(body.userId);
  const role = body.role;
  if (userId === undefined || (role !== "member" && role !== "owner" && role !== "viewer")) return problemResponse(400, "invalid_request", "userId and a valid role are required.");
  const key = `${projectId}:${userId}`;
  if (await get(store, "team-members", key) !== undefined) return problemResponse(409, "conflict", "The team member already exists.");
  const member: Models.TeamMember = { userId: userId as Ulid, email: `${userId}@local.test`, displayName: "Local team member", role, joinedAt: context.now };
  await put(store, "team-members", key, member);
  return jsonResponse(member, 201);
}

async function updateTeamMember(store: TerraScaleSqliteStore, context: LocalRequestContext, projectId: string, userId: string): Promise<Response> {
  const key = `${projectId}:${userId}`;
  const old = await get<Models.TeamMember>(store, "team-members", key);
  if (old === undefined) return notFound("team member");
  const role = objectBody(context).role;
  if (role !== "member" && role !== "owner" && role !== "viewer") return problemResponse(400, "invalid_request", "role is invalid.");
  const member = { ...old, role };
  await put(store, "team-members", key, member);
  return jsonResponse(member);
}

async function removeTeamMember(store: TerraScaleSqliteStore, projectId: string, userId: string): Promise<Response> {
  const key = `${projectId}:${userId}`;
  if (await get(store, "team-members", key) === undefined) return notFound("team member");
  await remove(store, "team-members", key);
  return new Response(null, { status: 204 });
}

async function auditPage(store: TerraScaleSqliteStore, projectId: string): Promise<Models.Page<Models.AuditEvent>> {
  const items = await records<Models.AuditEvent>(store, "audit", `${projectId}:`);
  return { items, totalCount: items.length, nextCursor: null };
}

async function getAuditEvent(store: TerraScaleSqliteStore, projectId: string, id: string): Promise<Response> {
  const item = await get<Models.AuditEvent>(store, "audit", `${projectId}:${id}`);
  return item === undefined ? notFound("audit event") : jsonResponse(item);
}

async function listNotifications(store: TerraScaleSqliteStore, context: LocalRequestContext): Promise<Response> {
  const params = context.url.searchParams;
  const projectId = text(params.get("projectId")) ?? undefined;
  const organizationId = text(params.get("organizationId")) ?? undefined;
  const unreadOnly = params.get("unreadOnly") === "true";
  const userId = await currentLocalUserId(store);
  const items = (await records<NotificationRecord>(store, "notifications")).filter((item) =>
    item.userId === userId && (!projectId || item.projectId === projectId) && (!organizationId || item.organizationId === organizationId) && (!unreadOnly || item.readAt === undefined));
  return jsonResponse(items.map(publicNotification));
}

async function markNotificationRead(store: TerraScaleSqliteStore, context: LocalRequestContext, id: string): Promise<Response> {
  const item = await get<NotificationRecord>(store, "notifications", id);
  if (item === undefined || item.userId !== await currentLocalUserId(store)) return notFound("notification");
  const updated = { ...item, readAt: item.readAt ?? context.now };
  await put(store, "notifications", id, updated);
  return jsonResponse(publicNotification(updated));
}

async function markAllNotificationsRead(store: TerraScaleSqliteStore, context: LocalRequestContext): Promise<Response> {
  const organizationId = context.url.searchParams.get("organizationId");
  const userId = await currentLocalUserId(store);
  const items = await records<NotificationRecord>(store, "notifications");
  let markedRead = 0;
  for (const item of items) {
    if (item.userId !== userId || item.readAt !== undefined || (organizationId !== null && item.organizationId !== organizationId)) continue;
    markedRead += 1;
    await put(store, "notifications", item.notificationId, { ...item, readAt: context.now });
  }
  return jsonResponse({ markedRead } satisfies Models.MarkAllNotificationsReadResponse);
}

function publicNotification(item: NotificationRecord): Models.Notification {
  const { userId: _userId, ...notification } = item;
  return notification;
}

function projectIdFromContext(context: LocalRequestContext): string | undefined {
  return text(context.request.headers.get("x-terrascale-project-id")) ?? undefined;
}
function projectIdFromRoute(context: LocalRequestContext): string | undefined {
  const match = /^\/api\/v(?:1|2|3)\/projects\/([^/]+)/u.exec(context.pathname);
  return match?.[1] === undefined ? undefined : decodeURIComponent(match[1]);
}
function queryKey(projectId: string, id: string): string { return `${projectId}:${id}`; }
function text(value: unknown): string | undefined { return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined; }
function countryFor(code: string): string { return code === "GRU" ? "BR" : code === "SFO" ? "US" : "DE"; }
function notFound(resource: string): Response { return problemResponse(404, "not_found", `${resource} was not found.`); }

async function currentLocalUserId(store: TerraScaleSqliteStore): Promise<string> {
  const stored = await store.getMeta("local.defaultUserId");
  const defaultId = typeof stored === "string" ? stored : undefined;
  return defaultId ?? DEFAULT_LOCAL_USER_ID;
}

async function nextId(store: TerraScaleSqliteStore): Promise<string> {
  // Metadata has no ID generator of its own; the store's monotonic ULID is
  // the one source of valid IDs shared by all local handlers.
  return store.nextUlid();
}

async function revisionOf(store: TerraScaleSqliteStore, projectId: string, id: string): Promise<string> {
  const value = await store.getMeta(`saved-query-revisions:${queryKey(projectId, id)}`);
  return typeof value === "string" && /^\d+$/u.test(value) ? value : "1";
}

async function get<T>(store: TerraScaleSqliteStore, namespace: string, key: string): Promise<T | undefined> { const [scope, id] = storageKey(namespace, key); return store.get<T>(namespace, scope, id); }
async function records<T>(store: TerraScaleSqliteStore, namespace: string, prefix?: string): Promise<T[]> {
  const [scope] = prefix === undefined ? [LOCAL_SCOPE] : storageKey(namespace, prefix);
  return store.list<T>(namespace, scope);
}
async function pageItems<T>(store: TerraScaleSqliteStore, namespace: string, prefix: string): Promise<Models.Page<T>> {
  const items = await records<T>(store, namespace, prefix);
  return { items, totalCount: items.length, nextCursor: null };
}
function decodeCursor(cursor: string | null): number | undefined {
  if (cursor === null || !/^(?:0|[1-9]\d*)$/u.test(cursor)) return undefined;
  const offset = Number(cursor);
  return Number.isSafeInteger(offset) ? offset : undefined;
}
function encodeCursor(offset: number): string { return String(offset); }
async function put(store: TerraScaleSqliteStore, namespace: string, key: string, value: unknown): Promise<void> { const [scope, id] = storageKey(namespace, key); store.put(namespace, scope, id, value); }
async function remove(store: TerraScaleSqliteStore, namespace: string, key: string): Promise<void> { const [scope, id] = storageKey(namespace, key); store.delete(namespace, scope, id); }
function storageKey(namespace: string, key: string): [string, string] {
  if (namespace === "saved-queries" || namespace === "team-members" || namespace === "audit") {
    const separator = key.indexOf(":");
    if (separator > 0) return [key.slice(0, separator), key.slice(separator + 1)];
  }
  return [LOCAL_SCOPE, key];
}
