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
 * @property {readonly string[]} regions Every region that holds the database, sorted by region id. The order has no meaning.
 * @property {boolean} allRegions Whether every serving region holds the database.
 * @property {string | null | undefined} [expiresAt] RFC 3339 expiry, or null without a TTL.
 */
/** @typedef {{ readonly name: string; readonly regions?: readonly string[] | undefined; readonly allRegions?: boolean | undefined; readonly ttlSeconds?: number | undefined }} V1DatabaseCreateRequest */
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

/** @param {{ readonly regions?: unknown; readonly allRegions?: unknown }} placement @returns {{ regions?: string[]; allRegions?: boolean }} */
export function requirePlacement(placement) {
  if (placement.regions !== undefined && placement.allRegions !== undefined)
    throw new TypeError("Supply regions or allRegions, not both.");
  if (placement.allRegions !== undefined && typeof placement.allRegions !== "boolean")
    throw new TypeError("allRegions must be a boolean.");
  if (placement.regions === undefined) return placement.allRegions === undefined ? {} : { allRegions: placement.allRegions };
  if (!Array.isArray(placement.regions) || placement.regions.length === 0) throw new TypeError("regions must be an array of at least one region.");
  const regions = placement.regions.map(region);
  if (new Set(regions).size !== regions.length) throw new TypeError("regions must not contain duplicates.");
  return { regions };
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
    createdAt, region: region(raw.region),
    regions: Object.freeze((raw.regions === undefined ? [] : array(raw.regions, "database regions").map(region)).toSorted()),
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
   * Creates a database in the named regions, or the called region when omitted.
   * @param {V1DatabaseCreateRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>}
   */
  async create(request, options = {}) {
    members(request, ["name", "regions", "allRegions", "ttlSeconds"]);
    const placement = requirePlacement(request);
    if (request.ttlSeconds !== undefined) requireTtlSeconds(request.ttlSeconds);
    mutationOptions(options);
    const name = requireName("database", request.name);
    const response = await this.#transport.call("createDatabase", { database: name }, {
      body: stringifyExactJson({ name,
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

  /**
   * Deletes the database only while its immutable ID is still expectedId; a changed identity answers 409.
   * @param {string} name @param {string} expectedId @param {V1RequestOptions} [options] @returns {Promise<void>}
   */
  async deleteIfId(name, expectedId, options = {}) {
    mutationOptions(options);
    await this.#transport.call("deleteDatabaseIfId", { database: requireDatabaseSelector(name), expectedId: requireId(expectedId) },
      { signal: options.signal });
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

  /**
   * Branches a snapshot copy only while the source's immutable ID is still expectedId; a changed identity answers 409.
   * @param {string} name @param {V1DatabaseBranchRequest} request @param {string} expectedId @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>}
   */
  async branchIfId(name, request, expectedId, options = {}) {
    members(request, ["name"]);
    mutationOptions(options);
    const response = await this.#transport.call("branchDatabaseIfId",
      { database: requireDatabaseSelector(name), expectedId: requireId(expectedId) }, {
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
