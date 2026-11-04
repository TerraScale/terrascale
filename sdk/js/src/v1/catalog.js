import { requireId } from "../ids.js";
import { array, boolean, malformed, object, string } from "./decode.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { stringifyExactJson } from "./json.js";
/**
 * @import { JsonValue } from "./json.js"
 * @import { V1Transport } from "./transport.js"
 * @import { V1RequestOptions } from "./terrabase-v1.js"
 */

/**
 * @typedef {object} V1ManagedDatabase
 * @property {string} id Canonical database ULID.
 * @property {string} projectId Canonical owning-project ULID.
 * @property {string} name Database name used in data routes.
 * @property {string | null | undefined} [previousName] Name lost in a cross-region conflict.
 * @property {string | null | undefined} [conflictOf] ULID of the database that kept that name.
 * @property {string | null} branchedFrom Source database ULID, or null.
 * @property {string} createdAt ISO date-time returned by the producer.
 * @property {string} region The region that answered.
 * @property {string} homeRegion The one region that stores and serves the database.
 * @property {readonly string[]} replicaRegions Sorted regions holding read replicas.
 * @property {boolean} allRegions Whether every serving region holds a replica.
 * @property {string | null | undefined} [expiresAt] RFC 3339 expiry, or null without a TTL.
 */
/** @typedef {{ readonly name: string; readonly homeRegion?: string | null | undefined; readonly replicaRegions?: readonly string[] | undefined; readonly allRegions?: boolean | undefined; readonly ttlSeconds?: number | undefined }} V1DatabaseCreateRequest */
/** @typedef {{ readonly name: string }} V1DatabaseBranchRequest */
/** @typedef {{ readonly after?: string | undefined; readonly limit?: number | undefined }} V1DatabaseListRequest */
/** @typedef {{ readonly items: readonly V1ManagedDatabase[]; readonly next: string | null }} V1DatabasePage */

/** @param {unknown} value @param {readonly string[]} allowed @returns {void} */
function members(value, allowed) {
  if (typeof value !== "object" || value === null || Array.isArray(value) ||
      ![Object.prototype, null].includes(Object.getPrototypeOf(value)))
    throw new TypeError("A catalog request must be a plain object.");
  for (const key of Object.keys(value))
    if (!allowed.includes(key)) throw new TypeError(`Unknown catalog request member ${key}.`);
}

/** @param {object} options @returns {void} */
function mutationOptions(options) {
  if ("idempotencyKey" in options)
    throw new TypeError("The current producer does not support Idempotency-Key; mutations are never automatically retried.");
}

/** @param {unknown} value @returns {string} */
export function region(value) {
  if (typeof value !== "string" || !/^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/u.test(value))
    throw new TypeError("A region is 1-63 lowercase letters, digits or hyphens, starts with a letter and cannot end with a hyphen.");
  return value;
}

/** @param {{ readonly replicaRegions?: unknown; readonly allRegions?: unknown }} placement @returns {{ replicaRegions?: string[]; allRegions?: boolean }} */
export function requirePlacement(placement) {
  if (placement.replicaRegions !== undefined && placement.allRegions !== undefined)
    throw new TypeError("Supply replicaRegions or allRegions, not both.");
  if (placement.allRegions !== undefined && typeof placement.allRegions !== "boolean")
    throw new TypeError("allRegions must be a boolean.");
  if (placement.replicaRegions === undefined) return placement.allRegions === undefined ? {} : { allRegions: placement.allRegions };
  if (!Array.isArray(placement.replicaRegions)) throw new TypeError("replicaRegions must be an array of regions.");
  return { replicaRegions: placement.replicaRegions.map(region) };
}

/** @param {JsonValue | undefined} value @returns {V1ManagedDatabase} */
export function decodeManagedDatabase(value) {
  const raw = object(value, "managed database");
  const createdAt = string(raw.createdAt, "database createdAt");
  if (!/^\d{4}-\d{2}-\d{2}T/u.test(createdAt) || !Number.isFinite(Date.parse(createdAt))) malformed("database createdAt");
  if (raw.expiresAt !== undefined && raw.expiresAt !== null &&
      (typeof raw.expiresAt !== "string" || !Number.isFinite(Date.parse(raw.expiresAt)))) malformed("database expiresAt");
  return Object.freeze({
    ...(raw.expiresAt === undefined ? {} : { expiresAt: raw.expiresAt }),
    id: requireId(raw.id, "Database ID"), projectId: requireId(raw.projectId, "Project ID"),
    name: requireName("database", raw.name),
    branchedFrom: raw.branchedFrom === null ? null : requireId(raw.branchedFrom, "Source database ID"),
    createdAt, region: region(raw.region), homeRegion: region(raw.homeRegion),
    replicaRegions: Object.freeze(raw.replicaRegions === undefined ? [] : array(raw.replicaRegions, "database replicaRegions").map(region)),
    allRegions: raw.allRegions === undefined ? false : boolean(raw.allRegions, "database allRegions"),
    ...(raw.previousName === undefined ? {} : { previousName: raw.previousName === null ? null : requireName("database", raw.previousName) }),
    ...(raw.conflictOf === undefined ? {} : { conflictOf: raw.conflictOf === null ? null : requireId(raw.conflictOf, "Conflict database ID") }),
  });
}

