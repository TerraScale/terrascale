import type * as Models from "../../models.js";
import type { OrganizationId, ProjectId, Ulid, UserId } from "../../ids.js";
import { normalizeRegionCodes, parseRegionCode } from "../../regions.js";
import {
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalHandler,
  type LocalRequestContext,
} from "../context.js";
import type { TerraScaleSqliteStore } from "../store.js";

/*
 * The local adapter deliberately stores complete public DTOs.  This keeps the
 * adapter useful to the Dashboard while leaving the SQLite implementation
 * free to choose its table layout (and also makes old local databases safe to
 * read when a DTO gains an optional field).
 */
const LOCAL_USER_ID = "01ARZ3NDEKTSV4RRFFQ69G5FAV" as UserId;
const LOCAL_USER_EMAIL = "local@example.test";
const LOCAL_USER_NAME = "Local User";
const LOCAL_SCOPE = "local";

export const handleLocalOrganizations: LocalHandler = async (context) => {
  const store = context.store;
  const body = objectBody(context);

  let match = matchLocalRoute(context, "GET", "/api/v1/organizations");
  if (match !== undefined) {
    return jsonResponse(await records<Models.Organization>(store, "organizations"));
  }
  if (matchLocalRoute(context, "POST", "/api/v1/organizations") !== undefined) {
    const name = nonBlank(body.name);
    if (name === undefined) return problemResponse(400, "invalid_request", "name is required.");
    const organizationId = (await store.nextUlid()) as OrganizationId;
    const organization: Models.Organization = {
      organizationId,
      name,
      ownerUserIds: [LOCAL_USER_ID],
      createdByUserId: LOCAL_USER_ID,
      status: "Active",
      deletionRequestedAt: null,
      deletionRequestedByUserId: null,
      createdAt: context.now,
    };
    await put(store, "organizations", organizationId, organization);
    await put(store, "organization-members", memberKey(organizationId, LOCAL_USER_ID), ownerMember(context.now));
    return jsonResponse(organization, 201);
  }

  match = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}");
  if (match !== undefined) {
    return getOrganization(store, match.organizationId!);
  }
  match = matchLocalRoute(context, "PATCH", "/api/v1/organizations/{organizationId}");
  if (match !== undefined) {
    const organization = await get<Models.Organization>(store, "organizations", match.organizationId!);
    if (organization === undefined) return notFound("organization");
    const name = nonBlank(body.name);
    if (name === undefined) return problemResponse(400, "invalid_request", "name is required.");
    const updated: Models.Organization = { ...organization, name };
    await put(store, "organizations", match.organizationId!, updated);
    return jsonResponse(updated);
  }
  match = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}:request-deletion");
  if (match !== undefined) {
    const organization = await get<Models.Organization>(store, "organizations", match.organizationId!);
    if (organization === undefined) return notFound("organization");
    if (body.confirm !== true || body.organizationName !== organization.name) {
      return problemResponse(400, "confirmation_required", "confirm and the organization name are required.");
    }
    const updated: Models.Organization = {
      ...organization,
      status: "DeletionPending",
      deletionRequestedAt: context.now,
      deletionRequestedByUserId: LOCAL_USER_ID,
    };
    await put(store, "organizations", match.organizationId!, updated);
    return jsonResponse(updated);
  }
  match = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}:leave");
  if (match !== undefined) {
    const organization = await get<Models.Organization>(store, "organizations", match.organizationId!);
    if (organization === undefined) return notFound("organization");
    const key = memberKey(match.organizationId!, LOCAL_USER_ID);
    if (await get(store, "organization-members", key) === undefined) return notFound("membership");
    await remove(store, "organization-members", key);
    return new Response(null, { status: 204 });
  }

  match = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/projects");
  if (match !== undefined) {
    const organizationId = match.organizationId!;
    if (await get<Models.Organization>(store, "organizations", organizationId) === undefined) return notFound("organization");
    const all = await records<Models.Project>(store, "projects");
    return jsonResponse(all.filter((project) => project.organizationId === organizationId));
  }
  match = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}/projects");
  if (match !== undefined) {
    if (await get<Models.Organization>(store, "organizations", match.organizationId!) === undefined) return notFound("organization");
    const name = nonBlank(body.name);
    const backupRegion = typeof body.backupRegion === "string" ? body.backupRegion : undefined;
    const regions = stringArray(body.regions);
    if (name === undefined || backupRegion === undefined || regions === undefined) {
      return problemResponse(400, "invalid_request", "name, regions, and backupRegion are required.");
    }
    let selectedRegions: Models.Project["regions"];
    let selectedBackupRegion: Models.Project["backupRegion"];
    try {
      selectedRegions = normalizeRegionCodes(regions);
      selectedBackupRegion = parseRegionCode(backupRegion, "backupRegion");
    } catch (error) {
      return problemResponse(400, "invalid_request", regionValidationMessage(error));
    }
    const projectId = (await store.nextUlid()) as ProjectId;
    const slug = nonBlank(body.slug);
    const project: Models.Project = {
      projectId,
      organizationId: match.organizationId! as OrganizationId,
      name,
      ownerUserIds: [LOCAL_USER_ID],
      regions: selectedRegions,
      backupRegion: selectedBackupRegion,
      placementStatus: "Ready",
      placementGeneration: 1,
      ...(slug === undefined ? {} : { slug }),
      createdAt: context.now,
    };
    await put(store, "projects", projectId, project);
    return jsonResponse(project, 201);
  }

  match = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/members");
  if (match !== undefined) {
    if (await get<Models.Organization>(store, "organizations", match.organizationId!) === undefined) return notFound("organization");
    return jsonResponse(await recordsByPrefix<Models.OrganizationMember>(store, "organization-members", `${match.organizationId!}:`));
  }
  match = matchLocalRoute(context, "PATCH", "/api/v1/organizations/{organizationId}/members/{userId}");
  if (match !== undefined) {
    const key = memberKey(match.organizationId!, match.userId!);
    const member = await get<Models.OrganizationMember>(store, "organization-members", key);
    if (member === undefined) return notFound("member");
    const role = body.role;
    if (role !== "Member" && role !== "OrganizationOwner") return problemResponse(400, "invalid_request", "role is invalid.");
    const updated: Models.OrganizationMember = { ...member, role };
    await put(store, "organization-members", key, updated);
    return jsonResponse(updated);
  }
  match = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}/members/{userId}:remove");
  if (match !== undefined) {
    if (body.confirm !== true) return problemResponse(400, "confirmation_required", "confirm must be true.");
    const key = memberKey(match.organizationId!, match.userId!);
    if (await get(store, "organization-members", key) === undefined) return notFound("member");
    await remove(store, "organization-members", key);
    return new Response(null, { status: 204 });
  }

  match = matchLocalRoute(context, "GET", "/api/v1/organizations/{organizationId}/invitations");
  if (match !== undefined) {
    if (await get<Models.Organization>(store, "organizations", match.organizationId!) === undefined) return notFound("organization");
    return jsonResponse(await recordsByPrefix<Models.Invitation>(store, "invitations", `${match.organizationId!}:`));
  }
  match = matchLocalRoute(context, "POST", "/api/v1/organizations/{organizationId}/invitations");
  if (match !== undefined) {
    if (await get<Models.Organization>(store, "organizations", match.organizationId!) === undefined) return notFound("organization");
    const email = nonBlank(body.email);
    const responseUrlTemplate = nonBlank(body.responseUrlTemplate);
    if (email === undefined || responseUrlTemplate === undefined) return problemResponse(400, "invalid_request", "email and responseUrlTemplate are required.");
    const invitationId = (await store.nextUlid()) as Ulid;
    const invitationProjectId = nonBlank(body.projectId);
    const invitation: Models.Invitation = {
      invitationId,
      organizationId: match.organizationId! as OrganizationId,
      ...(invitationProjectId === undefined ? { projectId: null } : { projectId: invitationProjectId as ProjectId }),
      email,
      role: "Member",
      status: "Pending",
      invitedByEmail: LOCAL_USER_EMAIL,
      expiresAt: new Date(Date.parse(context.now) + 7 * 86_400_000).toISOString(),
      acceptedAt: null,
      acceptedByUserId: null,
      declinedAt: null,
      canceledAt: null,
      lastSentAt: context.now,
      sentCount: 1,
      createdAt: context.now,
    };
    await put(store, "invitations", invitationKey(match.organizationId!, invitationId), invitation);
    return jsonResponse(invitation, 201);
  }

  for (const action of ["accept", "decline", "resend", "cancel"] as const) {
    match = matchLocalRoute(context, "POST", `/api/v1/organizations/{organizationId}/invitations/{invitationId}:${action}`);
    if (match !== undefined) return invitationAction(store, context, match.organizationId!, match.invitationId!, action);
  }

  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}");
  if (match !== undefined) return getProject(store, match.projectId!);
  match = matchLocalRoute(context, "PATCH", "/api/v1/projects/{projectId}");
  if (match !== undefined) {
    const project = await get<Models.Project>(store, "projects", match.projectId!);
    if (project === undefined) return notFound("project");
    const projectName = nonBlank(body.name);
    let projectRegions: Models.Project["regions"] | undefined;
    if (Object.hasOwn(body, "regions")) {
      const regionValues = stringArray(body.regions);
      if (regionValues === undefined) {
        return problemResponse(400, "invalid_request", "regions must be an array of exact RegionCode values.");
      }
      try {
        projectRegions = normalizeRegionCodes(regionValues);
      } catch (error) {
        return problemResponse(400, "invalid_request", regionValidationMessage(error));
      }
    }
    let projectBackupRegion: Models.Project["backupRegion"] | undefined;
    if (Object.hasOwn(body, "backupRegion")) {
      try {
        projectBackupRegion = parseRegionCode(body.backupRegion, "backupRegion");
      } catch (error) {
        return problemResponse(400, "invalid_request", regionValidationMessage(error));
      }
    }
    const projectSlug = nonBlank(body.slug);
    const updated: Models.Project = {
      ...project,
      ...(projectName === undefined ? {} : { name: projectName }),
      ...(projectRegions === undefined ? {} : { regions: projectRegions }),
      ...(projectBackupRegion === undefined ? {} : { backupRegion: projectBackupRegion }),
      ...(projectSlug === undefined ? {} : { slug: projectSlug }),
      placementStatus: "Ready",
      placementGeneration: project.placementGeneration + 1,
    };
    await put(store, "projects", match.projectId!, updated);
    return jsonResponse(updated);
  }
  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/regions");
  if (match !== undefined) {
    const project = await get<Models.Project>(store, "projects", match.projectId!);
    if (project === undefined) return notFound("project");
    return jsonResponse(project.regions.map((region): Models.ProjectRegionPlacement => ({
      region,
      placementGeneration: project.placementGeneration,
      status: "Ready",
      availability: "available",
      isBackupRegion: region === project.backupRegion,
      replicationLagMilliseconds: 0,
      error: null,
      updatedAt: context.now,
    })));
  }
  match = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}");
  if (match !== undefined) {
    const project = await get<Models.Project>(store, "projects", match.projectId!);
    if (project === undefined) return notFound("project");
    if (body.confirm !== true || body.projectName !== project.name) return problemResponse(400, "confirmation_required", "confirm and the project name are required.");
    const counts = await cascadeProject(store, match.projectId!);
    await remove(store, "projects", match.projectId!);
    const deleted: Models.DeletedProject = {
      projectId: project.projectId,
      organizationId: project.organizationId,
      name: project.name,
      deleted: true,
      databasesDeleted: counts.databases,
      physicalStorageDeleted: counts.databases > 0,
    };
    return jsonResponse(deleted);
  }

  return undefined;
};

