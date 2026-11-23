import { requireId } from "../ids.js";
import { array, boolean, malformed, object, string } from "./decode.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { ExactDecimal, stringifyExactJson } from "./json.js";
import { requireConsistency } from "./bookmark.js";
import { members, pageQuery } from "./management-validation.js";
/**
 * @import { JsonValue } from "./json.js"
 * @import { V1Transport } from "./transport.js"
 * @import { V1RequestOptions } from "./terrabase-v1.js"
 * @import { V1Consistency } from "./bookmark.js"
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
 * @property {readonly string[]} regions Every region that holds the database, sorted by region id. Every one accepts reads and writes; the order has no meaning.
 * @property {number | bigint} placementVersion Placement epoch; unchanged by an identical region set.
 * @property {boolean} allRegions Whether every serving region holds the database.
 * @property {string | null | undefined} [expiresAt] RFC 3339 expiry, or null without a TTL.
 * @property {V1Consistency} defaultConsistency Mode of SQL requests that name none.
 * @property {boolean} strict
 * @property {string} state
 */
/** @typedef {{ readonly name: string; readonly regions?: readonly string[] | undefined; readonly allRegions?: boolean | undefined; readonly ttlSeconds?: number | undefined; readonly defaultConsistency?: V1Consistency | undefined }} V1DatabaseCreateRequest */
/** @typedef {{ readonly name: string }} V1DatabaseBranchRequest */
/** @typedef {{ readonly after?: string | undefined; readonly limit?: number | undefined }} V1DatabaseListRequest */
/** @typedef {{ readonly items: readonly V1ManagedDatabase[]; readonly next: string | null }} V1DatabasePage */

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
  if (!Array.isArray(placement.regions) || (placement.regions.length < 1 || placement.regions.length > 9)) throw new TypeError("regions must be an array of one to nine regions.");
  const regions = placement.regions.map(region);
  if (new Set(regions).size !== regions.length) throw new TypeError("regions must not contain duplicates.");
  return { regions };
}

/** @param {JsonValue | undefined} value @returns {V1ManagedDatabase} */
export function decodeManagedDatabase(value) {
  const raw = object(value, "managed database");
  const createdAt = string(raw.createdAt, "database createdAt");
  if (!/^\d{4}-\d{2}-\d{2}T/u.test(createdAt) || !Number.isFinite(Date.parse(createdAt))) malformed("database createdAt");
  if (raw.expiresAt != null &&
      (typeof raw.expiresAt !== "string" || !Number.isFinite(Date.parse(raw.expiresAt)))) malformed("database expiresAt");
  const defaultConsistency = raw.defaultConsistency === "fast" || raw.defaultConsistency === "strong" ? raw.defaultConsistency : malformed("database defaultConsistency");
  const version = raw.placementVersion;
  const placementVersion = typeof version === "number" && Number.isSafeInteger(version) ? version
    : version instanceof ExactDecimal ? version.toBigInt() : malformed("database placementVersion");
  return Object.freeze({
    ...(raw.expiresAt === undefined ? {} : { expiresAt: raw.expiresAt }),
    id: requireId(raw.id, "Database ID"), projectId: requireId(raw.projectId, "Project ID"),
    name: requireName("database", raw.name),
    branchedFrom: raw.branchedFrom == null ? null : requireId(raw.branchedFrom, "Source database ID"),
    createdAt, region: region(raw.region),
    regions: Object.freeze((raw.regions === undefined ? [] : array(raw.regions, "database regions").map(region)).toSorted()),
    placementVersion,
    allRegions: raw.allRegions === undefined ? false : boolean(raw.allRegions, "database allRegions"),
    defaultConsistency, strict: boolean(raw.strict, "database strict"), state: string(raw.state, "database state"),
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
   * Creates a database in the named peer regions, or the called region when omitted.
   * @param {V1DatabaseCreateRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>}
   */
  async create(request, options = {}) {
    members(request, ["name", "regions", "allRegions", "ttlSeconds", "defaultConsistency"]);
    const placement = requirePlacement(request);
    if (request.ttlSeconds !== undefined) requireTtlSeconds(request.ttlSeconds);
    const defaultConsistency = requireConsistency(request.defaultConsistency);
    const name = requireName("database", request.name);
    const response = await this.#transport.call("createDatabase", { database: name }, {
      body: stringifyExactJson({ name,
        ...placement,
        ...(request.ttlSeconds === undefined ? {} : { ttlSeconds: request.ttlSeconds }),
        ...(defaultConsistency === undefined ? {} : { defaultConsistency }),
      }, "database create"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }

  /** @param {V1DatabaseListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1DatabasePage>} */
  async list(request = {}, options = {}) {
    const response = await this.#transport.call("listDatabases", {}, { query: pageQuery(request), signal: options.signal });
    const raw = object(response.value, "database page");
    const next = raw.next ?? null;
    if (next !== null && (typeof next !== "string" || next === "")) malformed("database next");
    return Object.freeze({ items: Object.freeze(array(raw.items, "databases").map(decodeManagedDatabase)), next });
  }

  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>} */
  async get(name, options = {}) {
    const response = await this.#transport.call("getDatabase", { database: requireDatabaseSelector(name) }, { signal: options.signal });
    return decodeManagedDatabase(response.value);
  }

  /**
   * Rename by immutable ULID or current name with databases:write. See docs/database-rename.md.
   * Returns the committed database metadata.
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
    const response = await this.#transport.call("updateDatabase", { database: requireDatabaseSelector(databaseId) }, {
      body: stringifyExactJson({ name: requireName("database", name) }, "database rename"), signal: options.signal,
    });
    return decodeManagedDatabase(response.value);
  }

  /**
   * Deletes the database in every region; branches remain independent.
   * @param {string} name @param {V1RequestOptions} [options] @returns {Promise<void>}
   */
  async delete(name, options = {}) {
    await this.#transport.call("deleteDatabase", { database: requireDatabaseSelector(name) }, { signal: options.signal });
  }

  /**
   * Deletes the database only while its immutable ID is still expectedId; a changed identity answers 409.
   * @param {string} name @param {string} expectedId @param {V1RequestOptions} [options] @returns {Promise<void>}
   */
  async deleteIfId(name, expectedId, options = {}) {
    await this.#transport.call("deleteDatabaseIfId", { database: requireDatabaseSelector(name), expectedId: requireId(expectedId) },
      { signal: options.signal });
  }

  /** @param {string} name @param {V1DatabaseBranchRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>} */
  async branch(name, request, options = {}) {
    members(request, ["name"]);
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