/** Public database catalog operations, bound to the transport's authorized project. */
export class V1Databases {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }

  /**
   * Creates a database in homeRegion, or the called region when omitted.
   * @param {V1DatabaseCreateRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>}
   */
  async create(request, options = {}) {
    members(request, ["name", "homeRegion", "replicaRegions", "allRegions", "ttlSeconds"]);
    const placement = requirePlacement(request);
    if (request.ttlSeconds !== undefined) requireTtlSeconds(request.ttlSeconds);
    mutationOptions(options);
    const name = requireName("database", request.name);
    if (request.homeRegion !== undefined && request.homeRegion !== null) region(request.homeRegion);
    const response = await this.#transport.call("createDatabase", { database: name }, {
      body: stringifyExactJson({ name,
        ...(request.homeRegion === undefined ? {} : { homeRegion: request.homeRegion }),
        ...placement,
        ...(request.ttlSeconds === undefined ? {} : { ttlSeconds: request.ttlSeconds }),
      }, "database create"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }

  /** @param {V1DatabaseListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1DatabasePage>} */
  async list(request = {}, options = {}) {
    members(request, ["after", "limit"]);
    const query = new URLSearchParams();
    if (request.after !== undefined) {
      if (typeof request.after !== "string") throw new TypeError("Database after must be a string.");
      query.set("after", request.after);
    }
    if (request.limit !== undefined) {
      if (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 100) throw new TypeError("Database list limit is 1-100.");
      query.set("limit", String(request.limit));
    }
    const response = await this.#transport.call("listDatabases", {}, { query, signal: options.signal });
    const raw = object(response.value, "database page");
    if (raw.next !== null && (typeof raw.next !== "string" || raw.next === "")) malformed("database next");
    return Object.freeze({ items: Object.freeze(array(raw.items, "databases").map(decodeManagedDatabase)), next: raw.next });
  }

  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>} */
  async get(name, options = {}) {
    const response = await this.#transport.call("getDatabase", { database: requireDatabaseSelector(name) }, { signal: options.signal });
    return decodeManagedDatabase(response.value);
  }

  /**
   * Rename by immutable ULID or current name with databases:write. See docs/database-rename.md.
   * Returns the database metadata committed by its home region.
   * ID, data, keys and region stay unchanged. Invalid names return 400 (or a
   * local TypeError); known same-region duplicates return 409. After convergence,
   * the old name has no alias and may be claimed by another database. Update
   * name-based connection strings and handles; ID-based strings stay valid. Reconcile uncertain failures before explicitly retrying.
   * @param {string} databaseId
   * @param {string} name
   * @param {V1RequestOptions} [options]
   * @returns {Promise<V1ManagedDatabase>}
   */
  async rename(databaseId, name, options = {}) {
    mutationOptions(options);
    const response = await this.#transport.call("updateDatabase", { database: requireDatabaseSelector(databaseId) }, {
      body: stringifyExactJson({ name: requireName("database", name) }, "database rename"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }

  /**
   * Deletes the database in its home region; branches remain independent.
   * @param {string} name @param {V1RequestOptions} [options] @returns {Promise<void>}
   */
  async delete(name, options = {}) {
    mutationOptions(options);
    await this.#transport.call("deleteDatabase", { database: requireDatabaseSelector(name) }, { signal: options.signal });
  }

  /** @param {string} name @param {V1DatabaseBranchRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>} */
  async branch(name, request, options = {}) {
    members(request, ["name"]);
    mutationOptions(options);
    const response = await this.#transport.call("branchDatabase", { database: requireDatabaseSelector(name) }, {
      body: stringifyExactJson({ name: requireName("database", request.name) }, "database branch"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }
}

/** @param {number} value @param {number} [maximum] @returns {number} */
export function requireTtlSeconds(value, maximum = 31_536_000) {
  if (!Number.isSafeInteger(value) || value < 60 || value > maximum) throw new TypeError(`ttlSeconds must be an integer from 60 to ${maximum}.`);
  return value;
}
