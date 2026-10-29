import { requireId } from "../ids.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { flag, list, nullable, numeric, optional, record, text } from "./management-decode.js";
import { members, mutationOptions, pageQuery } from "./management-validation.js";
import { malformed, object } from "./decode.js";
import { stringifyExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
const backup = record({ id: requireId, databaseId: requireId, status: text, trigger: text, sizeBytes: numeric, createdAt: text });
const backupPage = record({ items: list(backup), next: nullable(text) });
const policy = record({ fullBackupCron: nullable(text), retentionDays: numeric, disabled: flag, updatedAt: nullable(text) });
const window = record({ earliest: text, latest: text });
const plan = record({ window, pointInTime: text, snapshotAt: text, latestSegmentAt: nullable(text), segmentCount: numeric, historyCheck: text });
const restored = record({ id: requireId, projectId: requireId, name: text, branchedFrom: nullable(requireId), createdAt: text,
  homeRegion: optional(nullable(text)), replicaRegions: optional(nullable(list(text))), placementVersion: optional(numeric), replicas: list(text) });
const destination = record({ endpoint: text, region: text, bucket: text, mode: text, updatedAt: text });
/** @typedef {ReturnType<typeof backup>} V1Backup */
/** @typedef {ReturnType<typeof backupPage>} V1BackupPage */
/** @typedef {ReturnType<typeof policy>} V1BackupPolicy */
/** @typedef {ReturnType<typeof window>} V1RestoreWindow */
/** @typedef {ReturnType<typeof plan>} V1PointInTimePlan */
/** @typedef {ReturnType<typeof restored>} V1RestoredDatabase */
/** @typedef {ReturnType<typeof destination>} V1BackupDestination */
/** @typedef {{ readonly fullBackupCron?: string | null; readonly retentionDays?: number | null; readonly disabled?: boolean | null }} V1BackupPolicyRequest */
/** @typedef {{ readonly pointInTime: string; readonly destination?: "platform" | "customer" | null }} V1PointInTimeRequest */
/** @typedef {V1PointInTimeRequest & { readonly name?: string | null }} V1PointInTimeRestoreRequest */
/** @typedef {{ readonly endpoint: string; readonly region: string; readonly bucket: string; readonly accessKeyId: string; readonly secretKey: string; readonly mode: "both" | "customer" }} V1BackupDestinationRequest */
/** @param {unknown} value @returns {"platform" | "customer"} */
function source(value) {
  if (value !== "platform" && value !== "customer") throw new TypeError("destination must be platform or customer.");
  return value;
}
/** @param {V1PointInTimeRequest} request */
function pointInTime(request) {
  if (typeof request.pointInTime !== "string" || !Number.isFinite(Date.parse(request.pointInTime))) throw new TypeError("pointInTime must be an RFC 3339 instant.");
  if (request.destination !== undefined && request.destination !== null) source(request.destination);
}

/** Backups and explicit point-in-time restore sources for one database. */
export class V1Backups {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1Backup>} */
  async create(options = {}) {
    mutationOptions(options);
    return backup((await this.#transport.call("createBackup", this.#params, { signal: options.signal })).value);
  }
  /** @param {import("./catalog.js").V1DatabaseListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1BackupPage>} */
  async list(request = {}, options = {}) { return backupPage((await this.#transport.call("listBackups", this.#params, { query: pageQuery(request), signal: options.signal })).value); }
  /** @param {string} id @param {{readonly name?: string | null}} [request] @param {V1RequestOptions} [options] @returns {Promise<V1RestoredDatabase>} */
  async restore(id, request = {}, options = {}) {
    members(request, ["name"]);
    if (request.name !== undefined && request.name !== null) requireName("database", request.name);
    mutationOptions(options);
    return restored((await this.#transport.call("restoreBackup", { ...this.#params, backup: requireId(id) }, {
      body: stringifyExactJson(request, "restore"), signal: options.signal,
    })).value);
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1BackupPolicy>} */
  async getPolicy(options = {}) { return policy((await this.#transport.call("getBackupPolicy", this.#params, { signal: options.signal })).value); }
  /** @param {V1BackupPolicyRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1BackupPolicy>} */
  async setPolicy(request, options = {}) {
    members(request, ["fullBackupCron", "retentionDays", "disabled"]);
    mutationOptions(options);
    return policy((await this.#transport.call("setBackupPolicy", this.#params, { body: stringifyExactJson(request, "backup policy"), signal: options.signal })).value);
  }
  /** @param {{readonly destination?: "platform" | "customer"}} [request] @param {V1RequestOptions} [options] @returns {Promise<V1RestoreWindow>} */
  async window(request = {}, options = {}) {
    members(request, ["destination"]);
    const query = new URLSearchParams();
    if (request.destination !== undefined) query.set("destination", source(request.destination));
    return window((await this.#transport.call("getRestoreWindow", this.#params, { query, signal: options.signal })).value);
  }
  /** @param {V1PointInTimeRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1PointInTimePlan>} */
  async plan(request, options = {}) {
    members(request, ["pointInTime", "destination"]);
    pointInTime(request);
    return plan((await this.#transport.call("planPointInTime", this.#params, { body: stringifyExactJson(request, "restore plan"), signal: options.signal })).value);
  }
  /** @param {V1PointInTimeRestoreRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1RestoredDatabase>} */
  async restorePointInTime(request, options = {}) {
    members(request, ["pointInTime", "destination", "name"]);
    pointInTime(request);
    if (request.name !== undefined && request.name !== null) requireName("database", request.name);
    mutationOptions(options);
    return restored((await this.#transport.call("restorePointInTime", this.#params, { body: stringifyExactJson(request, "point-in-time restore"), signal: options.signal })).value);
  }
}
/** Write-only destination credentials are sent only to the configured API, never retained in the client. */
export class V1BackupDestinationClient {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1BackupDestination>} */
  async get(options = {}) { return destination((await this.#transport.call("getBackupDestination", {}, { signal: options.signal })).value); }
  /** @param {V1BackupDestinationRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1BackupDestination>} */
  async set(request, options = {}) {
    members(request, ["endpoint", "region", "bucket", "accessKeyId", "secretKey", "mode"]);
    mutationOptions(options);
    const raw = object((await this.#transport.call("setBackupDestination", {}, { body: stringifyExactJson(request, "backup destination"), signal: options.signal })).value, "backup destination");
    if (raw.accessKeyId !== undefined || raw.secretKey !== undefined) malformed("write-only destination credentials");
    return destination(raw);
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<void>} */
  async delete(options = {}) { mutationOptions(options); await this.#transport.call("deleteBackupDestination", {}, { signal: options.signal }); }
}