async function invitationAction(
  store: TerraScaleSqliteStore,
  context: LocalRequestContext,
  organizationId: string,
  invitationId: string,
  action: "accept" | "decline" | "resend" | "cancel",
): Promise<Response> {
  const key = invitationKey(organizationId, invitationId);
  const invitation = await get<Models.Invitation>(store, "invitations", key);
  if (invitation === undefined) return notFound("invitation");
  if (action !== "resend" && invitation.status !== "Pending") return problemResponse(409, "conflict", "The invitation is no longer pending.");
  const updated: Models.Invitation = action === "accept"
    ? { ...invitation, status: "Accepted", acceptedAt: context.now, acceptedByUserId: LOCAL_USER_ID }
    : action === "decline"
      ? { ...invitation, status: "Declined", declinedAt: context.now }
      : action === "cancel"
        ? { ...invitation, status: "Canceled", canceledAt: context.now }
        : { ...invitation, lastSentAt: context.now, sentCount: invitation.sentCount + 1 };
  await put(store, "invitations", key, updated);
  if (action === "accept") {
    await put(store, "organization-members", memberKey(organizationId, LOCAL_USER_ID), ownerMember(context.now, "Member"));
  }
  return jsonResponse(updated);
}

async function getOrganization(store: TerraScaleSqliteStore, id: string): Promise<Response> {
  const item = await get<Models.Organization>(store, "organizations", id);
  return item === undefined ? notFound("organization") : jsonResponse(item);
}

