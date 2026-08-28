import { createHash, randomUUID } from "node:crypto";

import type * as Models from "../../models.js";
import type { BackupPolicyId, DatabaseId, ProjectId } from "../../ids.js";
import {
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalHandler,
} from "../context.js";

type Store = {
  get(namespace: string, key: string): unknown | PromiseLike<unknown>;
  list(namespace: string): unknown | PromiseLike<unknown>;
  put(namespace: string, key: string, value: unknown): unknown | PromiseLike<unknown>;
  delete(namespace: string, key: string): unknown | PromiseLike<unknown>;
  nextUlid(): string | PromiseLike<string>;
};
type Confirmation = { readonly projectId: string; readonly databaseId: string; readonly operationSessionId: string; readonly requestDigest: string; readonly expiresAt: string };
type Idempotency = { readonly fingerprint: string; readonly resourceId: string };

const POLICIES = "backup-policies";
const CONFIRMATIONS = "backup-confirmations";
const BACKUPS = "backups";
const RESTORE_POINTS = "restore-points";
const RESTORES = "restores";
const IDEMPOTENCY = "backup-idempotency";
const RESTORE_IDEMPOTENCY = "restore-idempotency";
const ACTOR = "local-user";
const STORE_SCOPE = "local";

export const handleLocalBackups: LocalHandler = async (context) => {
  const raw = context.store;
  const store: Store = {
    get: (namespace, key) => raw.get(namespace, STORE_SCOPE, key),
    list: (namespace) => raw.list(namespace, STORE_SCOPE),
    put: (namespace, key, value) => raw.put(namespace, STORE_SCOPE, key, value),
    delete: (namespace, key) => raw.delete(namespace, STORE_SCOPE, key),
    nextUlid: () => raw.nextUlid(),
  };
  const body = objectBody(context);
  let match = matchLocalRoute(context, "PUT", "/api/v1/projects/{projectId}/databases/{databaseId}/backups/policy");
  if (match !== undefined) {
    const fullBackupCron = text(body.fullBackupCron);
    if (fullBackupCron === undefined) return problemResponse(400, "invalid_request", "fullBackupCron is required.");
    const previous = await get<Models.BackupPolicyResponse>(store, POLICIES, scopeKey(required(match!.projectId), required(match!.databaseId)));
    const policy: Models.BackupPolicyResponse = {
      backupPolicyId: previous?.backupPolicyId ?? (await store.nextUlid()) as BackupPolicyId,
      projectId: match!.projectId as ProjectId,
      databaseId: match!.databaseId as DatabaseId,
      databaseName: await databaseName(store, required(match!.databaseId)),
      fullBackupCron,
      incrementalBackupCron: text(body.incrementalBackupCron) ?? null,
      retentionDays: positiveInt(body.retentionDays, 30),
      disabled: body.disabled === true,
      destinationType: "LocalFolder",
      status: body.disabled === true ? "Disabled" : "Configured",
      createdAt: previous?.createdAt ?? context.now,
      updatedAt: context.now,
    };
    await store.put(POLICIES, scopeKey(required(match!.projectId), required(match!.databaseId)), policy);
    return jsonResponse(policy);
  }

  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/databases/{databaseId}/backups:plan");
  if (match !== undefined) {
    const operationSessionId = text(body.operationSessionId);
    if (operationSessionId === undefined) return problemResponse(400, "invalid_request", "operationSessionId is required.");
    const requestDigest = digest(required(match!.projectId), required(match!.databaseId), operationSessionId);
    const confirmationToken = randomUUID();
    const expiresAt = new Date(Date.parse(context.now) + 5 * 60_000).toISOString();
    await store.put(CONFIRMATIONS, confirmationToken, { projectId: required(match!.projectId), databaseId: required(match!.databaseId), operationSessionId, requestDigest, expiresAt } satisfies Confirmation);
    return jsonResponse({ action: "backup.trigger", projectId: required(match!.projectId) as ProjectId, databaseId: required(match!.databaseId) as DatabaseId, databaseName: await databaseName(store, required(match!.databaseId)), requestDigest, confirmationToken, expiresAt } satisfies Models.DatabaseBackupConfirmationPlanResponse);
  }

  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/databases/{databaseId}/backups:execute");
  if (match !== undefined) {
    const operationSessionId = text(body.operationSessionId);
    const confirmationToken = text(body.confirmationToken);
    const idempotencyKey = text(context.request.headers.get("idempotency-key"));
    if (operationSessionId === undefined || confirmationToken === undefined || idempotencyKey === undefined) return problemResponse(400, "invalid_request", "operationSessionId, confirmationToken, and idempotency-key are required.");
    const fingerprint = `${match!.projectId}:${match!.databaseId}:${operationSessionId}:${confirmationToken}`;
    const idempotencyScope = `${match!.projectId}:${match!.databaseId}:${ACTOR}:${idempotencyKey}`;
    const old = await get<Idempotency>(store, IDEMPOTENCY, idempotencyScope);
    if (old !== undefined) {
      if (old.fingerprint !== fingerprint) return problemResponse(409, "idempotency_key_reused", "The idempotency key was already used with a different request.");
      const backup = await get<Models.BackupJobResponse>(store, BACKUPS, old.resourceId);
      if (backup !== undefined) return jsonResponse(backup);
    }
    const confirmation = await get<Confirmation>(store, CONFIRMATIONS, confirmationToken);
    if (confirmation === undefined) return problemResponse(400, "confirmation_not_found", "The backup confirmation was not found.");
    if (Date.parse(confirmation.expiresAt) <= Date.parse(context.now)) return problemResponse(400, "confirmation_expired", "The backup confirmation has expired.");
    if (confirmation.projectId !== match!.projectId || confirmation.databaseId !== match!.databaseId || confirmation.operationSessionId !== operationSessionId || confirmation.requestDigest !== digest(match!.projectId, match!.databaseId, operationSessionId)) return problemResponse(400, "confirmation_mismatch", "The backup confirmation does not match this request.");
    const policy = await get<Models.BackupPolicyResponse>(store, POLICIES, scopeKey(match!.projectId, match!.databaseId));
    const now = context.now;
    const backupId = (await store.nextUlid()) as import("../../ids.js").BackupId;
    const backup: Models.BackupJobResponse = {
      backupId, projectId: match!.projectId as ProjectId, databaseId: match!.databaseId as DatabaseId, databaseName: await databaseName(store, match!.databaseId),
      policy: policy?.fullBackupCron ?? "manual", retention: `${policy?.retentionDays ?? 30} days`, status: "Completed", destinationType: policy?.destinationType ?? "LocalFolder", hasRestorePoint: true, requestedBy: ACTOR, requestedAt: now, updatedAt: now, completedAt: now, errorMessage: null,
    };
    await store.put(BACKUPS, backupId, backup);
    await store.put(RESTORE_POINTS, backupId, { name: `backup-${backupId}`, backupId, createdAt: now, isSnapshotRestore: true, isEncrypted: true, isIncremental: false, filesToRestore: 1 } satisfies Models.RestorePointResponse);
    await store.put(IDEMPOTENCY, idempotencyScope, { fingerprint, resourceId: backupId });
    await store.delete(CONFIRMATIONS, confirmationToken);
    return jsonResponse(backup, 201);
  }

  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/backups");
  if (match !== undefined) {
    const backups = (await records<Models.BackupJobResponse>(store, BACKUPS)).filter((item) => item.projectId === match!.projectId && item.databaseId === match!.databaseId).sort((a, b) => b.requestedAt.localeCompare(a.requestedAt));
    return jsonResponse(backups.slice(skip(context.url.searchParams.get("skip")), skip(context.url.searchParams.get("skip")) + take(context.url.searchParams.get("take"))));
  }
  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/backups/{backupId}");
  if (match !== undefined) {
    const backup = await get<Models.BackupJobResponse>(store, BACKUPS, required(match!.backupId));
    return backup === undefined || backup.projectId !== match!.projectId || backup.databaseId !== match!.databaseId ? notFound("backup") : jsonResponse(backup);
  }
  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/restore-points");
  if (match !== undefined) {
    const backups = (await records<Models.BackupJobResponse>(store, BACKUPS)).filter((item) => item.projectId === match!.projectId && item.databaseId === match!.databaseId);
    const points = (await records<Models.RestorePointResponse>(store, RESTORE_POINTS)).filter((point) => point.backupId !== undefined && backups.some((backup) => backup.backupId === point.backupId));
    return jsonResponse(points);
  }
  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/databases/{databaseId}/restores");
  if (match !== undefined) {
    const backupId = text(body.backupId);
    if (backupId === undefined) return problemResponse(400, "invalid_request", "backupId is required.");
    const backup = await get<Models.BackupJobResponse>(store, BACKUPS, backupId);
    if (backup === undefined || backup.projectId !== match!.projectId || backup.databaseId !== match!.databaseId) return notFound("backup");
    const idempotencyKey = text(body.idempotencyKey);
    const fingerprint = `${match!.projectId}:${match!.databaseId}:${backupId}`;
    const scope = idempotencyKey === undefined ? undefined : `${match!.projectId}:${match!.databaseId}:${ACTOR}:${idempotencyKey}`;
    if (scope !== undefined) {
      const old = await get<Idempotency>(store, RESTORE_IDEMPOTENCY, scope);
      if (old !== undefined) {
        if (old.fingerprint !== fingerprint) return problemResponse(409, "idempotency_key_reused", "The idempotency key was already used with a different request.");
        const restore = await get<Models.RestoreJobResponse>(store, RESTORES, old.resourceId);
        if (restore !== undefined) return jsonResponse(restore);
      }
    }
    const restoreId = (await store.nextUlid()) as import("../../ids.js").RestoreId;
    const now = context.now;
    const restore: Models.RestoreJobResponse = { restoreId, projectId: match!.projectId as ProjectId, databaseId: match!.databaseId as DatabaseId, backupId: backup.backupId, restoredDatabaseId: await store.nextUlid() as DatabaseId, restoredDatabaseName: `${backup.databaseName} restored`, status: "Completed", requestedBy: ACTOR, requestedAt: now, updatedAt: now, completedAt: now, errorMessage: null };
    await store.put(RESTORES, restoreId, restore);
    if (scope !== undefined) await store.put(RESTORE_IDEMPOTENCY, scope, { fingerprint, resourceId: restoreId });
    return jsonResponse(restore, 201);
  }
  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/restores/{restoreId}");
  if (match !== undefined) {
    const restore = await get<Models.RestoreJobResponse>(store, RESTORES, required(match!.restoreId));
    return restore === undefined || restore.projectId !== match!.projectId || restore.databaseId !== match!.databaseId ? notFound("restore") : jsonResponse(restore);
  }
  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/restores");
  if (match !== undefined) {
    const restores = (await records<Models.RestoreJobResponse>(store, RESTORES)).filter((item) => item.projectId === match!.projectId && item.databaseId === match!.databaseId).sort((a, b) => b.requestedAt.localeCompare(a.requestedAt));
    return jsonResponse(restores.slice(skip(context.url.searchParams.get("skip")), skip(context.url.searchParams.get("skip")) + take(context.url.searchParams.get("take"))));
  }
  return undefined;
};

async function databaseName(store: Store, databaseId: string): Promise<string> {
  const direct = await get<{ readonly name?: string }>(store, "databases", databaseId);
  if (direct?.name !== undefined) return direct.name;
  const all = await records<{ readonly databaseId?: string; readonly name?: string }>(store, "databases");
  return all.find((item) => item.databaseId === databaseId)?.name ?? `database-${databaseId}`;
}
async function get<T>(store: Store, namespace: string, key: string): Promise<T | undefined> { return await store.get(namespace, key) as T | undefined; }
async function records<T>(store: Store, namespace: string): Promise<T[]> { const value = await store.list(namespace); return Array.isArray(value) ? value as T[] : value !== null && typeof value === "object" ? Object.values(value) as T[] : []; }
function scopeKey(projectId: string, databaseId: string): string { return `${projectId}:${databaseId}`; }
function digest(projectId: string, databaseId: string, operationSessionId: string): string { return createHash("sha256").update(`${projectId}\n${databaseId}\n${operationSessionId.trim()}`, "utf8").digest("hex"); }
function text(value: unknown): string | undefined { return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined; }
function positiveInt(value: unknown, fallback: number): number { const number = typeof value === "string" && value.trim() !== "" ? Number(value) : typeof value === "number" ? value : fallback; return Number.isFinite(number) && number > 0 ? Math.floor(number) : fallback; }
function skip(value: string | null): number { return Math.max(0, positiveInt(value, 0)); }
function take(value: string | null): number { return Math.min(1000, Math.max(1, positiveInt(value, 100))); }
function notFound(resource: string): Response { return problemResponse(404, "not_found", `${resource} was not found.`); }
function required(value: string | undefined): string { return value ?? ""; }