async function getProject(store: TerraScaleSqliteStore, id: string): Promise<Response> {
  const item = await get<Models.Project>(store, "projects", id);
  return item === undefined ? notFound("project") : jsonResponse(item);
}

function ownerMember(joinedAt: string, role: Models.OrganizationMemberRole = "OrganizationOwner"): Models.OrganizationMember {
  return { userId: LOCAL_USER_ID, email: LOCAL_USER_EMAIL, displayName: LOCAL_USER_NAME, role, joinedAt };
}

function memberKey(organizationId: string, userId: string): string { return `${organizationId}:${userId}`; }
function invitationKey(organizationId: string, invitationId: string): string { return `${organizationId}:${invitationId}`; }
function nonBlank(value: unknown): string | undefined { return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined; }
function stringArray(value: unknown): string[] | undefined { return Array.isArray(value) && value.every((item) => typeof item === "string") ? value : undefined; }
function regionValidationMessage(error: unknown): string { return error instanceof Error ? error.message : "The Region selection is invalid."; }
function notFound(resource: string): Response { return problemResponse(404, "not_found", `${resource} was not found.`); }

type CascadeCounts = {
  readonly databases: number;
};

/** Remove project-owned records emitted by the other local public handlers. */
async function cascadeProject(store: TerraScaleSqliteStore, projectId: string): Promise<CascadeCounts> {
  let databases = 0;
  const ownedResourceIds = new Set<string>();

  // Data handler uses project-scoped SQLite records. Keep the index rows out
  // of the count, but remove them along with their entities.
  for (const namespace of ["sdk-local-database", "sdk-local-database-index"] as const) {
    const values = await rawList(store, namespace, projectId);
    for (const value of values) {
      const id = recordStorageId(value, namespace, projectId);
      if (id === undefined) continue;
      await store.delete(namespace, projectId, id);
      ownedResourceIds.add(id);
      if (namespace === "sdk-local-database") databases += 1;
    }
    await store.delete(namespace, projectId, "index");
  }

  // Backup and platform handlers keep all records in local scope and carry
  // projectId in their public DTO (or an internal equivalent).
  for (const namespace of [
    "backups", "backup-policies", "backup-confirmations", "backup-idempotency",
    "restore-points", "restores", "restore-idempotency",
    "notifications",
  ]) {
    const values = await rawList(store, namespace, LOCAL_SCOPE);
    for (const value of values) {
      if (!belongsToProject(value, projectId, ownedResourceIds)) continue;
      const id = recordStorageId(value, namespace, projectId);
      if (id === undefined) continue;
      await store.delete(namespace, LOCAL_SCOPE, id);
      ownedResourceIds.add(id);
    }
  }

  // These namespaces are intentionally project-scoped by this adapter.
  for (const namespace of ["saved-queries", "team-members", "audit"] as const) {
    const values = await rawList(store, namespace, projectId);
    for (const value of values) {
      const id = recordStorageId(value, namespace, projectId);
      if (id !== undefined) await store.delete(namespace, projectId, id);
    }
  }
  return { databases };
}

async function rawList(store: TerraScaleSqliteStore, namespace: string, scope: string): Promise<unknown[]> {
  return store.list<unknown>(namespace, scope);
}
function belongsToProject(value: unknown, projectId: string, ownedResourceIds: ReadonlySet<string>): boolean {
  if (value === null || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  return item.projectId === projectId || item.project_id === projectId || (typeof item.resourceId === "string" && ownedResourceIds.has(item.resourceId)) || (typeof item.backupId === "string" && ownedResourceIds.has(item.backupId));
}
function recordId(value: unknown, namespace: string): string | undefined {
  if (value === null || typeof value !== "object") return undefined;
  const item = value as Record<string, unknown>;
  const fields = namespace === "sdk-local-database" ? ["databaseId", "id"] : ["backupId", "backupPolicyId", "confirmationToken", "storageId", "restorePointId", "restoreId", "keyId", "clientId", "notificationId", "savedQueryId", "userId", "auditEventId"];
  const field = fields.find((candidate) => typeof item[candidate] === "string");
  return field === undefined ? undefined : item[field] as string;
}
function recordStorageId(value: unknown, namespace: string, projectId: string): string | undefined {
  if (value === null || typeof value !== "object") return undefined;
  const item = value as Record<string, unknown>;
  if (namespace === "backup-policies" && typeof item.databaseId === "string") return `${projectId}:${item.databaseId}`;
  return recordId(value, namespace);
}

async function get<T>(store: TerraScaleSqliteStore, namespace: string, key: string): Promise<T | undefined> {
  const [scope, id] = storageKey(namespace, key);
  return store.get<T>(namespace, scope, id);
}

async function records<T>(store: TerraScaleSqliteStore, namespace: string): Promise<T[]> {
  return store.list<T>(namespace, LOCAL_SCOPE);
}

async function recordsByPrefix<T>(store: TerraScaleSqliteStore, namespace: string, prefix: string): Promise<T[]> {
  const scoped = namespace === "organization-members" || namespace === "invitations" ? prefix.slice(0, -1) : LOCAL_SCOPE;
  return store.list<T>(namespace, scoped);
}

async function put(store: TerraScaleSqliteStore, namespace: string, key: string, value: unknown): Promise<void> {
  const [scope, id] = storageKey(namespace, key);
  await store.put(namespace, scope, id, value);
}
async function remove(store: TerraScaleSqliteStore, namespace: string, key: string): Promise<void> {
  const [scope, id] = storageKey(namespace, key);
  await store.delete(namespace, scope, id);
}
function storageKey(namespace: string, key: string): [string, string] {
  if (namespace === "organization-members" || namespace === "invitations") {
    const separator = key.indexOf(":");
    if (separator > 0) return [key.slice(0, separator), key.slice(separator + 1)];
  }
  return [LOCAL_SCOPE, key];
}
